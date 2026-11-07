using System.Text.Json;
using System.Text.Json.Serialization;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Actas;

/// <summary>Hasta dónde ha llegado un acta (cada paso se guarda).</summary>
public enum FaseActa
{
    Nueva,
    Transcrita,
    Redactada,
}

/// <summary>Lo que dice una persona seguido (los tramos seguidos de la misma persona, juntos).</summary>
public sealed record IntervencionActa(int Id, int Hablante, double Inicio, double Fin, string Texto);

/// <param name="Nombre">Como aparece en el acta: «Raúl (Informática de Azor)».</param>
/// <param name="MuestraInicio">Un trozo de su voz, para reconocerla al escucharlo.</param>
public sealed record ParticipanteActa(int Id, string Nombre, double MuestraInicio, double MuestraFin);

/// <param name="Grabacion">Ruta del audio o vídeo de la reunión (no se copia).</param>
/// <param name="Fecha">Día de la reunión: con él, «la semana que viene» pasa a ser una fecha.</param>
/// <param name="Personas">Cuántas personas hablan, si se sabe; null: lo decide Calíope.</param>
/// <param name="Glosario">Nombres y términos tal como se escriben (personas, empresas, marcas, siglas).</param>
public sealed record ProyectoActa(
    string Id,
    string Titulo,
    string Grabacion,
    double Duracion,
    DateTimeOffset Creado,
    DateOnly Fecha,
    FaseActa Fase = FaseActa.Nueva,
    int? Personas = null,
    string Glosario = "",
    IReadOnlyList<ParticipanteActa>? Participantes = null,
    IReadOnlyList<IntervencionActa>? Intervenciones = null)
{
    [JsonIgnore] public IReadOnlyList<ParticipanteActa> ListaParticipantes => Participantes ?? [];
    [JsonIgnore] public IReadOnlyList<IntervencionActa> ListaIntervenciones => Intervenciones ?? [];

    public ProyectoActa ConIntervencion(IntervencionActa i) =>
        this with { Intervenciones = ListaIntervenciones.Select(x => x.Id == i.Id ? i : x).ToList() };
}

/// <summary>
/// Las actas, en «Mis actas»: una carpeta por reunión con proyecto.json, acta.md (el acta, editable) y «trabajo»
/// (el audio preparado para transcribir).
/// </summary>
public sealed class AlmacenActas(string carpeta)
{
    /// <summary>Más de 4 horas de reunión no caben en el modelo de lenguaje ni por partes.</summary>
    public static readonly TimeSpan DuracionMaxima = TimeSpan.FromHours(4);

    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Carpeta => carpeta;

    public string CarpetaDe(string id) => Path.Combine(carpeta, id);
    public string Trabajo(string id) => Path.Combine(CarpetaDe(id), "trabajo");
    public string Audio(string id) => Path.Combine(Trabajo(id), "audio.wav");
    public string ArchivoActa(string id) => Path.Combine(CarpetaDe(id), "acta.md");

    public ProyectoActa Crear(string grabacion, double duracion, DateOnly fecha)
    {
        if (duracion > DuracionMaxima.TotalSeconds)
            throw new InvalidOperationException(T("La grabación dura {0}; de momento se pueden hacer actas de reuniones de hasta {1} horas.",
                TimeSpan.FromSeconds(duracion).ToString(@"h\:mm\:ss"), DuracionMaxima.TotalHours));
        var titulo = Path.GetFileNameWithoutExtension(grabacion);
        var baseId = Textos.Slug(titulo, "acta");
        var id = baseId;
        for (var n = 2; Directory.Exists(CarpetaDe(id)); n++)
            id = $"{baseId}-{n}";
        return Guardar(new ProyectoActa(id, titulo, grabacion, Math.Round(duracion, 2), DateTimeOffset.Now, fecha));
    }

    public ProyectoActa Guardar(ProyectoActa p)
    {
        Directory.CreateDirectory(CarpetaDe(p.Id));
        var ruta = Path.Combine(CarpetaDe(p.Id), "proyecto.json");
        var temporal = ruta + ".tmp";
        File.WriteAllText(temporal, JsonSerializer.Serialize(p, Opciones));
        File.Move(temporal, ruta, overwrite: true);
        return p;
    }

    public ProyectoActa? Abrir(string id)
    {
        var ruta = Path.Combine(CarpetaDe(id), "proyecto.json");
        return File.Exists(ruta) ? JsonSerializer.Deserialize<ProyectoActa>(File.ReadAllText(ruta), Opciones) : null;
    }

    /// <summary>Las actas, de la más reciente a la más antigua.</summary>
    public IReadOnlyList<ProyectoActa> Listar() => !Directory.Exists(carpeta)
        ? []
        : Directory.EnumerateDirectories(carpeta)
            .Select(d => Abrir(Path.GetFileName(d)))
            .OfType<ProyectoActa>()
            .OrderByDescending(p => p.Creado)
            .ToList();

    public string? LeerActa(string id) => File.Exists(ArchivoActa(id)) ? File.ReadAllText(ArchivoActa(id)) : null;

    public void GuardarActa(string id, string texto)
    {
        Directory.CreateDirectory(CarpetaDe(id));
        File.WriteAllText(ArchivoActa(id), texto);
    }

    public void Borrar(string id)
    {
        if (Directory.Exists(CarpetaDe(id)))
            Directory.Delete(CarpetaDe(id), recursive: true);
    }
}
