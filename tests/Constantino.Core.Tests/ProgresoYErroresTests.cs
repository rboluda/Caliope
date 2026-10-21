using System.Net;
using System.Security.Cryptography;
using System.Text;
using Constantino.Core.Catalogo;
using Constantino.Core.Instalaciones;

namespace Constantino.Core.Tests;

public class MedidorTests
{
    [Fact]
    public void Calcula_velocidad_y_tiempo_restante()
    {
        var m = new Medidor();
        m.Anotar(0, 1000L << 20, TimeSpan.Zero);
        m.Anotar(100L << 20, 1000L << 20, TimeSpan.FromSeconds(10));

        Assert.Equal(10L << 20, m.BytesPorSegundo!.Value, 1);
        Assert.Equal(90, m.Restante!.Value.TotalSeconds, 1);
        Assert.Equal("Descargando · 100 MB de ~1000 MB · 10 MB/s · quedan ~2 min",
            m.Texto("Descargando", 100L << 20, 1000L << 20, aproximado: true));
    }

    [Fact]
    public void Sin_tiempo_suficiente_no_inventa_la_velocidad()
    {
        var m = new Medidor();
        m.Anotar(0, 100, TimeSpan.Zero);
        m.Anotar(50, 100, TimeSpan.FromMilliseconds(300));

        Assert.Null(m.BytesPorSegundo);
        Assert.Equal("Bajando · 0 KB de 0 KB", m.Texto("Bajando", 50, 100));
    }

    [Fact]
    public void La_ventana_olvida_las_muestras_antiguas()
    {
        var m = new Medidor();
        m.Anotar(0, null, TimeSpan.Zero);
        m.Anotar(1000L << 20, null, TimeSpan.FromSeconds(5)); // ráfaga rápida al principio
        m.Anotar(1001L << 20, null, TimeSpan.FromSeconds(30));
        m.Anotar(1002L << 20, null, TimeSpan.FromSeconds(31));
        m.Anotar(1003L << 20, null, TimeSpan.FromSeconds(32));

        Assert.True(m.BytesPorSegundo < 2L << 20, "la velocidad refleja solo los últimos segundos");
    }

    [Theory]
    [InlineData(5, "casi listo")]
    [InlineData(42, "quedan ~50 s")]
    [InlineData(150, "quedan ~3 min")]
    [InlineData(4000, "quedan ~1 h 6 min")]
    public void Formatea_el_tiempo_restante(int segundos, string esperado)
    {
        Assert.Equal(esperado, Medidor.TiempoRestante(TimeSpan.FromSeconds(segundos)));
    }

    [Theory]
    [InlineData(512L * 1024, "512 KB")]
    [InlineData(5L << 20, "5,0 MB")]
    [InlineData(250L << 20, "250 MB")]
    [InlineData(4530000000L, "4,2 GB")]
    public void Formatea_tamanos(long bytes, string esperado)
    {
        Assert.Equal(esperado, Medidor.Tamano(bytes));
    }
}

public class DiagnosticoErroresTests
{
    [Theory]
    [InlineData("requests.exceptions.ChunkedEncodingError: ('Connection broken: IncompleteRead(275000568 bytes read)')", "conexión")]
    [InlineData("error: Failed to download `torch==2.11.0`", "conexión")]
    [InlineData("OSError: [Errno 28] No space left on device", "espacio")]
    [InlineData("PermissionError: [Errno 13] Permission denied: 'C:\\\\x'", "permiso")]
    [InlineData("dispositivo=cpu", "NVIDIA")]
    public void Reconoce_causas_conocidas(string salida, string clave)
    {
        Assert.Contains(clave, DiagnosticoErrores.Explicar("bla bla\n" + salida + "\n"));
    }

    [Fact]
    public void Si_no_reconoce_la_causa_muestra_la_ultima_linea_de_error()
    {
        var texto = DiagnosticoErrores.Explicar("Traceback\n  File x\nValueError: algo raro\n");

        Assert.Equal("ha fallado (ValueError: algo raro).", texto);
    }

    [Fact]
    public void Sin_pistas_remite_al_registro()
    {
        Assert.Contains("registro", DiagnosticoErrores.Explicar("nada útil"));
    }
}

