using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Constantino.Core.IA;
using Constantino.Core.Motores;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Actas;

/// <summary>Los motores que necesita el acta (FFmpeg, Whisper y la huella de voz del motor principal).</summary>
public interface IMotoresActa
{
    /// <summary>El audio de la grabación (o del vídeo), listo para transcribir.</summary>
    Task ExtraerAudioAsync(string grabacion, string wav, CancellationToken ct);

    /// <summary>Tramos cortados por las pausas, con los trozos muy cortos unidos y sin lo que Whisper se inventa.</summary>
    Task<IReadOnlyList<TramoTranscrito>> TranscribirAsync(string wav, CancellationToken ct);

    /// <summary>Quién dice cada tramo (personas desde 1, por orden de aparición). Con <paramref name="personas"/>, cuántas son.</summary>
    Task<IReadOnlyList<int>> HablantesAsync(string wav, IReadOnlyList<TramoTranscrito> tramos, int? personas, CancellationToken ct);
}

public sealed record EventoActa(string Mensaje, double? Fraccion = null);

/// <summary>
/// El secretario de la reunión: transcribe la grabación, distingue a las personas que hablan, propone quién es cada
/// una y redacta el acta con la IA local. Las fechas relativas se convierten con la fecha de la reunión, los términos
/// se escriben como dice el glosario, y cada punto lleva el minuto en que se dijo para poder comprobarlo.
/// </summary>
public sealed class Secretario(AlmacenActas almacen, IMotoresActa motores, IModeloLenguaje ia)
{
    /// <summary>Hasta aquí (caracteres de transcripción), el acta sale de una vez; con más, por partes.</summary>
    public const int LimiteDeUnaVez = 60_000;
    /// <summary>Tamaño de cada parte en una reunión larga.</summary>
    public const int TamanoParte = 36_000;

    /// <summary>Transcribe y separa por personas. Rehacerlo borra los nombres puestos.</summary>
    public async Task<ProyectoActa> TranscribirAsync(ProyectoActa p, IProgress<EventoActa> progreso, CancellationToken ct)
    {
        var audio = almacen.Audio(p.Id);
        if (!File.Exists(audio))
        {
            progreso.Report(new EventoActa(T("Preparando el audio de la reunión"), 0.02));
            Directory.CreateDirectory(almacen.Trabajo(p.Id));
            await motores.ExtraerAudioAsync(p.Grabacion, audio, ct).ConfigureAwait(false);
        }
        progreso.Report(new EventoActa(T("Escuchando la reunión y pasándola a texto"), 0.1));
        var tramos = await motores.TranscribirAsync(audio, ct).ConfigureAwait(false);
        if (tramos.Count == 0)
            throw new InvalidOperationException(T("No se ha encontrado ninguna voz en la grabación."));
        progreso.Report(new EventoActa(T("Distinguiendo a las personas que hablan"), 0.75));
        var hablantes = await motores.HablantesAsync(audio, tramos, p.Personas, ct).ConfigureAwait(false);
        var (intervenciones, participantes) = Agrupar(tramos, hablantes);
        p = almacen.Guardar(p with { Fase = FaseActa.Transcrita, Intervenciones = intervenciones, Participantes = participantes });
        progreso.Report(new EventoActa(T("Reunión transcrita: {0} personas, {1} intervenciones", participantes.Count, intervenciones.Count), 1));
        return p;
    }

