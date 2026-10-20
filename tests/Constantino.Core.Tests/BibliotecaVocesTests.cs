using System.Text;
using Constantino.Core.Instalaciones;
using Constantino.Core.Voces;

namespace Constantino.Core.Tests;

public sealed class BibliotecaVocesTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "constantino-voces", Guid.NewGuid().ToString("N"));
    private readonly string _ffmpeg;
    private readonly string _audio;
    private readonly FfmpegFalso _ejecutor = new();

    public BibliotecaVocesTests()
    {
        Directory.CreateDirectory(_raiz);
        _ffmpeg = Path.Combine(_raiz, "ffmpeg.exe");
        File.WriteAllText(_ffmpeg, "");
        _audio = Path.Combine(_raiz, "grabacion.mp3");
        File.WriteAllText(_audio, "audio");
    }

    public void Dispose() => Directory.Delete(_raiz, recursive: true);

    private BibliotecaVoces Biblioteca() => new(Path.Combine(_raiz, "voces"), _ffmpeg, _ejecutor);

    private static SolicitudImportacion Solicitud(string archivo, string nombre = "Narrador de la hoguera", bool consentimiento = true,
        string? transcripcion = null, double segundos = 15) =>
        new(archivo, nombre, "Spanish", transcripcion ?? Transcripcion(segundos), consentimiento);

    /// <summary>Un texto con las letras que se dirían, a ritmo normal, en <paramref name="segundos"/>.</summary>
    private static string Transcripcion(double segundos) =>
        string.Join(' ', Enumerable.Repeat("En la puerta hay una chica empapada.", (int)Math.Ceiling(segundos * 13 / 29)));

    [Fact]
    public async Task Importa_convirtiendo_a_wav_de_24_kHz_y_guarda_la_ficha()
    {
        _ejecutor.Segundos = 17.6;
        var bib = Biblioteca();

        var texto = Transcripcion(17.6);
        var r = await bib.ImportarAsync(Solicitud(_audio) with { Transcripcion = "  " + texto.Replace(" ", "\n  ") + "  " });

        Assert.Equal("narrador-de-la-hoguera", r.Voz.Id);
        Assert.Equal(17.6, r.Voz.DuracionSegundos, 2);
        Assert.Equal(texto, r.Voz.Transcripcion);
        Assert.False(r.Voz.SoloTimbre);
        Assert.Null(r.Aviso);
        Assert.Contains("-ar 24000", _ejecutor.Ultima);
        Assert.Equal(r.Voz with { Creada = default }, bib.Obtener(r.Voz.Id)! with { Creada = default });
        Assert.Single(bib.Listar());
    }

    [Fact]
    public async Task Nombres_repetidos_no_pisan_otra_voz()
    {
        var bib = Biblioteca();
        var a = await bib.ImportarAsync(Solicitud(_audio, "Ábuelo Ñoño"));
        var b = await bib.ImportarAsync(Solicitud(_audio, "Ábuelo Ñoño"));

        Assert.Equal("abuelo-nono", a.Voz.Id);
        Assert.Equal("abuelo-nono-2", b.Voz.Id);
        Assert.Equal(2, bib.Listar().Count);
    }

    [Theory]
    [InlineData(1.5, false)]
    [InlineData(5, true)]
    [InlineData(40, true)]
    [InlineData(75, false)]
    public async Task Valida_la_duracion(double segundos, bool valida)
    {
        _ejecutor.Segundos = segundos;
        var bib = Biblioteca();

        if (valida)
        {
            var r = await bib.ImportarAsync(Solicitud(_audio, segundos: segundos));
            Assert.NotNull(r.Aviso); // 5 s y 40 s funcionan, pero fuera de lo ideal
        }
        else
        {
            var e = await Assert.ThrowsAsync<VozInvalidaException>(() => bib.ImportarAsync(Solicitud(_audio, segundos: segundos)));
            Assert.Contains("tiene que durar", e.Message);
            Assert.Empty(Directory.EnumerateDirectories(bib.Carpeta)); // no deja carpetas a medias
        }
    }

    [Fact]
    public async Task Exige_consentimiento_transcripcion_y_nombre()
    {
        var bib = Biblioteca();
        await Assert.ThrowsAsync<VozInvalidaException>(() => bib.ImportarAsync(Solicitud(_audio, consentimiento: false)));
        await Assert.ThrowsAsync<VozInvalidaException>(() => bib.ImportarAsync(Solicitud(_audio, transcripcion: "hola")));
        await Assert.ThrowsAsync<VozInvalidaException>(() => bib.ImportarAsync(Solicitud(_audio, nombre: "  ")));
        await Assert.ThrowsAsync<VozInvalidaException>(() => bib.ImportarAsync(Solicitud(Path.Combine(_raiz, "no-existe.wav"))));
        Assert.Empty(bib.Listar());
    }

    [Fact]
    public async Task Rechaza_una_transcripcion_que_no_encaja_con_la_duracion()
    {
        _ejecutor.Segundos = 20.5;
        var bib = Biblioteca();

        var corta = await Assert.ThrowsAsync<VozInvalidaException>(() => bib.ImportarAsync(Solicitud(_audio, transcripcion: "Jordi pruebas")));
        Assert.Contains("no parece corresponder", corta.Message);
        Assert.Contains("unas 267 letras y has escrito 12", corta.Message);

        var larga = await Assert.ThrowsAsync<VozInvalidaException>(() => bib.ImportarAsync(Solicitud(_audio, transcripcion: Transcripcion(60))));
        Assert.Contains("demasiado larga", larga.Message);
        Assert.Empty(bib.Listar());
    }

    [Fact]
    public async Task Sin_transcripcion_imita_solo_el_timbre()
    {
        var bib = Biblioteca();

        var r = await bib.ImportarAsync(Solicitud(_audio, transcripcion: "") with { SinTranscripcion = true });

        Assert.True(r.Voz.SoloTimbre);
        Assert.Equal("", bib.Obtener(r.Voz.Id)!.Transcripcion);
    }

    [Fact]
    public async Task Corregir_la_transcripcion_valida_y_admite_dejarla_vacia()
    {
        _ejecutor.Segundos = 20;
        var bib = Biblioteca();
        var voz = (await bib.ImportarAsync(Solicitud(_audio, segundos: 20))).Voz;

        Assert.Throws<VozInvalidaException>(() => bib.CambiarTranscripcion(voz.Id, "Jordi pruebas"));
        Assert.True(bib.CambiarTranscripcion(voz.Id, "   ").SoloTimbre);
        Assert.Equal(Transcripcion(18), bib.CambiarTranscripcion(voz.Id, Transcripcion(18)).Transcripcion);
    }

    [Fact]
    public async Task Si_ffmpeg_falla_explica_que_no_se_pudo_leer_el_audio()
    {
        _ejecutor.Codigo = 1;
        var e = await Assert.ThrowsAsync<VozInvalidaException>(() => Biblioteca().ImportarAsync(Solicitud(_audio)));
        Assert.Contains("No se ha podido leer el audio", e.Message);
    }

    [Fact]
    public async Task Renombrar_conserva_la_carpeta_y_borrar_la_elimina()
    {
        var bib = Biblioteca();
        var voz = (await bib.ImportarAsync(Solicitud(_audio))).Voz;

        var renombrada = bib.Renombrar(voz.Id, "Narrador nocturno");
        Assert.Equal(voz.Id, renombrada.Id);
        Assert.Equal("Narrador nocturno", bib.Obtener(voz.Id)!.Nombre);

        bib.Borrar(voz.Id);
        Assert.Null(bib.Obtener(voz.Id));
        Assert.Empty(bib.Listar());
    }

    [Fact]
    public void Ignora_carpetas_sin_ficha_o_con_ficha_rota()
    {
        var bib = Biblioteca();
        Directory.CreateDirectory(Path.Combine(bib.Carpeta, "vacia"));
        Directory.CreateDirectory(Path.Combine(bib.Carpeta, "rota"));
        File.WriteAllText(Path.Combine(bib.Carpeta, "rota", BibliotecaVoces.ArchivoFicha), "{no es json");
        File.WriteAllText(Path.Combine(bib.Carpeta, "rota", BibliotecaVoces.ArchivoReferencia), "");

        Assert.Empty(bib.Listar());
    }

    [Fact]
    public void Lee_la_duracion_de_un_wav_con_bloques_extra()
    {
        var ruta = Path.Combine(_raiz, "x.wav");
        EscribirWav(ruta, 2.5, conBloqueExtra: true);

        Assert.Equal(2.5, BibliotecaVoces.DuracionWav(ruta), 3);
    }

    internal static void EscribirWav(string ruta, double segundos, bool conBloqueExtra = false)
    {
        const int sr = 24000;
        var datos = new byte[(int)(sr * segundos) * 2];
        using var w = new BinaryWriter(File.Create(ruta));
        var extra = conBloqueExtra ? 8 + 4 : 0;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + extra + datos.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sr);
        w.Write(sr * 2);
        w.Write((short)2);
        w.Write((short)16);
        if (conBloqueExtra)
        {
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            w.Write(4);
            w.Write(Encoding.ASCII.GetBytes("INFO"));
        }
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(datos.Length);
        w.Write(datos);
    }

    /// <summary>Simula ffmpeg: escribe un WAV de la duración indicada en el último argumento.</summary>
    private sealed class FfmpegFalso : IEjecutorProcesos
    {
        public double Segundos { get; set; } = 15;
        public int Codigo { get; set; }
        public string Ultima { get; private set; } = "";

        public Task<ResultadoProceso> EjecutarAsync(string programa, IReadOnlyList<string> argumentos,
            IReadOnlyDictionary<string, string> entorno, Action<string> alEscribir, CancellationToken ct)
        {
            Ultima = string.Join(' ', argumentos);
            if (Codigo == 0)
                EscribirWav(argumentos[^1], Segundos);
            return Task.FromResult(new ResultadoProceso(Codigo, ""));
        }
    }
}
