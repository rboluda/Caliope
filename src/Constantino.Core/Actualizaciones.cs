using System.Text.Json;

namespace Constantino.Core;

/// <summary>Una versión de Calíope publicada en GitHub.</summary>
/// <param name="Instalador">Descarga directa del instalador, o null si la versión no lo trae.</param>
public sealed record VersionPublicada(Version Version, string Etiqueta, string Pagina, string? Instalador, string Notas);

/// <summary>Comprobar si hay una versión de Calíope más nueva que la que está en marcha.</summary>
public static class Actualizaciones
{
    public const string Repositorio = "rboluda/Caliope";
    public static string UrlUltimaVersion => $"https://api.github.com/repos/{Repositorio}/releases/latest";

    /// <summary>Lee la respuesta de la API de GitHub para la última versión publicada.</summary>
    public static VersionPublicada? Leer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        if (raiz.TryGetProperty("draft", out var borrador) && borrador.GetBoolean())
            return null;
        if (raiz.TryGetProperty("prerelease", out var previa) && previa.GetBoolean())
            return null;
        var etiqueta = raiz.GetProperty("tag_name").GetString() ?? "";
        if (VersionDeEtiqueta(etiqueta) is not { } version)
            return null;
        string? instalador = null;
        if (raiz.TryGetProperty("assets", out var archivos))
        {
            instalador = archivos.EnumerateArray()
                .Where(a => a.GetProperty("name").GetString()?.EndsWith("-Instalador.exe", StringComparison.OrdinalIgnoreCase) == true)
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault();
        }
        return new VersionPublicada(version, etiqueta,
            raiz.TryGetProperty("html_url", out var pagina) ? pagina.GetString() ?? "" : "",
            instalador,
            raiz.TryGetProperty("body", out var notas) ? notas.GetString() ?? "" : "");
    }

    /// <summary>«v1.2.0» o «1.2» → 1.2.0. Null si no es una versión.</summary>
    public static Version? VersionDeEtiqueta(string etiqueta)
    {
        var texto = etiqueta.Trim().TrimStart('v', 'V');
        var guion = texto.IndexOfAny(['-', '+']);
        if (guion >= 0)
            texto = texto[..guion];
        return Version.TryParse(texto, out var v) ? Normalizar(v) : null;
    }

    public static bool EsMasNueva(VersionPublicada publicada, Version actual) => publicada.Version > Normalizar(actual);

    /// <summary>1.2 y 1.2.0.0 se comparan como 1.2.0.</summary>
    private static Version Normalizar(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
