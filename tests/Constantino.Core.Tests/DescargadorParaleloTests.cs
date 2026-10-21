using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Constantino.Core.Instalaciones;

namespace Constantino.Core.Tests;

public sealed class DescargadorParaleloTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "constantino-descargas-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Contenido = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7 % 251)).ToArray();
    private static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(Contenido));

    public DescargadorParaleloTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch (IOException) { }
    }

    private string Destino => Path.Combine(_carpeta, "f.bin");

    private static Descargador Descargador(Servidor s, int conexiones = 4) =>
        new(new HttpClient(s), intentos: 4, esperaBase: TimeSpan.Zero, sinDatos: TimeSpan.FromMilliseconds(300),
            conexiones: conexiones, tramoMinimo: 10_000);

    private static readonly IProgress<(long, long?)> SinProgreso = new ProgresoSincrono<(long, long?)>(_ => { });

    [Fact]
    public async Task Baja_por_tramos_con_varias_conexiones_y_los_une()
    {
        var s = new Servidor(Contenido);

        await Descargador(s).DescargarAsync("https://x/f.bin", Destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(Contenido, await File.ReadAllBytesAsync(Destino));
        Assert.Equal(4, s.Rangos);
        Assert.Equal(["f.bin"], Directory.EnumerateFiles(_carpeta).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Una_conexion_que_se_queda_callada_se_corta_y_se_reanuda()
    {
        var s = new Servidor(Contenido) { CallarseUnaVezTras = 3_000 };

        await Descargador(s).DescargarAsync("https://x/f.bin", Destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(Contenido, await File.ReadAllBytesAsync(Destino));
        Assert.True(s.Rangos > 4, "el tramo que se calló debe pedirse otra vez, desde donde iba");
    }

    [Fact]
    public async Task Con_una_sola_conexion_tambien_se_reanuda_si_se_calla()
    {
        var s = new Servidor(Contenido, admiteRangos: false) { CallarseUnaVezTras = 3_000 };

        await Descargador(s).DescargarAsync("https://x/f.bin", Destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(Contenido, await File.ReadAllBytesAsync(Destino));
    }

    [Fact]
    public async Task Si_el_servidor_ignora_los_rangos_baja_con_una_conexion()
    {
        var s = new Servidor(Contenido, anunciaRangosPeroLosIgnora: true);

        await Descargador(s).DescargarAsync("https://x/f.bin", Destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(Contenido, await File.ReadAllBytesAsync(Destino));
        Assert.Equal(["f.bin"], Directory.EnumerateFiles(_carpeta).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Continua_una_descarga_a_medias_de_una_version_anterior()
    {
        await File.WriteAllBytesAsync(Destino + ".part", Contenido[..40_000]);
        var s = new Servidor(Contenido);

        await Descargador(s).DescargarAsync("https://x/f.bin", Destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(Contenido, await File.ReadAllBytesAsync(Destino));
        Assert.Equal(60_000, s.BytesServidos);
    }

    /// <summary>Servidor falso con HEAD, rangos y la posibilidad de dejar una conexión colgada.</summary>
    private sealed class Servidor(byte[] contenido, bool admiteRangos = true, bool anunciaRangosPeroLosIgnora = false) : HttpMessageHandler
    {
        private int _rangos;
        private long _servidos;
        public int Rangos => _rangos;
        public long BytesServidos => Interlocked.Read(ref _servidos);
        public int CallarseUnaVezTras { get; set; } = -1;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var anuncia = admiteRangos || anunciaRangosPeroLosIgnora;
            if (request.Method == HttpMethod.Head)
            {
                var cabecera = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                cabecera.Content.Headers.ContentLength = contenido.Length;
                if (anuncia)
                    cabecera.Headers.AcceptRanges.Add("bytes");
                return Task.FromResult(cabecera);
            }

            var (desde, hasta) = (0L, contenido.LongLength - 1);
            var parcial = false;
            if (request.Headers.Range?.Ranges.Single() is { } r && !anunciaRangosPeroLosIgnora)
            {
                Interlocked.Increment(ref _rangos);
                (desde, hasta, parcial) = (r.From ?? 0, r.To ?? contenido.LongLength - 1, true);
            }
            var trozo = contenido[(int)desde..(int)(hasta + 1)];
            Interlocked.Add(ref _servidos, trozo.Length);
            var callarseTras = CallarseUnaVezTras;
            if (callarseTras >= 0 && trozo.Length > callarseTras)
                CallarseUnaVezTras = -1; // solo la primera conexión que llegue tan lejos
            else
                callarseTras = -1;
            var respuesta = new HttpResponseMessage(parcial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(new FlujoQueSeCalla(trozo, callarseTras)),
            };
            respuesta.Content.Headers.ContentLength = trozo.Length;
            if (parcial)
                respuesta.Content.Headers.ContentRange = new ContentRangeHeaderValue(desde, hasta, contenido.Length);
            return Task.FromResult(respuesta);
        }
    }

    /// <summary>Entrega los datos y, si se indica, se queda colgado sin cerrar a partir de cierta posición.</summary>
    private sealed class FlujoQueSeCalla(byte[] datos, int callarseTras) : MemoryStream(datos, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (callarseTras >= 0 && Position >= callarseTras)
                await Task.Delay(Timeout.Infinite, ct);
            var limite = callarseTras >= 0 ? (int)Math.Min(buffer.Length, callarseTras - Position) : buffer.Length;
            return await base.ReadAsync(buffer[..limite], ct);
        }
    }
}
