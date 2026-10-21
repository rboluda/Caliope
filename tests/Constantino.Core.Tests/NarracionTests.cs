using Constantino.Core.Motores;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Constantino.Core.Audio;
using Constantino.Core.Instalaciones;
using Constantino.Core.Narracion;

namespace Constantino.Core.Tests;

public class GuionTests
{
    private const string Ejemplo = """
        // comentario que no se lee
        # gancho
        Sábado por la noche. [pause] Diluvia.

        # discoteca
        En la puerta hay una chica. [pause] Te quitas la cazadora.
        Bailáis toda la noche.


        # Final del todo
        De madrugada. [long pause] Al día siguiente. [pause] [whispers] Y te dice que murió.
        """;

    [Fact]
    public void Separa_bloques_nombres_y_etiquetas()
    {
        var b = Guion.Leer(Ejemplo);

        Assert.Equal(3, b.Count);
        Assert.Equal(["01_gancho", "02_discoteca", "03_final-del-todo"], b.Select(x => x.Archivo));
        Assert.Equal([new EventoVoz("Sábado por la noche."), new EventoSilencio(0.6), new EventoVoz("Diluvia.")], b[0].Eventos);
        // Las líneas seguidas del mismo bloque se unen en un solo fragmento.
        Assert.Equal(new EventoVoz("Te quitas la cazadora. Bailáis toda la noche."), b[1].Eventos[2]);
        Assert.Equal([new EventoVoz("De madrugada."), new EventoSilencio(1.5), new EventoVoz("Al día siguiente."),
            new EventoSilencio(0.6), new EventoVoz("Y te dice que murió.", Susurro: true, Variante: "susurro")], b[2].Eventos);
        Assert.Empty(b[2].Avisos);
    }

    [Fact]
    public void El_susurro_acaba_con_otra_etiqueta_o_con_la_linea()
    {
        var b = Guion.Leer("Hola [sighs] qué tal. [WHISPERS] secreto\nnormal otra vez [Long_Pause] fin [/whispers]")[0];

        Assert.Equal(["Etiqueta desconocida eliminada: [sighs]"], b.Avisos);
        Assert.Equal([new EventoVoz("Hola qué tal."), new EventoVoz("secreto", true, "susurro"), new EventoVoz("normal otra vez"),
            new EventoSilencio(1.5), new EventoVoz("fin")], b.Eventos);
    }

    [Fact]
    public void Reconoce_las_etiquetas_de_las_variantes_de_la_voz()
    {
        var b = Guion.Leer("Uno [Tenso] dos [/tenso] tres [alegre] cuatro\ncinco", variantes: ["tenso", "alegre"])[0];

        Assert.Equal([new EventoVoz("Uno"), new EventoVoz("dos", Variante: "tenso"), new EventoVoz("tres"),
            new EventoVoz("cuatro", Variante: "alegre"), new EventoVoz("cinco")], b.Eventos);
        Assert.Empty(b.Avisos);
        // Sin la variante en la voz, la etiqueta se quita y se avisa.
        Assert.Single(Guion.Leer("Uno [tenso] dos")[0].Avisos);
    }

    [Fact]
    public void Las_pausas_seguidas_se_suman_y_se_pueden_configurar()
    {
        var b = Guion.Leer("Uno. [pause] [pause] [long pause] Dos.", pausa: 1, pausaLarga: 3)[0];

        Assert.Equal(new EventoSilencio(5), b.Eventos[1]);
    }

    [Fact]
    public void Parte_textos_largos_por_frases_sin_romperlas()
    {
        var trozos = Guion.PartirEnFrases("Primera frase. Segunda frase algo más larga. Tercera.", 30);

        Assert.Equal(["Primera frase.", "Segunda frase algo más larga.", "Tercera."], trozos);
        Assert.Equal(["Corto."], Guion.PartirEnFrases("Corto.", 30));
    }
}

