using System.IO.Compression;
using Constantino.Core.Actas;
using Constantino.Core.IA;
using Constantino.Core.Instalaciones;
using Constantino.Core.Motores;

namespace Constantino.Core.Tests;

public sealed class ActasTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "constantino-actas-" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenActas _almacen;
    private readonly MotoresFalsos _motores = new();
    private readonly IAFalsa _ia = new();
    private static readonly IProgress<EventoActa> Sin = new ProgresoSincrono<EventoActa>(_ => { });

    public ActasTests()
    {
        _almacen = new AlmacenActas(Path.Combine(_carpeta, "Mis actas"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch (IOException) { }
    }

    private Secretario Secretario() => new(_almacen, _motores, _ia);

    private ProyectoActa Nueva() => _almacen.Crear(Path.Combine(_carpeta, "10-09 Reunión de Azor.mp3"), 1200, new DateOnly(2026, 10, 9));

    [Fact]
    public void Crea_guarda_y_lista_actas_de_hasta_cuatro_horas()
    {
        Assert.Throws<InvalidOperationException>(() => _almacen.Crear(Path.Combine(_carpeta, "larga.mp3"), 4 * 3600 + 1, new DateOnly(2026, 1, 1)));
        var p = Nueva();

        Assert.Equal("10-09 Reunión de Azor", p.Titulo);
        Assert.Equal("10-09-reunion-de-azor", p.Id);
        Assert.Equal(new DateOnly(2026, 10, 9), _almacen.Abrir(p.Id)!.Fecha);
        Assert.Single(_almacen.Listar());
    }

    [Fact]
    public void Junta_los_tramos_seguidos_de_cada_persona_y_elige_su_muestra()
    {
        TramoTranscrito[] tramos =
        [
            new(0, 4, "Hola."), new(4.5, 9, "¿Qué tal?"), new(9.5, 25, "Bien, gracias, os cuento."), new(30, 33, "Vale."), new(33.5, 35, "Perfecto."),
        ];

        var (intervenciones, participantes) = Actas.Secretario.Agrupar(tramos, [1, 1, 2, 2, 1]);

        // La pausa de 5 s entre 25 y 30 separa dos intervenciones aunque sean de la misma persona.
        Assert.Equal([1, 2, 2, 1], intervenciones.Select(i => i.Hablante));
        Assert.Equal("Hola. ¿Qué tal?", intervenciones[0].Texto);
        Assert.Equal([1, 2, 3, 4], intervenciones.Select(i => i.Id));
        Assert.Equal(["Persona 1", "Persona 2"], participantes.Select(x => x.Nombre));
        Assert.Equal((9.5, 19.5), (participantes[1].MuestraInicio, participantes[1].MuestraFin));
    }

    [Fact]
    public void La_transcripcion_lleva_minuto_y_nombre()
    {
        var p = Nueva() with
        {
            Participantes = [new ParticipanteActa(1, "Raúl (Informática)", 0, 1)],
            Intervenciones = [new IntervencionActa(1, 1, 65, 70, "Te creo un usuario de VPN."), new IntervencionActa(2, 2, 3725, 3730, "Vale.")],
        };

        Assert.Equal("[01:05] Raúl (Informática): Te creo un usuario de VPN.\n[1:02:05] Persona 2: Vale.", Actas.Secretario.Transcripcion(p));
    }

    [Fact]
    public async Task Transcribe_separa_personas_y_no_vuelve_a_extraer_el_audio()
    {
        var p = await Secretario().TranscribirAsync(Nueva(), Sin, CancellationToken.None);

        Assert.Equal(FaseActa.Transcrita, p.Fase);
        Assert.Equal(2, p.ListaParticipantes.Count);
        Assert.Equal([1, 2, 1], p.ListaIntervenciones.Select(i => i.Hablante));
        Assert.Null(_motores.Personas);

        p = await Secretario().TranscribirAsync(_almacen.Guardar(p with { Personas = 3 }), Sin, CancellationToken.None);
        Assert.Equal(1, _motores.Extracciones);
        Assert.Equal(3, _motores.Personas);
    }

    [Fact]
    public async Task La_IA_propone_nombres_solo_para_quien_no_lo_tiene()
    {
        var p = await Secretario().TranscribirAsync(Nueva(), Sin, CancellationToken.None);
        p = _almacen.Guardar(p with { Participantes = [p.ListaParticipantes[0], p.ListaParticipantes[1] with { Nombre = "Alberto (Envira)" }] });
        _ia.Respuestas.Enqueue("""{"personas": [{"id": 1, "nombre": "Raúl", "papel": "Informática de Azor"}, {"id": 2, "nombre": "Gerard", "papel": null}]}""");

        p = await Secretario().ProponerNombresAsync(p, Sin, CancellationToken.None);

        Assert.Equal(["Raúl (Informática de Azor)", "Alberto (Envira)"], p.ListaParticipantes.Select(x => x.Nombre));
        Assert.Contains("Fecha de la reunión: viernes, 9 de octubre de 2026", _ia.Pedidos[0]);
    }

    [Fact]
    public void Lee_los_nombres_propuestos_y_descarta_los_que_no_lo_son()
    {
        var n = Actas.Secretario.LeerNombres("""{"personas": [{"id": "1", "nombre": "Raúl", "papel": ""}, {"id": 2, "nombre": null}, {"id": 3, "nombre": "Persona 3"}]}""");

        Assert.Equal(new Dictionary<int, string> { [1] = "Raúl" }, n);
        Assert.Empty(Actas.Secretario.LeerNombres("no es json"));
    }

    [Fact]
    public async Task Redacta_el_acta_de_una_vez_con_la_fecha_y_el_glosario()
    {
        var p = await Secretario().TranscribirAsync(_almacen.Guardar(Nueva() with { Glosario = "Endress+Hauser, VLAN" }), Sin, CancellationToken.None);
        _ia.Respuestas.Enqueue("```markdown\n# Acta: Azor\n- Punto [00:01]\n```");

        var (q, acta) = await Secretario().RedactarAsync(p, Sin, CancellationToken.None);

        Assert.Equal("# Acta: Azor\n- Punto [00:01]", acta);
        Assert.Equal(acta + "\n", _almacen.LeerActa(q.Id));
        Assert.Equal(FaseActa.Redactada, q.Fase);
        Assert.Single(_ia.Pedidos);
        Assert.Contains("Glosario", _ia.Pedidos[0]);
        Assert.Contains("Endress+Hauser, VLAN", _ia.Pedidos[0]);
        Assert.Contains("[00:00] Persona 1: Buenos días.", _ia.Pedidos[0]);
    }

    [Theory]
    [InlineData("**Fecha:** 9-10 · **Participantes:** Ana · **Mencionados:** ninguno\n## Puntos", "**Fecha:** 9-10 · **Participantes:** Ana\n## Puntos")]
    [InlineData("**Participantes:** Ana  \n**Mencionados:** sin ninguno  \n\n## Puntos", "**Participantes:** Ana  \n\n## Puntos")]
    [InlineData("**Mencionados:** Ascensión\n", "**Mencionados:** Ascensión\n")]
    public void Quita_los_mencionados_vacios(string antes, string despues)
    {
        Assert.Equal(despues, Actas.Secretario.QuitarMencionadosVacios(antes));
    }

    [Fact]
    public async Task Una_reunion_larga_se_resume_por_partes()
    {
        var p = Nueva() with
        {
            Fase = FaseActa.Transcrita,
            Participantes = [new ParticipanteActa(1, "Ana", 0, 1)],
            Intervenciones = Enumerable.Range(1, 40).Select(i => new IntervencionActa(i, 1, i * 60, i * 60 + 50, new string('x', 2000))).ToList(),
        };

        await Secretario().RedactarAsync(_almacen.Guardar(p), Sin, CancellationToken.None);

        var partes = Actas.Secretario.Partes(p.ListaIntervenciones, Actas.Secretario.TamanoParte).Count;
        Assert.Equal(3, partes);
        Assert.Equal(partes + 1, _ia.Pedidos.Count);
        Assert.Contains("Notas de la reunión, por partes", _ia.Pedidos[^1]);
    }

    [Fact]
    public async Task Se_puede_cambiar_quien_habla_y_unir_personas()
    {
        var s = Secretario();
        var p = await s.TranscribirAsync(Nueva(), Sin, CancellationToken.None);

        p = s.CambiarHablante(p, 1, 2);
        Assert.Equal(2, p.ListaIntervenciones[0].Hablante);
        p = s.UnirParticipantes(p, 2, 1);
        Assert.Equal(1, Assert.Single(p.ListaParticipantes).Id);
        Assert.All(p.ListaIntervenciones, i => Assert.Equal(1, i.Hablante));
        Assert.Single(_almacen.Abrir(p.Id)!.ListaParticipantes);
    }

    [Fact]
    public void Exporta_a_Word_con_titulos_vinetas_negritas_y_tablas()
    {
        var ruta = Path.Combine(_carpeta, "acta.docx");
        Directory.CreateDirectory(_carpeta);
        const string md = "# Acta: Azor & cía\n**Fecha:** 9-10-2026\n## Tareas\n- Crear la **VPN** [11:07]\n\n| Tarea | Responsable |\n|---|---|\n| VPN | Raúl <IT> |\n";

        DocumentoWord.Guardar(md, ruta);

        using var zip = ZipFile.OpenRead(ruta);
        Assert.Equal(["[Content_Types].xml", "_rels/.rels", "word/_rels/document.xml.rels", "word/styles.xml", "word/document.xml"],
            zip.Entries.Select(e => e.FullName));
        var xml = DocumentoWord.Documento(md);
        Assert.Contains("""<w:pStyle w:val="Titulo1"/>""", xml);
        Assert.Contains("Acta: Azor &amp; cía", xml);
        Assert.Contains("""<w:rPr><w:b/></w:rPr><w:t xml:space="preserve">VPN</w:t>""", xml);
        Assert.Contains("Raúl &lt;IT&gt;", xml);
        Assert.Equal(4, xml.Split("<w:tc>").Length - 1);
        Assert.DoesNotContain("---", xml);
    }

    private sealed class MotoresFalsos : IMotoresActa
    {
        public int Extracciones;
        public int? Personas;

        public Task ExtraerAudioAsync(string grabacion, string wav, CancellationToken ct)
        {
            Extracciones++;
            File.WriteAllText(wav, "audio");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TramoTranscrito>> TranscribirAsync(string wav, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TramoTranscrito>>([new(0, 3, "Buenos días."), new(4, 9, "Hola, ¿qué tal?"), new(10, 14, "Empezamos.")]);

        public Task<IReadOnlyList<int>> HablantesAsync(string wav, IReadOnlyList<TramoTranscrito> tramos, int? personas, CancellationToken ct)
        {
            Personas = personas;
            return Task.FromResult<IReadOnlyList<int>>([1, 2, 1]);
        }
    }

    private sealed class IAFalsa : IModeloLenguaje
    {
        public Queue<string> Respuestas { get; } = new();
        public List<string> Pedidos { get; } = [];

        public Task<string> PreguntarAsync(string sistema, string pedido, int maxTokens, bool json, CancellationToken ct, double temperatura = 0.3)
        {
            Pedidos.Add(pedido);
            return Task.FromResult(Respuestas.Count > 0 ? Respuestas.Dequeue() : "- nota [00:01]");
        }
    }
}
