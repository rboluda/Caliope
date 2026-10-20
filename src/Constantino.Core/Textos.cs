using System.Globalization;
using System.Text;

namespace Constantino.Core;

public static class Textos
{
    /// <summary>
    /// Convierte un nombre en algo apto para carpetas y archivos: sin tildes, en minúsculas, con guiones.
    /// «Ábuelo Ñoño» → «abuelo-nono».
    /// </summary>
    public static string Slug(string texto, string siVacio = "sin-nombre", int maximo = 40)
    {
        var sb = new StringBuilder();
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : '-');
        }
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? siVacio : slug[..Math.Min(slug.Length, maximo)].TrimEnd('-');
    }

    /// <summary>
    /// Un nombre legible apto para archivos de Windows: conserva tildes y espacios, quita lo prohibido.
    /// «La cazadora: parte 1?» → «La cazadora parte 1».
    /// </summary>
    public static string NombreDeArchivo(string texto, string siVacio = "narracion")
    {
        var prohibidos = Path.GetInvalidFileNameChars();
        var limpio = new string(texto.Select(c => prohibidos.Contains(c) ? ' ' : c).ToArray());
        limpio = string.Join(' ', limpio.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return limpio.Length == 0 ? siVacio : limpio[..Math.Min(limpio.Length, 80)].TrimEnd('.', ' ');
    }
}