public sealed class PcmTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "constantino-pcm", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    [Fact]
    public void Escribe_y_lee_wav_en_coma_flotante_y_de_16_bits()
    {
        var ruta = Path.Combine(_carpeta, "a.wav");
        new Pcm([0f, 0.5f, -0.25f], 24000).EscribirWav(ruta);
        Assert.Equal([0f, 0.5f, -0.25f], Pcm.LeerWav(ruta).Muestras);

        var ruta16 = Path.Combine(_carpeta, "b.wav");
        BibliotecaVocesTests.EscribirWav(ruta16, 0.5, conBloqueExtra: true);
        var leido = Pcm.LeerWav(ruta16);
        Assert.Equal(24000, leido.Frecuencia);
        Assert.Equal(0.5, leido.Segundos, 3);
    }

    [Fact]
    public void Recorta_el_silencio_de_los_extremos_con_margen()
    {
        const int sr = 24000;
        var muestras = new float[sr * 3];
        for (var i = sr; i < 2 * sr; i++)
            muestras[i] = 0.3f * MathF.Sin(i * 0.05f);

        var recortado = new Pcm(muestras, sr).Recortar(margen: 0.05);

        Assert.InRange(recortado.Segundos, 1.08, 1.12);
        Assert.Equal(0f, recortado.Muestras[0]); // fundido de entrada
        Assert.Empty(new Pcm(new float[sr], sr).Recortar().Muestras);
    }

    [Fact]
    public void Une_y_aplica_ganancia()
    {
        var unido = Pcm.Unir([new Pcm([1f, 1f], 10), Pcm.Silencio(0.3, 10), new Pcm([0.5f], 10)], 10);

        Assert.Equal([1f, 1f, 0, 0, 0, 0.5f], unido.Muestras);
        Assert.Equal(0.5f, new Pcm([1f], 10).ConGanancia(-6.0206).Muestras[0], 3);
    }
}

