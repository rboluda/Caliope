using Constantino.Core.Doblaje;
using Constantino.Core.Instalaciones;
using Constantino.Core.Motores;

namespace Constantino.Core.Tests;

public sealed class DoblajeTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "constantino-doblaje-" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenDoblajes _almacen;
    private readonly MotoresFalsos _motores = new();
    private readonly TraductorFalso _traductor = new();
    private readonly AudioFalso _audio = new();
    private static readonly IProgress<EventoDoblaje> Sin = new ProgresoSincrono<EventoDoblaje>(_ => { });

    public DoblajeTests()
    {
        _almacen = new AlmacenDoblajes(Path.Combine(_carpeta, "Mis doblajes"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch (IOException) { }
    }

    private Doblador Doblador() => new(_almacen, _motores, _traductor, _audio);

    private string RutaVoz(string? _) => Path.Combine(_carpeta, "mi-voz.wav");

    [Theory]
    [InlineData(2.0, 3.0, 1.0, EncajeFrase.Cabe)]
    [InlineData(3.6, 3.0, 1.2, EncajeFrase.Justa)]
    [InlineData(4.5, 3.0, 1.25, EncajeFrase.NoCabe)]
    public void Encaja_acelerando_hasta_un_maximo(double duracion, double hueco, double tempo, EncajeFrase encaje)
    {
        var r = Doblaje.Doblador.Encajar(duracion, hueco);

        Assert.Equal(tempo, r.Tempo, 3);
        Assert.Equal(encaje, r.Encaje);
    }

    [Fact]
    public void El_hueco_llega_hasta_la_frase_siguiente_o_el_final()
    {
        var p = new ProyectoDoblaje("p", "p", "v.mp4", 20, DateTimeOffset.Now, Frases:
        [
            new FraseDoblaje(1, 1, 1.0, 3.0, "a"),
            new FraseDoblaje(2, 1, 5.0, 6.0, "b"),
        ]);

        Assert.Equal(3.9, Doblaje.Doblador.HuecoDisponible(p, p.ListaFrases[0]), 3);
        Assert.Equal(14.9, Doblaje.Doblador.HuecoDisponible(p, p.ListaFrases[1]), 3);
    }

    [Fact]
    public void La_referencia_es_el_tramo_seguido_mas_largo_hasta_12_segundos()
    {
        var frases = new List<FraseDoblaje>
        {
            new(1, 1, 0, 2, "a"), new(2, 1, 10, 14, "b"), new(3, 1, 14.3, 18, "c"), new(4, 1, 18.2, 25, "d"), new(5, 1, 30, 33, "e"),
        };

        var (inicio, fin) = Doblaje.Doblador.ElegirReferencia(frases);

        // 14,3–25: el tramo seguido más largo que no pasa de 12 s (10–18 también es seguido, pero más corto).
        Assert.Equal(14.3, inicio, 3);
        Assert.Equal(25, fin, 3);
    }

    [Fact]
    public void Los_subtitulos_tienen_numero_tiempos_y_traduccion()
    {
        var srt = Doblaje.Doblador.Subtitulos([new FraseDoblaje(1, 1, 61.5, 64, "Hello", "Hola", EncajeFrase.Cabe, Duracion: 1.25)]);

        Assert.Equal("1\r\n00:01:01,500 --> 00:01:02,750\r\nHola\r\n\r\n", srt);
    }

    [Fact]
    public void No_admite_videos_de_mas_de_diez_minutos()
    {
        Assert.Throws<InvalidOperationException>(() => _almacen.Crear(Path.Combine(_carpeta, "largo.mp4"), 601));
        var p = _almacen.Crear(Path.Combine(_carpeta, "Mi vídeo.mp4"), 120);
        Assert.Equal(p, _almacen.Abrir(p.Id));
        Assert.Equal("mi-video", p.Id);
    }

    [Fact]
    public async Task Prepara_el_guion_traducido_y_lo_reanuda_sin_repetir()
    {
        var p = _almacen.Crear(Path.Combine(_carpeta, "v.mp4"), 30);

        p = await Doblador().PrepararAsync(p, "mi-voz", Sin, CancellationToken.None);

        Assert.Equal(FaseDoblaje.Traducido, p.Fase);
        Assert.Equal(["es: Hello there.", "es: How are you?", "es: Bye."], p.ListaFrases.Select(f => f.Traduccion));
        Assert.Equal("mi-voz", Assert.Single(p.ListaHablantes).VozPronunciacion);
        Assert.True(File.Exists(Doblador().Referencia(p, 1)));
        Assert.Equal(1, _motores.Separaciones);

        await Doblador().PrepararAsync(p, "mi-voz", Sin, CancellationToken.None);
        Assert.Equal(1, _motores.Separaciones);
        Assert.Equal(1, _motores.Transcripciones);
        Assert.Equal(1, _traductor.Tandas);
    }

    [Fact]
    public async Task Genera_encaja_y_solo_rehace_lo_que_cambia()
    {
        var p = await Doblador().PrepararAsync(_almacen.Crear(Path.Combine(_carpeta, "v.mp4"), 30), "mi-voz", Sin, CancellationToken.None);

        p = await Doblador().GenerarAsync(p, RutaVoz, null, Sin, CancellationToken.None);

        Assert.All(p.ListaFrases, f => Assert.NotEqual(EncajeFrase.SinGenerar, f.Encaje));
        Assert.All(p.ListaFrases, f => Assert.True(File.Exists(Doblador().ArchivoFrase(p, f.Id))));
        Assert.Equal(3, _motores.Sintesis.Count);
        Assert.All(_motores.Sintesis, s => Assert.EndsWith("referencia_1.wav", s.Timbre, StringComparison.Ordinal));

        // Sin cambios no se regenera nada; al cambiar una traducción, solo esa.
        await Doblador().GenerarAsync(p, RutaVoz, null, Sin, CancellationToken.None);
        Assert.Equal(3, _motores.Sintesis.Count);
        p = p.ConFrase(p.ListaFrases[1] with { Traduccion = "Otra" });
        await Doblador().GenerarAsync(p, RutaVoz, null, Sin, CancellationToken.None);
        Assert.Equal(4, _motores.Sintesis.Count);
        Assert.Equal("Otra", _motores.Sintesis[^1].Texto);
    }

    [Fact]
    public async Task Si_no_cabe_ni_acelerando_pide_una_frase_mas_corta()
    {
        var p = _almacen.Crear(Path.Combine(_carpeta, "v.mp4"), 30);
        p = _almacen.Guardar(p with
        {
            Fase = FaseDoblaje.Traducido,
            Hablantes = [new HablanteDoblaje(1, "H", null, 0, 1)],
            // La frase siguiente empieza en 1 s: el hueco de la primera es 0,9 s.
            Frases = [new FraseDoblaje(1, 1, 0, 0.8, "Hello", new string('x', 60)), new FraseDoblaje(2, 1, 1.0, 2.0, "Bye", "Adiós")],
        });
        Directory.CreateDirectory(_almacen.Trabajo(p.Id));
        File.WriteAllText(Doblador().Referencia(p, 1), "");

        p = await Doblador().GenerarAsync(p, RutaVoz, null, Sin, CancellationToken.None);

        Assert.Equal(2, _traductor.Acortadas);
        Assert.Equal(new string('x', 15), p.ListaFrases[0].Traduccion);
        Assert.Contains(_audio.Llamadas, l => l.StartsWith("tempo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exporta_video_audio_y_subtitulos_con_la_voz_original_si_se_pide()
    {
        var p = await Doblador().PrepararAsync(_almacen.Crear(Path.Combine(_carpeta, "v.mp4"), 30), null, Sin, CancellationToken.None);
        p = await Doblador().GenerarAsync(p, RutaVoz, null, Sin, CancellationToken.None);
        p = _almacen.Guardar(p with { VozOriginalDeFondo = true });

        var r = await Doblador().ExportarAsync(p, Sin, CancellationToken.None);

        Assert.True(File.Exists(r.Subtitulos));
        Assert.Contains(_audio.Llamadas, l => l == "montar 3 con original");
        Assert.Contains(_audio.Llamadas, l => l.StartsWith("unir", StringComparison.Ordinal));
        Assert.EndsWith("(castellano).mp4", r.Video, StringComparison.Ordinal);
    }

    [Fact]
    public void El_traductor_lee_ids_numericos_y_de_texto()
    {
        var f = TraductorLocal.LeerFrases("""{"frases": [{"id": 1, "es": "Hola"}, {"id": "2", "es": "Adiós"}, {"es": "sin id"}]}""");

        Assert.Equal(new Dictionary<int, string> { [1] = "Hola", [2] = "Adiós" }, f);
    }

    private sealed class MotoresFalsos : IMotoresDoblaje
    {
        public int Separaciones, Transcripciones;
        public List<(string Texto, string Pronunciacion, string Timbre)> Sintesis { get; } = [];

        public Task SepararAsync(string audio, string voces, string fondo, CancellationToken ct)
        {
            Separaciones++;
            File.WriteAllText(voces, "v");
            File.WriteAllText(fondo, "f");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TramoTranscrito>> TranscribirAsync(string voces, string idioma, CancellationToken ct)
        {
            Transcripciones++;
            return Task.FromResult<IReadOnlyList<TramoTranscrito>>(
                [new(0.5, 2.5, "Hello there."), new(3.0, 5.0, "How are you?"), new(6.0, 7.0, "Bye.")]);
        }

        public Task<double> SintetizarAsync(string texto, string pronunciacion, string timbre, string salida, int semilla, CancellationToken ct)
        {
            Sintesis.Add((texto, pronunciacion, timbre));
            File.WriteAllText(salida, texto);
            return Task.FromResult(texto.Length / 14.0);
        }
    }

    private sealed class TraductorFalso : ITraductorDoblaje
    {
        public int Tandas, Acortadas;

        public Task<IReadOnlyDictionary<int, string>> TraducirAsync(IReadOnlyList<FraseATraducir> frases, CancellationToken ct)
        {
            Tandas++;
            return Task.FromResult<IReadOnlyDictionary<int, string>>(frases.ToDictionary(f => f.Id, f => "es: " + f.Original));
        }

        public Task<string> AcortarAsync(string original, string traduccion, int maxCaracteres, CancellationToken ct)
        {
            Acortadas++;
            return Task.FromResult(traduccion[..(traduccion.Length / 2)]);
        }
    }

    private sealed class AudioFalso : IAudioDoblaje
    {
        public List<string> Llamadas { get; } = [];

        public Task ExtraerAudioAsync(string video, string wav, CancellationToken ct) => Escribir(wav, "extraer");
        public Task RecortarAsync(string wav, double inicio, double fin, string salida, CancellationToken ct) => Escribir(salida, $"recortar {inicio}-{fin}");
        public Task CambiarTempoAsync(string wav, string salida, double tempo, CancellationToken ct) => Escribir(salida, $"tempo {tempo:0.00}");

        public Task MontarAsync(string fondo, string? vozOriginal, IReadOnlyList<(double Inicio, string Archivo)> frases, double duracion,
            string salida, CancellationToken ct) =>
            Escribir(salida, $"montar {frases.Count}{(vozOriginal is null ? "" : " con original")}");

        public Task UnirAsync(string video, string audio, string salida, CancellationToken ct) => Escribir(salida, "unir");

        private Task Escribir(string ruta, string llamada)
        {
            Llamadas.Add(llamada);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, llamada);
            return Task.CompletedTask;
        }
    }
}
