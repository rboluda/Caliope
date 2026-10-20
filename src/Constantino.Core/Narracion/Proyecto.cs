using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constantino.Core.Voces;

namespace Constantino.Core.Narracion;

public enum FormatoSalida
{
    Mp3,
    Wav,
}

/// <summary>Cómo se narra un proyecto. Todo lo que cambia el audio de un bloque entra en su huella.</summary>
public sealed record AjustesNarracion(
    string? VozId = null,
    int Semilla = 1234,
    double Temperatura = 0.9,
    double Pausa = 0.6,
    double PausaLarga = 1.5,
    double PausaEntreFrases = 0.35,
    double SusurroDb = -8,
    int MaxCaracteres = 400,
    double SilencioInicio = 0.15,
    double SilencioFin = 0.4,
    double PausaEntreBloques = 0.3,
    double Lufs = -16,
    double PicoDb = -1.5,
    FormatoSalida Formato = FormatoSalida.Mp3,
    string Bitrate = "192k",
    int Frecuencia = 48000,
    bool RecortarSilencios = true,
    double UmbralSilencioDb = -50,
    double MargenRecorte = 0.05,
    double Velocidad = 1.0,
    double TonoSemitonos = 0)
{
    /// <summary>Velocidad entre 0,8 y 1,2: más allá se nota artificial.</summary>
    public const double VelocidadMinima = 0.8, VelocidadMaxima = 1.2, TonoMaximo = 3;

    [JsonIgnore]
    public bool CambiaVelocidadOTono => Math.Abs(Velocidad - 1) > 0.001 || Math.Abs(TonoSemitonos) > 0.001;
}

/// <summary>Un bloque ya generado: con qué huella y en qué archivo.</summary>
public sealed record EstadoBloque(
    int Indice,
    string Nombre,
    string Huella,
    string Archivo,
    double Duracion,
    IReadOnlyList<string> Avisos,
    DateTimeOffset Generado);

public sealed record Proyecto(
    string Id,
    string Nombre,
    DateTimeOffset Creado,
    DateTimeOffset Modificado,
    AjustesNarracion Ajustes,
    IReadOnlyList<EstadoBloque> Bloques);

public static class Huellas
{
    /// <summary>
    /// Identifica el audio de un bloque: si no cambia el texto, la voz, el modelo ni los ajustes que afectan
    /// al sonido, el bloque generado sigue valiendo y no se repite.
    /// </summary>
    /// <param name="firmaVoz">Algo que cambie si cambia la voz (id, transcripción, grabación de referencia).</param>
    public static string DeBloque(Bloque bloque, AjustesNarracion ajustes, string firmaVoz, string modelo)
    {
        // La pausa entre bloques solo afecta al audio completo, no a cada bloque.
        var relevantes = JsonSerializer.SerializeToNode(ajustes with { PausaEntreBloques = 0, VozId = null })!.AsObject();
        // Ajustes añadidos en versiones posteriores: con su valor por defecto no cuentan, para que lo generado
        // antes siga valiendo.
        if (!ajustes.CambiaVelocidadOTono)
        {
            relevantes.Remove(nameof(AjustesNarracion.Velocidad));
            relevantes.Remove(nameof(AjustesNarracion.TonoSemitonos));
        }
        var datos = JsonSerializer.Serialize(new
        {
            bloque.Nombre,
            Eventos = bloque.Eventos.Select(e => e switch
            {
                // La variante solo entra cuando hay una distinta del susurro: así las huellas de los bloques
                // generados antes de existir las variantes siguen valiendo.
                EventoVoz v when v.Variante is null or CatalogoVariantes.Susurro => $"voz:{(v.Susurro ? "s" : "n")}:{v.Texto}",
                EventoVoz v => $"voz:{v.Variante}:{v.Texto}",
                EventoSilencio s => $"silencio:{s.Segundos:0.###}",
                _ => "",
            }),
            Ajustes = relevantes,
            Voz = firmaVoz,
            Modelo = modelo,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(datos)))[..16];
    }
}

