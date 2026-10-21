using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Constantino.Core;

/// <summary>
/// El idioma de la interfaz. Los textos se escriben en español en el código y en el XAML, y ese mismo texto es la
/// clave para buscar la traducción en Idiomas/&lt;idioma&gt;.json (recurso incrustado). Si falta una traducción se
/// muestra el español: nunca una clave vacía. El idioma se elige al arrancar; cambiarlo pide reiniciar.
/// </summary>
public static class Idioma
{
    public const string Espanol = "es";
    public const string Ingles = "en";

    /// <summary>Los idiomas disponibles, cada uno con su nombre en su propio idioma.</summary>
    public static IReadOnlyList<(string Clave, string Nombre)> Disponibles { get; } =
    [
        (Espanol, "Español"),
        (Ingles, "English"),
    ];

    private static IReadOnlyDictionary<string, string> _tabla = new Dictionary<string, string>();

    public static string Actual { get; private set; } = Espanol;

    public static bool EsIngles => Actual == Ingles;

    /// <summary>Cultura para números y fechas: «4,5 GB» en español, «4.5 GB» en inglés.</summary>
    public static CultureInfo Cultura { get; private set; } = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>El del sistema si es español; si no, inglés.</summary>
    public static string Predeterminado() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Espanol ? Espanol : Ingles;

    public static void Activar(string? idioma)
    {
        Actual = Disponibles.Any(d => d.Clave == idioma) ? idioma! : Predeterminado();
        Cultura = CultureInfo.GetCultureInfo(Actual == Ingles ? "en-US" : "es-ES");
        _tabla = Actual == Espanol ? new Dictionary<string, string>() : Tabla(Actual);
    }

    /// <summary>Las traducciones de <paramref name="idioma"/>: texto en español → texto traducido.</summary>
    public static IReadOnlyDictionary<string, string> Tabla(string idioma)
    {
        using var flujo = typeof(Idioma).Assembly.GetManifestResourceStream($"Constantino.Core.Idiomas.{idioma}.json");
        return flujo is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(flujo) ?? new Dictionary<string, string>();
    }

    /// <summary>El texto en el idioma de la interfaz.</summary>
    public static string T(string texto) => _tabla.TryGetValue(texto, out var t) ? t : texto;

    /// <summary>Como <see cref="string.Format(IFormatProvider, string, object[])"/>, con el formato traducido.</summary>
    public static string T(string formato, params object?[] valores) => string.Format(Cultura, T(formato), valores);
}
