using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constantino.Core.Motores;
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
    double TonoSemitonos = 0,
    string Motor = MotoresDeVoz.Qwen,
    double Intensidad = 0.5)
{
    /// <summary>Velocidad entre 0,8 y 1,2: más allá se nota artificial.</summary>
    public const double VelocidadMinima = 0.8, VelocidadMaxima = 1.2, TonoMaximo = 3;

    /// <summary>Intensidad emocional de Chatterbox: 0,25 contenida, 0,5 natural, 1 exagerada.</summary>
    public const double IntensidadMinima = 0.25, IntensidadMaxima = 1.0;

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

/// <param name="Tomas">Semilla elegida para algunos bloques («Otras tomas»), por la clave de su texto.</param>
public sealed record Proyecto(
    string Id,
    string Nombre,
    DateTimeOffset Creado,
    DateTimeOffset Modificado,
    AjustesNarracion Ajustes,
    IReadOnlyList<EstadoBloque> Bloques,
    IReadOnlyDictionary<string, int>? Tomas = null)
{
    /// <summary>Los ajustes con los que se genera <paramref name="bloque"/>: los del proyecto con su toma elegida.</summary>
    public AjustesNarracion AjustesDe(Bloque bloque) =>
        Tomas is not null && Tomas.TryGetValue(Huellas.ClaveBloque(bloque), out var semilla)
            ? Ajustes with { Semilla = semilla }
            : Ajustes;
}

public static class Huellas
{
    /// <summary>
    /// Identifica el audio de un bloque: si no cambia el texto, la voz, el modelo ni los ajustes que afectan
    /// al sonido, el bloque generado sigue valiendo y no se repite.
    /// </summary>
    /// <param name="firmaVoz">Algo que cambie si cambia la voz (id, transcripción, grabación de referencia).</param>
    /// <param name="firmaOtraVoz">Firma de las voces de [voz: …] (por id).</param>
    /// <param name="firmaEfecto">Firma de los efectos de [sfx: …] (por nombre).</param>
    public static string DeBloque(Bloque bloque, AjustesNarracion ajustes, string firmaVoz, string modelo,
        Func<string, string>? firmaOtraVoz = null, Func<string, string>? firmaEfecto = null)
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
        // El motor (y sus ajustes propios) solo cuentan si no es Qwen, el único que había antes.
        if (ajustes.Motor == MotoresDeVoz.Qwen)
        {
            relevantes.Remove(nameof(AjustesNarracion.Motor));
            relevantes.Remove(nameof(AjustesNarracion.Intensidad));
        }
        var datos = JsonSerializer.Serialize(new
        {
            bloque.Nombre,
            Eventos = bloque.Eventos.Select(e => e switch
            {
                // La variante solo entra cuando hay una distinta del susurro: así las huellas de los bloques
                // generados antes de existir las variantes siguen valiendo.
                EventoVoz { VozId: { } otra } v => $"voz@{otra}={firmaOtraVoz?.Invoke(otra)}:{(v.Susurro ? "s" : "n")}:{v.Texto}",
                EventoVoz v when v.Variante is null or CatalogoVariantes.Susurro => $"voz:{(v.Susurro ? "s" : "n")}:{v.Texto}",
                EventoVoz v => $"voz:{v.Variante}:{v.Texto}",
                EventoSilencio s => $"silencio:{s.Segundos:0.###}",
                EventoEfecto ef => $"efecto:{ef.Nombre}={firmaEfecto?.Invoke(ef.Nombre)}",
                _ => "",
            }),
            Ajustes = relevantes,
            Voz = firmaVoz,
            Modelo = modelo,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(datos)))[..16];
    }

    /// <summary>
    /// Identifica un bloque por su contenido (nombre y lo que se lee), no por su posición: así la toma elegida
    /// sigue con su bloque aunque se añadan párrafos antes.
    /// </summary>
    public static string ClaveBloque(Bloque bloque)
    {
        var datos = bloque.Nombre + "\n" + string.Join("\n", bloque.Eventos.Select(e => e.ToString()));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(datos)))[..12];
    }

    /// <summary>Cambia si cambia el archivo (tamaño o fecha): para efectos y sonidos.</summary>
    public static string FirmaArchivo(string? ruta)
    {
        if (ruta is null || !File.Exists(ruta))
            return "0";
        var info = new FileInfo(ruta);
        return $"{info.Length}-{info.LastWriteTimeUtc.Ticks}";
    }
}

/// <summary>Proyectos en disco: <c>proyectos/&lt;id&gt;/proyecto.json</c>, <c>guion.txt</c> y <c>salida/</c>.</summary>
public sealed class AlmacenProyectos(string carpeta)
{
    public const string ArchivoProyecto = "proyecto.json", ArchivoGuion = "guion.txt", CarpetaSalidaNombre = "Audio";

    /// <summary>Nombre de la carpeta de audio en versiones anteriores.</summary>
    private const string CarpetaSalidaAntigua = "salida";

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

    public string CarpetaSalida(string id)
    {
        var nueva = Path.Combine(CarpetaDe(id), CarpetaSalidaNombre);
        var antigua = Path.Combine(CarpetaDe(id), CarpetaSalidaAntigua);
        if (Directory.Exists(antigua) && !Directory.Exists(nueva))
        {
            try { Directory.Move(antigua, nueva); }
            catch (IOException) { return antigua; } // en uso (p. ej. sonando): se renombra la próxima vez
        }
        return nueva;
    }

    private static void EscribirAtomico(string ruta, string contenido)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        var temporal = ruta + ".tmp";
        File.WriteAllText(temporal, contenido);
        File.Move(temporal, ruta, overwrite: true);
    }
}