/// <summary>Proyectos en disco: <c>proyectos/&lt;id&gt;/proyecto.json</c>, <c>guion.txt</c> y <c>salida/</c>.</summary>
public sealed class AlmacenProyectos(string carpeta)
{
    public const string ArchivoProyecto = "proyecto.json", ArchivoGuion = "guion.txt", CarpetaSalidaNombre = "salida";

    public const string GuionDeEjemplo = """
        // Escribe aquí tu guion. Cada párrafo (separado por una línea en blanco) es un bloque y da un archivo de audio.
        // Etiquetas: [pause] pausa corta · [long pause] pausa larga · [whispers] susurra hasta el final de la línea.
        // Las líneas que empiezan por // son comentarios y no se leen.

        # inicio
        Sábado por la noche. [pause] Diluvia.

        # historia
        En la puerta de la discoteca hay una chica con un vestido de verano, empapada, temblando de frío. [long pause]
        [whispers] Y nadie sabe de dónde ha salido.
        """;

    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Carpeta => carpeta;

    public IReadOnlyList<Proyecto> Listar()
    {
        if (!Directory.Exists(carpeta))
            return [];
        return Directory.EnumerateDirectories(carpeta)
            .Select(d => Leer(Path.GetFileName(d)))
            .OfType<Proyecto>()
            .OrderByDescending(p => p.Modificado)
            .ToList();
    }

    public Proyecto Crear(string nombre, AjustesNarracion ajustes, string? guion = null)
    {
        nombre = nombre.Trim();
        if (nombre.Length == 0)
            throw new ArgumentException("El proyecto necesita un nombre.", nameof(nombre));
        var baseId = Textos.Slug(nombre, "proyecto");
        var id = baseId;
        for (var n = 2; Directory.Exists(CarpetaDe(id)); n++)
            id = $"{baseId}-{n}";
        Directory.CreateDirectory(CarpetaDe(id));
        var ahora = DateTimeOffset.Now;
        var proyecto = new Proyecto(id, nombre, ahora, ahora, ajustes, []);
        Guardar(proyecto);
        GuardarGuion(id, guion ?? GuionDeEjemplo);
        return proyecto;
    }

    public Proyecto? Leer(string id)
    {
        var ruta = Path.Combine(CarpetaDe(id), ArchivoProyecto);
        if (!File.Exists(ruta))
            return null;
        try
        {
            var p = JsonSerializer.Deserialize<Proyecto>(File.ReadAllText(ruta), Opciones);
            return p is null ? null : p with { Id = id, Ajustes = p.Ajustes ?? new AjustesNarracion(), Bloques = p.Bloques ?? [] };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string LeerGuion(string id)
    {
        var ruta = Path.Combine(CarpetaDe(id), ArchivoGuion);
        return File.Exists(ruta) ? File.ReadAllText(ruta) : "";
    }

    public void GuardarGuion(string id, string texto) =>
        EscribirAtomico(Path.Combine(CarpetaDe(id), ArchivoGuion), texto);

    public Proyecto Guardar(Proyecto proyecto)
    {
        var p = proyecto with { Modificado = DateTimeOffset.Now };
        EscribirAtomico(Path.Combine(CarpetaDe(p.Id), ArchivoProyecto), JsonSerializer.Serialize(p, Opciones));
        return p;
    }

    public void Borrar(string id)
    {
        if (Directory.Exists(CarpetaDe(id)))
            Directory.Delete(CarpetaDe(id), recursive: true);
    }

    public string CarpetaDe(string id) => Path.Combine(carpeta, id);

    public string CarpetaSalida(string id) => Path.Combine(CarpetaDe(id), CarpetaSalidaNombre);

    private static void EscribirAtomico(string ruta, string contenido)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        var temporal = ruta + ".tmp";
        File.WriteAllText(temporal, contenido);
        File.Move(temporal, ruta, overwrite: true);
    }
}