    /// <summary>
    /// Une los tramos seguidos de la misma persona (con menos de 2 s entre ellos y hasta 90 s) en intervenciones, y de
    /// cada persona elige como muestra de su voz su tramo más largo (hasta 10 s).
    /// </summary>
    public static (List<IntervencionActa> Intervenciones, List<ParticipanteActa> Participantes) Agrupar(
        IReadOnlyList<TramoTranscrito> tramos, IReadOnlyList<int> hablantes)
    {
        var intervenciones = new List<IntervencionActa>();
        for (var i = 0; i < tramos.Count; i++)
        {
            var t = tramos[i];
            var h = i < hablantes.Count ? hablantes[i] : 1;
            if (intervenciones.Count > 0 && intervenciones[^1] is var u && u.Hablante == h && t.Inicio - u.Fin < 2 && t.Fin - u.Inicio <= 90)
                intervenciones[^1] = u with { Fin = t.Fin, Texto = u.Texto + " " + t.Texto };
            else
                intervenciones.Add(new IntervencionActa(intervenciones.Count + 1, h, t.Inicio, t.Fin, t.Texto));
        }
        var participantes = tramos.Select((t, i) => (Tramo: t, Hablante: i < hablantes.Count ? hablantes[i] : 1))
            .GroupBy(x => x.Hablante)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var muestra = g.MaxBy(x => x.Tramo.Fin - x.Tramo.Inicio).Tramo;
                return new ParticipanteActa(g.Key, T("Persona {0}", g.Key), muestra.Inicio, Math.Min(muestra.Fin, muestra.Inicio + 10));
            })
            .ToList();
        return (intervenciones, participantes);
    }

    public static string Reloj(double segundos)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, segundos));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>La transcripción por intervenciones: «[mm:ss] Nombre: texto».</summary>
    public static string Transcripcion(ProyectoActa p, IEnumerable<IntervencionActa>? intervenciones = null)
    {
        var nombres = p.ListaParticipantes.ToDictionary(x => x.Id, x => x.Nombre);
        return string.Join("\n", (intervenciones ?? p.ListaIntervenciones).Select(i =>
            $"[{Reloj(i.Inicio)}] {nombres.GetValueOrDefault(i.Hablante, T("Persona {0}", i.Hablante))}: {i.Texto}"));
    }

    private static string Contexto(ProyectoActa p) =>
        $"Reunión: {p.Titulo}\nFecha de la reunión: {p.Fecha.ToString("dddd, d 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("es-ES"))}\n"
        + (string.IsNullOrWhiteSpace(p.Glosario)
            ? ""
            : $"Glosario (así se escriben los nombres y términos que el reconocimiento de voz puede haber entendido mal): {p.Glosario.Trim()}\n")
        + "\n";

    private const string InstruccionesNombres = """
        Te doy la transcripción automática de una reunión. Cada línea lleva quién habla («Persona N»), pero la separación
        por voces no es perfecta: algunas frases cortas pueden estar atribuidas a otra persona.
        Deduce quién es cada «Persona N»: su nombre (si otros se dirigen a ella por su nombre o ella se presenta) y su papel
        (empresa o departamento). Fíjate en a quién se dirigen («Sí, hola Raúl», «Tú lo sabes, Gerard») y en lo que cada
        uno dice de sí mismo («mi red», «nuestro técnico»). Si no estás seguro, no pongas nombre.
        Responde solo con JSON: {"personas": [{"id": 1, "nombre": "…" o null, "papel": "…" o null}]}
        """;

    /// <summary>
    /// La IA propone quién es cada persona; solo se aplica a las que aún se llaman «Persona N». Hay que confirmarlo:
    /// ningún modelo local lo acierta siempre.
    /// </summary>
    public async Task<ProyectoActa> ProponerNombresAsync(ProyectoActa p, IProgress<EventoActa> progreso, CancellationToken ct)
    {
        progreso.Report(new EventoActa(T("Buscando cómo se llama cada persona"), 0.1));
        var respuesta = await ia.PreguntarAsync(InstruccionesNombres, Contexto(p) + "Transcripción:\n" + Transcripcion(p), 1500, true, ct)
            .ConfigureAwait(false);
        var propuestos = LeerNombres(respuesta);
        p = p with
        {
            Participantes = p.ListaParticipantes.Select(x => x.Nombre == T("Persona {0}", x.Id) && propuestos.TryGetValue(x.Id, out var n)
                ? x with { Nombre = n }
                : x).ToList(),
        };
        progreso.Report(new EventoActa(T("Nombres propuestos: revísalos antes de redactar el acta."), 1));
        return almacen.Guardar(p);
    }

    /// <summary>{"personas": [{"id", "nombre", "papel"}]} → id → «Nombre (papel)»; sin nombre, no se propone nada.</summary>
    public static Dictionary<int, string> LeerNombres(string json)
    {
        var nombres = new Dictionary<int, string>();
        JsonNode? raiz;
        try
        {
            raiz = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return nombres;
        }
        if (raiz?["personas"] is not JsonArray lista)
            return nombres;
        foreach (var x in lista.OfType<JsonObject>())
        {
            var id = x["id"] is JsonValue v && (v.TryGetValue<int>(out var n) || int.TryParse(v.ToString(), out n)) ? n : -1;
            var nombre = x["nombre"] is JsonValue vn && vn.TryGetValue<string>(out var s) ? s.Trim() : "";
            var papel = x["papel"] is JsonValue vp && vp.TryGetValue<string>(out var q) ? q.Trim() : "";
            if (id > 0 && nombre.Length > 0 && !nombre.StartsWith("Persona", StringComparison.OrdinalIgnoreCase))
                nombres[id] = papel.Length > 0 ? $"{nombre} ({papel})" : nombre;
        }
        return nombres;
    }

    private const string InstruccionesActa = """
        Eres el secretario de una reunión de trabajo y redactas su acta en castellano de España, clara, concisa y
        profesional. Te doy la transcripción automática, que puede tener errores de reconocimiento y frases atribuidas a
        otra persona: interpreta con sentido común.

        Reglas:
        - NO inventes nada que no se diga. Si falta un dato (responsable, plazo…), pon «sin especificar».
        - Las fechas relativas («la semana que viene», «el lunes») conviértelas en fechas reales a partir de la fecha de
          la reunión. No añadas ninguna otra fecha (ni la de redacción del acta) ni firmas.
        - Las empresas, marcas, administraciones y máquinas no son personas.
        - No desarrolles siglas salvo que se desarrollen en la reunión.
        - Llama a cada participante por su nombre, con su empresa o papel entre paréntesis la primera vez.
        - Termina cada viñeta de «Puntos clave», cada decisión y cada tarea con el minuto en que se habla de ello, entre
          corchetes: [12:34]. En la tabla de tareas, el minuto va en la columna «Notas».
        - No repitas la misma tarea dos veces.

        Formato (Markdown):
        # Acta: <título breve>
        **Fecha:** … · **Participantes:** … · **Mencionados:** solo personas con nombre que se nombran pero no participan
        (no empresas, marcas ni términos del glosario); si no hay ninguna, quita «Mencionados».
        ## Puntos clave
        Viñetas con lo esencial de lo que se habló, con cifras, equipos y nombres concretos, cada una con su [mm:ss].
        ## Decisiones tomadas
        ## Tareas
        | Tarea | Responsable | Plazo | Notas |
        ## Fechas límite
        ## Riesgos y problemas
        ## Seguimiento
        """;

    private const string InstruccionesNotas = """
        Eres el secretario de una reunión de trabajo larga y tomas notas de una parte de ella, en castellano de España.
        Te doy un trozo de la transcripción automática (puede tener errores: interpreta con sentido común).
        Anota en viñetas, de forma concisa y sin inventar nada: temas tratados (con cifras, equipos y nombres), decisiones,
        tareas (quién, qué y plazo si se dice), fechas y problemas. Cada viñeta termina con su minuto entre corchetes: [12:34].
        """;

    /// <summary>
    /// Redacta el acta y la guarda (acta.md). Si la reunión es larga, primero toma notas por partes y redacta con ellas.
    /// </summary>
    public async Task<(ProyectoActa Proyecto, string Acta)> RedactarAsync(ProyectoActa p, IProgress<EventoActa> progreso, CancellationToken ct)
    {
        if (p.ListaIntervenciones.Count == 0)
            throw new InvalidOperationException(T("Primero transcribe la reunión."));
        var contexto = Contexto(p);
        var partes = Partes(p.ListaIntervenciones, TamanoParte);
        string material;
        if (Transcripcion(p).Length <= LimiteDeUnaVez)
        {
            material = "Transcripción:\n" + Transcripcion(p);
        }
        else
        {
            var notas = new StringBuilder();
            for (var k = 0; k < partes.Count; k++)
            {
                progreso.Report(new EventoActa(T("Tomando notas de la parte {0} de {1}", k + 1, partes.Count), 0.05 + 0.6 * k / partes.Count));
                var desde = Reloj(partes[k][0].Inicio);
                var hasta = Reloj(partes[k][^1].Fin);
                var nota = await ia.PreguntarAsync(InstruccionesNotas,
                    contexto + $"Parte {k + 1} de {partes.Count} (del minuto {desde} al {hasta}):\n" + Transcripcion(p, partes[k]), 2500, false, ct)
                    .ConfigureAwait(false);
                notas.AppendLine($"### Parte {k + 1} ({desde}–{hasta})").AppendLine(nota.Trim()).AppendLine();
            }
            material = "Participantes: " + string.Join("; ", p.ListaParticipantes.Select(x => x.Nombre)) + "\n\nNotas de la reunión, por partes:\n" + notas;
        }
        progreso.Report(new EventoActa(T("Redactando el acta"), 0.7));
        var acta = (await ia.PreguntarAsync(InstruccionesActa, contexto + material, 6000, false, ct).ConfigureAwait(false)).Trim();
        acta = QuitarMencionadosVacios(QuitarBloque(acta));
        almacen.GuardarActa(p.Id, acta + "\n");
        p = almacen.Guardar(p with { Fase = FaseActa.Redactada });
        progreso.Report(new EventoActa(T("Acta redactada"), 1));
        return (p, acta);
    }

    /// <summary>Las intervenciones en partes de unos <paramref name="tamano"/> caracteres de transcripción.</summary>
    public static List<List<IntervencionActa>> Partes(IReadOnlyList<IntervencionActa> intervenciones, int tamano)
    {
        var partes = new List<List<IntervencionActa>> { new() };
        var actual = 0;
        foreach (var i in intervenciones)
        {
            if (actual > 0 && actual + i.Texto.Length > tamano)
            {
                partes.Add([]);
                actual = 0;
            }
            partes[^1].Add(i);
            actual += i.Texto.Length + 40;
        }
        return partes;
    }

    /// <summary>A veces el modelo envuelve la respuesta en un bloque ```markdown … ```: se quita.</summary>
    public static string QuitarBloque(string texto)
    {
        var t = texto.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;
        var primera = t.IndexOf('\n');
        var ultima = t.LastIndexOf("```", StringComparison.Ordinal);
        return primera > 0 && ultima > primera ? t[(primera + 1)..ultima].Trim() : t;
    }

    /// <summary>«Mencionados: ninguno» (o «sin ninguno», «no se indica»…) sobra: se quita la línea o el trozo.</summary>
    public static string QuitarMencionadosVacios(string acta) =>
        System.Text.RegularExpressions.Regex.Replace(acta,
            @"(\s*·\s*)?\*\*Mencionados:\*\*\s*(sin\b[^\n·]*|ningun[oa]s?\b[^\n·]*|no (hay|se indica)\b[^\n·]*|—|-)\s*(?=\n|$)",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline)
            .Replace("\n\n\n", "\n\n");

    /// <summary>La intervención pasa a otra persona.</summary>
    public ProyectoActa CambiarHablante(ProyectoActa p, int intervencion, int hablante) =>
        almacen.Guardar(p.ConIntervencion(p.ListaIntervenciones.First(i => i.Id == intervencion) with { Hablante = hablante }));

    /// <summary><paramref name="origen"/> y <paramref name="destino"/> eran la misma persona: sus intervenciones pasan a la segunda.</summary>
    public ProyectoActa UnirParticipantes(ProyectoActa p, int origen, int destino)
    {
        if (origen == destino || p.ListaParticipantes.All(x => x.Id != destino))
            return p;
        return almacen.Guardar(p with
        {
            Intervenciones = p.ListaIntervenciones.Select(i => i.Hablante == origen ? i with { Hablante = destino } : i).ToList(),
            Participantes = p.ListaParticipantes.Where(x => x.Id != origen).ToList(),
        });
    }
}