public sealed class NarradorTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "constantino-narrador", Guid.NewGuid().ToString("N"));
    private readonly SintetizadorFalso _sintetizador = new();
    private readonly ProcesadorFalso _procesador = new();
    private readonly AlmacenProyectos _almacen;

    public NarradorTests() => _almacen = new AlmacenProyectos(Path.Combine(_carpeta, "proyectos"));

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private static readonly AjustesNarracion Ajustes = new(VozId: "voz", Frecuencia: 24000, PausaEntreBloques: 0.5, SilencioInicio: 0.1, SilencioFin: 0.2);

    private const string GuionTres = """
        # uno
        Primera frase del todo. [pause] Segunda.

        # dos
        [whispers] Un secreto.

        # tres
        Final.
        """;

    private Narrador Narrador() => new(_almacen, _sintetizador, _procesador);

    private static IProgress<EventoNarracion> Sin => new ProgresoSincrono<EventoNarracion>(_ => { });

    [Fact]
    public async Task Genera_todos_los_bloques_el_completo_y_el_manifiesto()
    {
        var p = _almacen.Crear("La cazadora", Ajustes, GuionTres);
        var eventos = new List<EventoNarracion>();

        var r = await Narrador().NarrarAsync(p, GuionTres, null, false, new ProgresoSincrono<EventoNarracion>(eventos.Add), CancellationToken.None);

        var salida = _almacen.CarpetaSalida(p.Id);
        Assert.Equal(3, r.Generados);
        Assert.Equal(["01_uno.mp3", "02_dos.mp3", "03_tres.mp3"], r.Proyecto.Bloques.Select(b => b.Archivo));
        Assert.All(r.Proyecto.Bloques, b => Assert.True(File.Exists(Path.Combine(salida, b.Archivo))));
        Assert.Equal(Path.Combine(salida, "la-cazadora_completo.mp3"), r.Completo);
        Assert.Equal(["Primera frase del todo.", "Segunda.", "Un secreto.", "Final."], _sintetizador.Textos);

        // El bloque 1 = 0,1 + frase + 0,6 de pausa + frase + 0,2, más 0,05 s que el recorte deja a cada lado de cada
        // frase; el completo suma los bloques y 0,5 s entre ellos.
        var manifiesto = JsonNode.Parse(File.ReadAllText(r.Manifiesto))!;
        var completo = manifiesto["completo"]!;
        var b1 = r.Proyecto.Bloques[0].Duracion;
        Assert.Equal(0.1 + Duracion("Primera frase del todo.") + 0.6 + Duracion("Segunda.") + 0.2 + 4 * 0.05, b1, 2);
        Assert.Equal(0, (double)completo["bloques"]![0]!["inicio_s"]!);
        Assert.Equal(b1 + 0.5, (double)completo["bloques"]![1]!["inicio_s"]!, 2);
        Assert.Equal(r.Proyecto.Bloques.Sum(b => b.Duracion) + 1.0, (double)completo["duracion_s"]!, 2);
        Assert.Equal(1, eventos[^1].Fraccion);
        Assert.False(Directory.Exists(Path.Combine(salida, ".tmp")));
    }

    [Fact]
    public async Task Solo_regenera_lo_que_cambia()
    {
        var p = _almacen.Crear("p", Ajustes, GuionTres);
        var r1 = await Narrador().NarrarAsync(p, GuionTres, null, false, Sin, CancellationToken.None);
        _sintetizador.Textos.Clear();

        var r2 = await Narrador().NarrarAsync(r1.Proyecto, GuionTres, null, false, Sin, CancellationToken.None);
        Assert.Equal((0, 3), (r2.Generados, r2.SinCambios));
        Assert.Empty(_sintetizador.Textos);

        var cambiado = GuionTres.Replace("Final.", "Final distinto.");
        var r3 = await Narrador().NarrarAsync(r2.Proyecto, cambiado, null, false, Sin, CancellationToken.None);
        Assert.Equal((1, 2), (r3.Generados, r3.SinCambios));
        Assert.Equal(["Final distinto."], _sintetizador.Textos);

        // Cambiar un ajuste que afecta al sonido invalida todos los bloques.
        var r4 = await Narrador().NarrarAsync(r3.Proyecto with { Ajustes = r3.Proyecto.Ajustes with { Semilla = 7 } }, cambiado, null, false, Sin, CancellationToken.None);
        Assert.Equal(3, r4.Generados);
    }

    [Fact]
    public async Task Regenerar_un_bloque_concreto_aunque_no_haya_cambiado()
    {
        var p = _almacen.Crear("p", Ajustes, GuionTres);
        var r1 = await Narrador().NarrarAsync(p, GuionTres, null, false, Sin, CancellationToken.None);
        _sintetizador.Textos.Clear();

        var r2 = await Narrador().NarrarAsync(r1.Proyecto, GuionTres, new HashSet<int> { 2 }, forzar: true, Sin, CancellationToken.None);

        Assert.Equal(["Un secreto."], _sintetizador.Textos);
        Assert.Equal((1, 2), (r2.Generados, r2.SinCambios));
        Assert.NotNull(r2.Completo);
    }

    [Fact]
    public async Task El_susurro_baja_el_volumen_pero_se_mide_sin_bajar()
    {
        var p = _almacen.Crear("p", Ajustes with { SusurroDb = -20 }, GuionTres);

        await Narrador().NarrarAsync(p, GuionTres, null, false, Sin, CancellationToken.None);

        var (montaje, medida) = _procesador.Bloques["02_dos"];
        Assert.Equal(0.1f, montaje.Muestras.Max(), 2);   // 1,0 × −20 dB
        Assert.Equal(1f, medida.Muestras.Max(), 2);
    }

    [Fact]
    public async Task Si_se_quita_un_bloque_se_borran_sus_archivos()
    {
        var p = _almacen.Crear("p", Ajustes, GuionTres);
        var r1 = await Narrador().NarrarAsync(p, GuionTres, null, false, Sin, CancellationToken.None);
        var sinTercero = GuionTres[..GuionTres.IndexOf("# tres", StringComparison.Ordinal)];

        var r2 = await Narrador().NarrarAsync(r1.Proyecto, sinTercero, null, false, Sin, CancellationToken.None);

        Assert.Equal(2, r2.Proyecto.Bloques.Count);
        Assert.False(File.Exists(Path.Combine(_almacen.CarpetaSalida(p.Id), "03_tres.mp3")));
    }

    [Fact]
    public async Task Avisa_de_duraciones_anomalas()
    {
        _sintetizador.Recortar = true;
        var guion = "Una frase bastante larga que el modelo ha dejado a medias por algún motivo.";
        var p = _almacen.Crear("p", Ajustes, guion);

        var r = await Narrador().NarrarAsync(p, guion, null, false, Sin, CancellationToken.None);

        Assert.Contains("Duración anómala", Assert.Single(r.Proyecto.Bloques[0].Avisos));
    }

    [Fact]
    public async Task Lo_generado_antes_de_un_fallo_se_conserva()
    {
        _sintetizador.FallarCon = "Final.";
        var p = _almacen.Crear("p", Ajustes, GuionTres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Narrador().NarrarAsync(p, GuionTres, null, false, Sin, CancellationToken.None));

        var guardado = _almacen.Leer(p.Id)!;
        Assert.Equal([1, 2], guardado.Bloques.Select(b => b.Indice));
    }

    [Fact]
    public async Task Las_variantes_de_la_voz_se_usan_con_sus_etiquetas()
    {
        _sintetizador.Variantes = ["susurro", "tenso"];
        const string guion = "Normal. [tenso] Algo va mal. [whispers] Un secreto.\nOtra vez normal. [desconocida] Fin.";
        var p = _almacen.Crear("p", Ajustes with { SusurroDb = -20 }, guion);

        var r = await Narrador().NarrarAsync(p, guion, null, false, Sin, CancellationToken.None);

        Assert.Equal([("Normal.", null), ("Algo va mal.", "tenso"), ("Un secreto.", "susurro"), ("Otra vez normal. Fin.", (string?)null)],
            _sintetizador.Lecturas);
        // Con variante de susurro, el susurro no se simula bajando el volumen.
        Assert.Equal(1f, _procesador.Bloques["01_bloque"].Montaje.Muestras.Max(), 2);
        Assert.Contains("Etiqueta desconocida eliminada: [desconocida]", r.Proyecto.Bloques[0].Avisos);
    }

    [Fact]
    public async Task Una_variante_que_la_voz_no_tiene_se_lee_con_la_voz_normal()
    {
        const string guion = "[whispers] Secreto.";
        var p = _almacen.Crear("p", Ajustes, guion);

        await Narrador().NarrarAsync(p, guion, null, false, Sin, CancellationToken.None);

        Assert.Equal([("Secreto.", (string?)null)], _sintetizador.Lecturas);
    }

    [Fact]
    public void Lee_otras_voces_efectos_y_el_diccionario()
    {
        var contexto = new ContextoGuion(
            Voces: new Dictionary<string, string> { ["anciano"] = "abuelo-1", ["maria"] = "maria" },
            Efectos: ["trueno"],
            Diccionario: [new Pronunciacion("Calíope", "Ca-lí-o-pe"), new Pronunciacion("ONU", "o ene u")]);

        var b = Guion.Leer("[voz: Anciano] Hola, onu. [sfx: trueno] Sigue. [normal] Fin.\n[voz: maría] Y la ONUS de Calíope. [voz: nadie] x [sfx: gato]",
            0.6, 1.5, contexto)[0];

        Assert.Equal(
        [
            new EventoVoz("Hola, o ene u.", VozId: "abuelo-1"),
            new EventoEfecto("trueno"),
            new EventoVoz("Sigue.", VozId: "abuelo-1"),
            new EventoVoz("Fin."),
            new EventoVoz("Y la ONUS de Ca-lí-o-pe.", VozId: "maria"),
            new EventoVoz("x"),
        ], b.Eventos);
        Assert.Contains(b.Avisos, a => a.Contains("Voz desconocida"));
        Assert.Contains(b.Avisos, a => a.Contains("Efecto desconocido"));
    }

    [Fact]
    public void La_huella_no_cambia_por_ajustes_nuevos_con_su_valor_por_defecto()
    {
        var bloque = Guion.Leer("Hola. [whispers] Adiós.")[0];
        var normal = Huellas.DeBloque(bloque, Ajustes, "voz", "modelo");

        Assert.Equal(normal, Huellas.DeBloque(bloque, Ajustes with { Velocidad = 1.0, TonoSemitonos = 0 }, "voz", "modelo"));
        Assert.NotEqual(normal, Huellas.DeBloque(bloque, Ajustes with { Velocidad = 1.1 }, "voz", "modelo"));
        Assert.NotEqual(normal, Huellas.DeBloque(bloque, Ajustes with { TonoSemitonos = -1 }, "voz", "modelo"));
        Assert.NotEqual(normal, Huellas.DeBloque(Guion.Leer("Hola. [tenso] Adiós.", variantes: ["tenso"])[0], Ajustes, "voz", "modelo"));
    }

    [Fact]
    public void El_motor_y_la_intensidad_solo_cuentan_con_chatterbox()
    {
        var bloque = Guion.Leer("Hola.")[0];
        var qwen = Huellas.DeBloque(bloque, Ajustes, "voz", "modelo");

        // Con Qwen la intensidad no se usa: cambiarla no obliga a regenerar.
        Assert.Equal(qwen, Huellas.DeBloque(bloque, Ajustes with { Intensidad = 0.9 }, "voz", "modelo"));
        var chatterbox = Huellas.DeBloque(bloque, Ajustes with { Motor = MotoresDeVoz.Chatterbox }, "voz", "modelo");
        Assert.NotEqual(qwen, chatterbox);
        Assert.NotEqual(chatterbox, Huellas.DeBloque(bloque, Ajustes with { Motor = MotoresDeVoz.Chatterbox, Intensidad = 0.9 }, "voz", "modelo"));
    }

    /// <summary>Huella calculada con la versión del H5: si cambia, los proyectos ya generados se darían por desactualizados.</summary>
    [Fact]
    public void La_huella_coincide_con_la_de_versiones_anteriores()
    {
        var bloque = Guion.Leer("# uno\nHola. [pause] Qué [whispers] miedo da.", 0.6, 1.5, ["susurro"])[0];

        Assert.Equal("bf9c291cc1d36a9f", Huellas.DeBloque(bloque, new AjustesNarracion(), "voz", "modelo"));
    }

    [Fact]
    public void El_filtro_de_velocidad_y_tono_conserva_formantes_y_respeta_los_limites()
    {
        Assert.Equal("", ProcesadorFfmpeg.FiltroVelocidadYTono(Ajustes));
        var filtro = ProcesadorFfmpeg.FiltroVelocidadYTono(Ajustes with { Velocidad = 1.5, TonoSemitonos = -12 });
        Assert.Contains("tempo=1.2:", filtro);                 // limitado a 1,2
        Assert.Contains($"pitch={Math.Pow(2, -3.0 / 12):0.#####}".Replace(',', '.'), filtro); // limitado a −3 semitonos
        Assert.Contains("formant=preserved", filtro);
    }

    private static double Duracion(string texto) => Math.Round(texto.Length / 14.0, 2);

    /// <summary>«Lee» un texto como un tono de amplitud 1 con 0,3 s de silencio delante y detrás (que se recorta).</summary>
    private sealed class SintetizadorFalso : ISintetizador
    {
        public List<string> Textos { get; } = [];
        public bool Recortar { get; set; }
        public string? FallarCon { get; set; }
        public string FirmaVoz => "voz-falsa";
        public string Modelo => "modelo-falso";
        public IReadOnlyCollection<string> Variantes { get; set; } = [];
        public IReadOnlyDictionary<string, string> OtrasVoces { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Firmas { get; } = [];
        public string FirmaDeVoz(string vozId) => Firmas.GetValueOrDefault(vozId, vozId);

        /// <summary>Con qué variante se leyó cada texto (null = voz normal).</summary>
        public List<(string Texto, string? Variante)> Lecturas { get; } = [];

        /// <summary>Con qué voz y semilla se leyó cada texto.</summary>
        public List<(string Texto, string? Voz, int Semilla)> Voces { get; } = [];

        public Task<string> SintetizarAsync(string texto, string? variante, string? vozId, AjustesNarracion ajustes, string salida, CancellationToken ct)
        {
            if (texto == FallarCon)
                throw new InvalidOperationException("fallo provocado");
            Textos.Add(texto);
            Lecturas.Add((texto, variante));
            Voces.Add((texto, vozId, ajustes.Semilla));
            const int sr = 24000;
            var voz = (int)(sr * (Recortar ? 0.3 : Duracion(texto)));
            var muestras = new float[voz + 2 * (int)(0.3 * sr)];
            for (var i = 0; i < voz; i++)
                muestras[(int)(0.3 * sr) + i] = 1f;
            new Pcm(muestras, sr).EscribirWav(salida);
            return Task.FromResult(salida);
        }
    }

    /// <summary>No normaliza: copia los WAV y guarda lo que recibe para comprobarlo.</summary>
    private sealed class ProcesadorFalso : IProcesadorAudio
    {
        public Dictionary<string, (Pcm Montaje, Pcm Medida)> Bloques { get; } = [];

        public Task NormalizarAsync(string entrada, string medida, string salidaWav, AjustesNarracion ajustes, CancellationToken ct)
        {
            var montaje = Pcm.LeerWav(entrada);
            Bloques[Path.GetFileNameWithoutExtension(entrada)] = (montaje, Pcm.LeerWav(medida));
            montaje.EscribirWav(salidaWav);
            return Task.CompletedTask;
        }

        public Task CodificarAsync(string wav, string destino, AjustesNarracion ajustes, CancellationToken ct)
        {
            File.Copy(wav, destino, overwrite: true);
            return Task.CompletedTask;
        }

        /// <summary>Un «efecto» de medio segundo.</summary>
        public Task ConvertirAsync(string origen, string destinoWav, int frecuencia, CancellationToken ct)
        {
            var muestras = new float[frecuencia / 2];
            Array.Fill(muestras, 0.5f);
            new Pcm(muestras, frecuencia).EscribirWav(destinoWav);
            return Task.CompletedTask;
        }
    }

    private sealed class SonidosFalsos(string carpeta) : ISonidos
    {
        public IReadOnlyCollection<string> Efectos => ["trueno"];

        public string? RutaEfecto(string nombre)
        {
            if (nombre != "trueno")
                return null;
            var ruta = Path.Combine(carpeta, "trueno.wav");
            if (!File.Exists(ruta))
            {
                Directory.CreateDirectory(carpeta);
                File.WriteAllText(ruta, "x");
            }
            return ruta;
        }
    }

    // --- Otras voces, efectos y tomas ---------------------------------------------------------

    [Fact]
    public async Task Lee_cada_linea_con_su_voz_y_mete_los_efectos()
    {
        _sintetizador.OtrasVoces = new Dictionary<string, string> { ["anciano"] = "abuelo-1" };
        const string guion = "Narra el narrador. [sfx: trueno]\n[voz: Anciano] No abras. [normal] Y abrió.";
        var p = _almacen.Crear("p", Ajustes, guion);
        var narrador = new Narrador(_almacen, _sintetizador, _procesador, new SonidosFalsos(Path.Combine(_carpeta, "sonidos")));

        var r = await narrador.NarrarAsync(p, guion, null, false, Sin, CancellationToken.None);

        Assert.Equal([("Narra el narrador.", null), ("No abras.", "abuelo-1"), ("Y abrió.", null)],
            _sintetizador.Voces.Select(v => (v.Texto, v.Voz)));
        // El efecto (0,5 s) ocupa su sitio en el bloque; tras él no hay pausa entre frases, solo entre las dos últimas.
        var sinEfecto = 0.1 + Duracion("Narra el narrador.") + Duracion("No abras.") + Duracion("Y abrió.") + Ajustes.PausaEntreFrases + 0.2 + 6 * 0.05;
        Assert.Equal(sinEfecto + 0.5, r.Proyecto.Bloques[0].Duracion, 1);
    }

    [Fact]
    public async Task La_huella_cambia_si_cambia_la_otra_voz_pero_no_la_de_los_bloques_sin_ella()
    {
        _sintetizador.OtrasVoces = new Dictionary<string, string> { ["anciano"] = "abuelo-1" };
        const string guion = "Solo narrador.\n\n[voz: anciano] Otra voz.";
        var p = _almacen.Crear("p", Ajustes, guion);
        var r1 = await Narrador().NarrarAsync(p, guion, null, false, Sin, CancellationToken.None);

        _sintetizador.Firmas["abuelo-1"] = "grabacion-nueva";
        var r2 = await Narrador().NarrarAsync(r1.Proyecto, guion, null, false, Sin, CancellationToken.None);

        Assert.Equal((1, 1), (r2.Generados, r2.SinCambios));
    }

    [Fact]
    public async Task Otras_tomas_y_elegir_una_sin_regenerar_el_resto()
    {
        var p = _almacen.Crear("p", Ajustes, GuionTres);
        var r1 = await Narrador().NarrarAsync(p, GuionTres, null, false, Sin, CancellationToken.None);
        _sintetizador.Voces.Clear();

        var tomas = await Narrador().GenerarTomasAsync(r1.Proyecto, GuionTres, 2, 2, Sin, CancellationToken.None);

        Assert.Equal(2, tomas.Count);
        Assert.Equal(2, tomas.Select(t => t.Semilla).Distinct().Count());
        Assert.DoesNotContain(Ajustes.Semilla, tomas.Select(t => t.Semilla));
        Assert.All(tomas, t => Assert.True(File.Exists(t.Archivo)));
        Assert.All(_sintetizador.Voces, v => Assert.NotEqual(Ajustes.Semilla, v.Semilla));

        var elegida = tomas[1].Semilla;
        var p2 = await Narrador().ElegirTomaAsync(r1.Proyecto, GuionTres, 2, elegida, CancellationToken.None);

        Assert.Equal(elegida, Assert.Single(p2.Tomas!).Value);
        // La toma anterior queda entre las alternativas y la elegida deja de estarlo.
        var restantes = Narrador().TomasDe(p2, GuionTres, 2).Select(t => t.Semilla).ToList();
        Assert.Contains(Ajustes.Semilla, restantes);
        Assert.DoesNotContain(elegida, restantes);

        _sintetizador.Voces.Clear();
        var r3 = await Narrador().NarrarAsync(p2, GuionTres, null, false, Sin, CancellationToken.None);
        Assert.Equal((0, 3), (r3.Generados, r3.SinCambios));
        Assert.NotNull(r3.Completo);

        // Volver a la toma original quita la elección.
        var p3 = await Narrador().ElegirTomaAsync(r3.Proyecto, GuionTres, 2, Ajustes.Semilla, CancellationToken.None);
        Assert.Empty(p3.Tomas!);
    }
}

