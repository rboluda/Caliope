using System.Text.Json;
using System.Text.Json.Serialization;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Doblaje;

/// <summary>Hasta dónde ha llegado la preparación de un doblaje (cada paso se guarda y se reanuda desde ahí).</summary>
public enum FaseDoblaje
{
    Nuevo,
    Separado,
    Transcrito,
    Traducido,
}

/// <summary>Si la frase doblada cabe en el tiempo que ocupaba la original.</summary>
public enum EncajeFrase
{
    SinGenerar,
    /// <summary>Cabe tal cual.</summary>
    Cabe,
    /// <summary>Cabe acelerándola un poco (sin cambiar el tono).</summary>
    Justa,
    /// <summary>Ni acelerándola al máximo: pisa la frase siguiente.</summary>
    NoCabe,
}

/// <param name="Original">Lo que se dice en el vídeo.</param>
/// <param name="Traduccion">Lo que se dirá en el doblaje (editable).</param>
/// <param name="Huella">De lo generado: si cambia la traducción o la voz, hay que regenerar.</param>
/// <param name="Tempo">Cuánto se aceleró para que quepa (1 = nada).</param>
public sealed record FraseDoblaje(
    int Id,
    int Hablante,
    double Inicio,
    double Fin,
    string Original,
    string Traduccion = "",
    EncajeFrase Encaje = EncajeFrase.SinGenerar,
    double? Duracion = null,
    double Tempo = 1.0,
    string? Huella = null)
{
    [JsonIgnore] public double Hueco => Fin - Inicio;
}

/// <param name="VozPronunciacion">Id de la voz de «Mis voces» que da la pronunciación castellana.</param>
/// <param name="ReferenciaInicio">Tramo de la voz original que se usa para su timbre.</param>
public sealed record HablanteDoblaje(int Id, string Nombre, string? VozPronunciacion, double ReferenciaInicio, double ReferenciaFin);

/// <param name="Video">Ruta del vídeo original (no se copia).</param>
public sealed record ProyectoDoblaje(
    string Id,
    string Nombre,
    string Video,
    double Duracion,
    DateTimeOffset Creado,
    FaseDoblaje Fase = FaseDoblaje.Nuevo,
    string IdiomaOrigen = "English",
    bool VozOriginalDeFondo = false,
    IReadOnlyList<HablanteDoblaje>? Hablantes = null,
    IReadOnlyList<FraseDoblaje>? Frases = null)
{
    [JsonIgnore] public IReadOnlyList<HablanteDoblaje> ListaHablantes => Hablantes ?? [];
    [JsonIgnore] public IReadOnlyList<FraseDoblaje> ListaFrases => Frases ?? [];

    public ProyectoDoblaje ConFrase(FraseDoblaje f) =>
        this with { Frases = ListaFrases.Select(x => x.Id == f.Id ? f : x).ToList() };
}

/// <summary>
/// Los doblajes, en «Mis doblajes»: una carpeta por proyecto con proyecto.json, «trabajo» (audio del vídeo, voz y
/// fondo separados, referencias), «frases» (cada frase doblada) y «Audio» (lo exportado).
/// </summary>
public sealed class AlmacenDoblajes(string carpeta)
{
    /// <summary>Duración máxima de un vídeo en la primera versión.</summary>
    public static readonly TimeSpan DuracionMaxima = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Carpeta => carpeta;

    public string CarpetaDe(string id) => Path.Combine(carpeta, id);
    public string Trabajo(string id) => Path.Combine(CarpetaDe(id), "trabajo");
    public string Frases(string id) => Path.Combine(CarpetaDe(id), "frases");
    public string Salida(string id) => Path.Combine(CarpetaDe(id), "Audio");

    public ProyectoDoblaje Crear(string video, double duracion)
    {
        if (duracion > DuracionMaxima.TotalSeconds)
            throw new InvalidOperationException(T("El vídeo dura {0}; de momento se pueden doblar vídeos de hasta {1} minutos.",
                TimeSpan.FromSeconds(duracion).ToString(@"m\:ss"), DuracionMaxima.TotalMinutes));
        var nombre = Path.GetFileNameWithoutExtension(video);
        var baseId = Textos.Slug(nombre, "doblaje");
        var id = baseId;
        for (var n = 2; Directory.Exists(CarpetaDe(id)); n++)
            id = $"{baseId}-{n}";
        var p = new ProyectoDoblaje(id, nombre, video, Math.Round(duracion, 2), DateTimeOffset.Now);
        return Guardar(p);
    }

    public ProyectoDoblaje Guardar(ProyectoDoblaje p)
    {
        Directory.CreateDirectory(CarpetaDe(p.Id));
        var ruta = Path.Combine(CarpetaDe(p.Id), "proyecto.json");
        var temporal = ruta + ".tmp";
        File.WriteAllText(temporal, JsonSerializer.Serialize(p, Opciones));
        File.Move(temporal, ruta, overwrite: true);
        return p;
    }

    public ProyectoDoblaje? Abrir(string id)
    {
        var ruta = Path.Combine(CarpetaDe(id), "proyecto.json");
        return File.Exists(ruta) ? JsonSerializer.Deserialize<ProyectoDoblaje>(File.ReadAllText(ruta), Opciones) : null;
    }

    /// <summary>Los proyectos, del más reciente al más antiguo.</summary>
    public IReadOnlyList<ProyectoDoblaje> Listar() => !Directory.Exists(carpeta)
        ? []
        : Directory.EnumerateDirectories(carpeta)
            .Select(d => Abrir(Path.GetFileName(d)))
            .OfType<ProyectoDoblaje>()
            .OrderByDescending(p => p.Creado)
            .ToList();

    public void Borrar(string id)
    {
        if (Directory.Exists(CarpetaDe(id)))
            Directory.Delete(CarpetaDe(id), recursive: true);
    }
}
