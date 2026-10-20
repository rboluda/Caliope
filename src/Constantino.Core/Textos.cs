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
}
