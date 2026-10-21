using System.Globalization;
using System.Text;

namespace Constantino.Core.Narracion;

/// <summary>Quien escucha un audio y escribe lo que se dice (en la app, Whisper).</summary>
public interface IRevisor
{
    Task<string> TranscribirAsync(string wav, CancellationToken ct);
}

/// <summary>Cuánto se parece lo que se oye a lo que se tenía que decir.</summary>
/// <param name="Errores">Palabras de más, de menos o cambiadas.</param>
/// <param name="Tasa">Errores por palabra del texto (0 = idéntico).</param>
public sealed record ComparacionLectura(int Errores, int Palabras, double Tasa)
{
    /// <summary>
    /// Mal leído: se ha saltado o inventado bastante. Unos pocos errores se toleran (Whisper también se equivoca,
    /// sobre todo con nombres propios).
    /// </summary>
    public bool MalLeido => Errores >= 3 && Tasa > 0.2;
}

/// <summary>Compara el texto de un bloque con la transcripción de su audio para detectar frases cortadas o inventadas.</summary>
public static class Revision
{
    public static ComparacionLectura Comparar(string esperado, string oido)
    {
        var a = Palabras(esperado);
        // Lo que Whisper escriba con cifras tampoco se compara.
        var b = Palabras(oido).Where(p => !p.Any(char.IsDigit)).ToList();
        var errores = Distancia(a, b);
        var palabras = a.Count(p => !EsCifra(p));
        return new ComparacionLectura(errores, palabras, palabras == 0 ? 0 : (double)errores / palabras);
    }

    /// <summary>Minúsculas, sin tildes ni signos de puntuación.</summary>
    internal static List<string> Palabras(string texto)
    {
        var sb = new StringBuilder();
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool EsCifra(string palabra) => palabra.Any(char.IsDigit);

    /// <summary>Cuántas palabras puede ocupar una cifra leída («1990» → «mil novecientos noventa»).</summary>
    private const int PalabrasPorCifra = 6;

    /// <summary>
    /// Distancia de edición por palabras (inserciones, borrados y sustituciones). Una cifra del texto equivale, sin
    /// coste, a entre 0 y <see cref="PalabrasPorCifra"/> palabras de lo oído: no se sabe cómo se ha leído.
    /// </summary>
    private static int Distancia(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var d = new int[a.Count + 1, b.Count + 1];
        for (var j = 0; j <= b.Count; j++)
            d[0, j] = j;
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 0; j <= b.Count; j++)
            {
                if (EsCifra(a[i - 1]))
                {
                    var mejor = int.MaxValue;
                    for (var k = 0; k <= Math.Min(PalabrasPorCifra, j); k++)
                        mejor = Math.Min(mejor, d[i - 1, j - k]);
                    d[i, j] = mejor;
                    continue;
                }
                var valor = d[i - 1, j] + 1;
                if (j > 0)
                    valor = Math.Min(valor, Math.Min(d[i, j - 1] + 1, d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
                d[i, j] = valor;
            }
        }
        return d[a.Count, b.Count];
    }
}
