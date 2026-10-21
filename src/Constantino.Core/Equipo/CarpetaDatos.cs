namespace Constantino.Core.Equipo;

/// <summary>Dónde guarda Constantino herramientas, entornos, modelos y voces.</summary>
public static class CarpetaDatos
{
    /// <summary>%LOCALAPPDATA%\Caliope: no se sincroniza con OneDrive y no necesita permisos de administrador.</summary>
    public static string Predeterminada =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caliope");

    /// <summary>La carpeta por defecto de las versiones anteriores, cuando la aplicación se llamaba Constantino.</summary>
    public static string PredeterminadaAntigua =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Constantino");

    private static readonly string[] VariablesOneDrive = ["OneDrive", "OneDriveCommercial", "OneDriveConsumer"];

    /// <summary>True si la ruta está dentro de una carpeta sincronizada por OneDrive.</summary>
    public static bool EstaEnOneDrive(string ruta) =>
        EstaEnOneDrive(ruta, VariablesOneDrive.Select(Environment.GetEnvironmentVariable));

    internal static bool EstaEnOneDrive(string ruta, IEnumerable<string?> raicesOneDrive)
    {
        var completa = Normalizar(ruta);
        foreach (var raiz in raicesOneDrive)
        {
            if (string.IsNullOrWhiteSpace(raiz))
                continue;
            var r = Normalizar(raiz);
            if (completa.Equals(r, StringComparison.OrdinalIgnoreCase)
                || completa.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Respaldo por si las variables no están definidas: «OneDrive» o «OneDrive - Empresa» como carpeta.
        return completa.Split(Path.DirectorySeparatorChar)
            .Any(p => p.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                      || p.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalizar(string ruta) =>
        Path.GetFullPath(ruta).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