/// <summary>ffmpeg de verdad, si está en el PATH (si no, la prueba no hace nada).</summary>
public sealed class ProcesadorFfmpegTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "constantino-ffmpeg", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private static string? Ffmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("where", "ffmpeg") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            var ruta = p!.StandardOutput.ReadLine();
            p.WaitForExit();
            return p.ExitCode == 0 && File.Exists(ruta) ? ruta : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Fact]
    public async Task Normaliza_al_volumen_objetivo_y_codifica_mp3()
    {
        if (Ffmpeg() is not { } ffmpeg)
            return;
        const int sr = 24000;
        var muestras = new float[sr * 3];
        for (var i = 0; i < muestras.Length; i++)
            muestras[i] = 0.02f * MathF.Sin(2 * MathF.PI * 220 * i / sr); // muy bajo
        var entrada = Path.Combine(_carpeta, "entrada.wav");
        new Pcm(muestras, sr).EscribirWav(entrada);
        var ajustes = new AjustesNarracion(Lufs: -16, Frecuencia: 48000);
        var procesador = new ProcesadorFfmpeg(ffmpeg, new EjecutorProcesos());

        var normalizado = Path.Combine(_carpeta, "norm.wav");
        await procesador.NormalizarAsync(entrada, entrada, normalizado, ajustes, CancellationToken.None);
        var mp3 = Path.Combine(_carpeta, "final.mp3");
        await procesador.CodificarAsync(normalizado, mp3, ajustes, CancellationToken.None);

        var resultado = Pcm.LeerWav(normalizado);
        Assert.Equal(48000, resultado.Frecuencia);
        Assert.InRange(resultado.Muestras.Max(), 0.1f, 0.85f); // subido mucho, pero por debajo del techo de −1,5 dB
        Assert.True(new FileInfo(mp3).Length > 10_000);

        // Más rápido: 3 s a velocidad 1,2 duran unos 2,5 s.
        var rapido = Path.Combine(_carpeta, "rapido.wav");
        await procesador.NormalizarAsync(entrada, entrada, rapido, ajustes with { Velocidad = 1.2, TonoSemitonos = -2 }, CancellationToken.None);
        Assert.InRange(Pcm.LeerWav(rapido).Segundos, 2.4, 2.6);
    }
}