public sealed class AvisosYReintentosTests : IDisposable
{
    private readonly string _datos = Path.Combine(Path.GetTempPath(), "constantino-pruebas", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_datos))
            Directory.Delete(_datos, recursive: true);
    }

    [Fact]
    public async Task Los_avisos_del_proceso_y_el_tamano_de_carpeta_llegan_al_progreso()
    {
        var catalogo = CatalogoComponentes.Leer("""
            { "version": 1, "componentes": [
              { "id": "m", "nombre": "M", "grupo": "G", "necesidad": "opcional", "resumen": "r", "detalle": "d",
                "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {},
                "instalacion": { "version": "1",
                  "pasos": [ { "tipo": "ejecutar", "descripcion": "Instalando", "programa": "{uv}", "argumentos": [],
                               "progreso": { "carpetas": ["{cache}/x"], "bytesConGpu": 1000, "bytesSinGpu": 1000 } } ],
                  "rutas": ["{cache}/x"], "comprobar": ["{cache}/x"] } } ] }
            """);
        var carpeta = Path.Combine(new RutasDatos(_datos, true).Cache, "x");
        var ejecutor = new EjecutorConAccion(async escribir =>
        {
            Directory.CreateDirectory(carpeta);
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "a"), new byte[600]);
            escribir("AVISO: reintentando (2 de 5)");
            await Task.Delay(150);
        });
        var eventos = new List<EventoInstalacion>();
        var instalador = new Instalador(catalogo, new RutasDatos(_datos, true), ejecutor, new DescargadorNulo(), TimeSpan.FromMilliseconds(10));

        await instalador.InstalarAsync(["m"], new ProgresoSincrono<EventoInstalacion>(e => { lock (eventos) eventos.Add(e); }), CancellationToken.None);

        List<EventoInstalacion> progreso;
        lock (eventos)
            progreso = eventos.Where(e => e.Tipo == TipoEvento.Progreso).ToList();
        Assert.Contains(progreso, e => e.Fraccion is >= 0.5 and < 1 && e.Mensaje.Contains("de ~"));
        Assert.Contains(progreso, e => e.Mensaje.Contains("reintentando (2 de 5)"));
        Assert.Equal(1, progreso[^1].Fraccion);
    }

    [Fact]
    public async Task El_descargador_reintenta_si_se_corta_la_conexion()
    {
        var contenido = Encoding.UTF8.GetBytes(new string('x', 200_000));
        var sha = Convert.ToHexStringLower(SHA256.HashData(contenido));
        var servidor = new ServidorQueFallaUnaVez(contenido);
        var destino = Path.Combine(_datos, "f.bin");

        await new Descargador(new HttpClient(servidor), intentos: 3, esperaBase: TimeSpan.Zero)
            .DescargarAsync("https://x/f.bin", destino, sha, new ProgresoSincrono<(long, long?)>(_ => { }), CancellationToken.None);

        Assert.Equal(2, servidor.Peticiones);
        Assert.Equal(contenido, await File.ReadAllBytesAsync(destino));
    }

    [Fact]
    public async Task El_descargador_no_reintenta_un_archivo_corrupto()
    {
        var servidor = new ServidorQueFallaUnaVez(Encoding.UTF8.GetBytes("hola"), fallar: false);

        await Assert.ThrowsAsync<DescargaCorruptaException>(() => new Descargador(new HttpClient(servidor), intentos: 3, esperaBase: TimeSpan.Zero)
            .DescargarAsync("https://x/f.bin", Path.Combine(_datos, "f.bin"), new string('0', 64),
                new ProgresoSincrono<(long, long?)>(_ => { }), CancellationToken.None));
        Assert.Equal(1, servidor.Peticiones);
    }

    private sealed class EjecutorConAccion(Func<Action<string>, Task> accion) : IEjecutorProcesos
    {
        public async Task<ResultadoProceso> EjecutarAsync(string programa, IReadOnlyList<string> argumentos,
            IReadOnlyDictionary<string, string> entorno, Action<string> alEscribir, CancellationToken ct)
        {
            await accion(alEscribir);
            return new ResultadoProceso(0, "");
        }
    }

    private sealed class DescargadorNulo : IDescargador
    {
        public Task DescargarAsync(string url, string destino, string sha256, IProgress<(long Recibidos, long? Total)> progreso, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ServidorQueFallaUnaVez(byte[] contenido, bool fallar = true) : HttpMessageHandler
    {
        public int Peticiones { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // La consulta previa (¿admite rangos?) no cuenta: este servidor no los admite.
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            Peticiones++;
            if (fallar && Peticiones == 1)
                throw new HttpRequestException("Conexión cortada");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contenido) });
        }
    }
}
