using System.Text.RegularExpressions;

namespace Constantino.Core.Instalaciones;

/// <summary>Traduce la salida técnica de un proceso que falló a una explicación para el usuario.</summary>
public static partial class DiagnosticoErrores
{
    private static readonly (string[] Pistas, string Explicacion)[] Causas =
    [
        (["No space left", "Errno 28", "not enough space", "espacio suficiente en el disco"],
            "no queda espacio en el disco. Libera espacio o elige otra carpeta."),
        (["Permission denied", "Errno 13", "Access is denied", "Acceso denegado", "being used by another process", "está siendo utilizado por otro proceso"],
            "Windows no ha dejado escribir algún archivo (permiso denegado o archivo en uso). Cierra los programas que puedan estar usándolo y vuelve a intentarlo."),
        (["MemoryError", "out of memory", "Unable to allocate"],
            "se ha agotado la memoria del equipo. Cierra otros programas y vuelve a intentarlo."),
        (["Connection broken", "ConnectionError", "Connection reset", "Connection aborted", "Read timed out", "timed out",
          "Max retries exceeded", "NameResolutionError", "getaddrinfo failed", "Failed to download", "SSLError", "IncompleteRead",
          "dns error", "error sending request"],
            "se ha cortado la conexión o no hay acceso a internet. Comprueba la conexión y pulsa «Reintentar»: lo ya descargado se aprovecha."),
        (["dispositivo=cpu"],
            "el motor no detecta tu tarjeta NVIDIA. Actualiza su driver desde nvidia.com y pulsa «Reparar»."),
    ];

    /// <summary>La explicación de la causa más probable, o la última línea de error si no se reconoce.</summary>
    public static string Explicar(string salida)
    {
        foreach (var (pistas, explicacion) in Causas)
        {
            if (pistas.Any(p => salida.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return explicacion;
        }
        var ultima = salida.Split('\n').Select(l => l.Trim()).LastOrDefault(l => LineaDeError().IsMatch(l));
        return ultima is null
            ? "ha fallado sin dar más detalles. Abre el registro para ver qué ha pasado."
            : $"ha fallado ({Recortar(ultima, 200)}).";
    }

    private static string Recortar(string texto, int maximo) => texto.Length <= maximo ? texto : texto[..maximo] + "…";

    [GeneratedRegex(@"^([\w.]+(Error|Exception)\b|error:|ERROR:)", RegexOptions.IgnoreCase)]
    private static partial Regex LineaDeError();
}
