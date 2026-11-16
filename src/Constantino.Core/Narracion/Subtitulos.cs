using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Constantino.Core.Motores;

namespace Constantino.Core.Narracion;

/// <summary>Una palabra del guion con el momento en que suena.</summary>
public sealed record PalabraConTiempo(string Texto, double Inicio, double Fin);

/// <summary>
/// Subtítulos de una narración: Whisper dice cuándo suena cada palabra y esos tiempos se pasan a las palabras del guion
/// tal como están escritas («1.250 €», no lo que entiende Whisper). Salen en SRT (frases) y en ASS con efecto karaoke
/// para vídeos verticales (Shorts).
/// </summary>
public static partial class Subtitulos
{
    /// <summary>El texto que se ve de un bloque del guion: sin nombre, comentarios ni etiquetas; [[escrito|dicho]] → escrito.</summary>
    public static string TextoVisible(string bloque)
    {
        var lineas = bloque.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith('#'));
        var texto = string.Join(" ", lineas);
        texto = PronunciacionEnLinea().Replace(texto, "$1");
        texto = Etiqueta().Replace(texto, " ");
        return Espacios().Replace(texto, " ").Trim();
    }

    /// <summary>
    /// Da a cada palabra del texto el tiempo de lo que Whisper oyó. Se alinean por sus formas normalizadas (en español,
    /// las cifras pasadas a palabras) con distancia de edición; las palabras sin pareja se reparten entre sus vecinas.
    /// </summary>
    public static IReadOnlyList<PalabraConTiempo> Alinear(string texto, IReadOnlyList<PalabraOida> oidas, bool espanol)
    {
        var visibles = texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var a = new List<(string Token, int Dueno)>();
        for (var i = 0; i < visibles.Length; i++)
            foreach (var t in Normalizar(visibles[i], espanol))
                a.Add((t, i));
        var b = new List<(string Token, double Inicio, double Fin)>();
        foreach (var o in oidas)
        {
            var tokens = Normalizar(o.Texto, espanol);
            for (var k = 0; k < tokens.Count; k++)
            {
                var paso = (o.Fin - o.Inicio) / tokens.Count;
                b.Add((tokens[k], o.Inicio + k * paso, o.Inicio + (k + 1) * paso));
            }
        }

        // Distancia de edición con camino de vuelta (0 = diagonal, 1 = arriba, 2 = izquierda).
        var n = a.Count;
        var m = b.Count;
        var camino = new byte[n + 1, m + 1];
        var anterior = new int[m + 1];
        var actual = new int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            anterior[j] = j;
            camino[0, j] = 2;
        }
        for (var i = 1; i <= n; i++)
        {
            actual[0] = i;
            camino[i, 0] = 1;
            for (var j = 1; j <= m; j++)
            {
                var diagonal = anterior[j - 1] + (a[i - 1].Token == b[j - 1].Token ? 0 : 1);
                var arriba = anterior[j] + 1;
                var izquierda = actual[j - 1] + 1;
                (actual[j], camino[i, j]) = diagonal <= arriba && diagonal <= izquierda ? (diagonal, (byte)0)
                    : arriba <= izquierda ? (arriba, (byte)1) : (izquierda, (byte)2);
            }
            (anterior, actual) = (actual, anterior);
        }
        var inicios = new double?[visibles.Length];
        var fines = new double?[visibles.Length];
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            var paso = i == 0 ? 2 : j == 0 ? 1 : camino[i, j];
            if (paso == 0)
            {
                if (a[i - 1].Token == b[j - 1].Token)
                {
                    var d = a[i - 1].Dueno;
                    inicios[d] = Math.Min(inicios[d] ?? double.MaxValue, b[j - 1].Inicio);
                    fines[d] = Math.Max(fines[d] ?? 0, b[j - 1].Fin);
                }
                (i, j) = (i - 1, j - 1);
            }
            else if (paso == 1)
                i--;
            else
                j--;
        }
        return Rellenar(visibles, inicios, fines, oidas);
    }

    /// <summary>Las palabras sin tiempo se reparten, por su longitud, en el hueco entre sus vecinas con tiempo.</summary>
    private static IReadOnlyList<PalabraConTiempo> Rellenar(string[] visibles, double?[] inicios, double?[] fines,
        IReadOnlyList<PalabraOida> oidas)
    {
        var principio = oidas.Count > 0 ? oidas[0].Inicio : 0;
        var final = oidas.Count > 0 ? oidas[^1].Fin : visibles.Length * 0.35;
        var resultado = new PalabraConTiempo[visibles.Length];
        var i = 0;
        while (i < visibles.Length)
        {
            if (inicios[i] is { } ini)
            {
                resultado[i] = new PalabraConTiempo(visibles[i], ini, Math.Max(ini + 0.05, fines[i]!.Value));
                i++;
                continue;
            }
            var j = i;
            while (j < visibles.Length && inicios[j] is null)
                j++;
            var desde = i > 0 ? resultado[i - 1].Fin : principio;
            var hasta = j < visibles.Length ? inicios[j]!.Value : final;
            var letras = Enumerable.Range(i, j - i).Sum(k => Math.Max(1, visibles[k].Length));
            var t = desde;
            for (var k = i; k < j; k++)
            {
                var dur = Math.Max(0, hasta - desde) * Math.Max(1, visibles[k].Length) / letras;
                resultado[k] = new PalabraConTiempo(visibles[k], t, t + dur);
                t += dur;
            }
            i = j;
        }
        return resultado;
    }

    private static List<string> Normalizar(string texto, bool espanol) =>
        Revision.Palabras(espanol ? NormalizadorEspanol.Normalizar(texto) : texto);

    /// <summary>SRT por frases: corta en los puntos, en las pausas de más de 0,7 s o a las <paramref name="maxLetras"/> letras.</summary>
    public static string Srt(IReadOnlyList<PalabraConTiempo> palabras, int maxLetras = 42)
    {
        var sb = new StringBuilder();
        var n = 0;
        var grupos = Agrupar(palabras, maxLetras, int.MaxValue);
        for (var g = 0; g < grupos.Count; g++)
        {
            var grupo = grupos[g];
            // Al menos 0,7 s en pantalla, sin pisar al siguiente.
            var fin = Math.Max(grupo[^1].Fin, grupo[0].Inicio + 0.7);
            if (g + 1 < grupos.Count)
                fin = Math.Max(grupo[^1].Fin, Math.Min(fin, grupos[g + 1][0].Inicio - 0.01));
            sb.Append(++n).Append('\n')
              .Append(Tiempo(grupo[0].Inicio, ',')).Append(" --> ").Append(Tiempo(fin, ',')).Append('\n')
              .Append(string.Join(" ", grupo.Select(p => p.Texto))).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// ASS con efecto karaoke para vídeo vertical (1080×1920): pocas palabras a la vez, grandes y en el centro; cada una
    /// se ilumina cuando suena.
    /// </summary>
    public static string AssKaraoke(IReadOnlyList<PalabraConTiempo> palabras, int maxPalabras = 4)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: 1080\nPlayResY: 1920\nWrapStyle: 0\n\n");
        sb.Append("[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, ")
          .Append("Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        // Primario: el color de la palabra ya dicha (amarillo); secundario: el de la que aún no ha sonado (blanco).
        sb.Append("Style: Karaoke,Arial Black,92,&H0000E6FF,&H00FFFFFF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,7,2,5,80,80,0,1\n\n");
        sb.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var grupo in Agrupar(palabras, 28, maxPalabras))
        {
            var texto = new StringBuilder();
            for (var k = 0; k < grupo.Count; k++)
            {
                var hasta = k + 1 < grupo.Count ? grupo[k + 1].Inicio : grupo[k].Fin;
                var cs = (int)Math.Max(1, Math.Round((hasta - grupo[k].Inicio) * 100));
                texto.Append("{\\k").Append(cs).Append('}').Append(grupo[k].Texto.Replace("{", "(").Replace("}", ")"));
                if (k + 1 < grupo.Count)
                    texto.Append(' ');
            }
            sb.Append("Dialogue: 0,").Append(Tiempo(grupo[0].Inicio, '.', centesimas: true)).Append(',')
              .Append(Tiempo(Math.Max(grupo[^1].Fin, grupo[0].Inicio + 0.4), '.', centesimas: true))
              .Append(",Karaoke,,0,0,0,,").Append(texto).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Abreviaturas cuyo punto no cierra la frase.</summary>
    private static readonly HashSet<string> Abreviaturas = new(StringComparer.OrdinalIgnoreCase)
    {
        "sr.", "sra.", "srta.", "sres.", "dr.", "dra.", "d.", "dña.", "ud.", "uds.", "vd.", "av.", "avda.", "p.", "pág.", "núm.", "tel.",
    };

    /// <summary>
    /// Grupos de palabras que se ven juntas. Primero frases (hasta un punto que no sea de abreviatura o una pausa de más
    /// de 0,7 s); las que pasan del límite de letras o de palabras se parten en trozos de tamaño parecido, para que no
    /// quede una palabra suelta al final.
    /// </summary>
    private static List<List<PalabraConTiempo>> Agrupar(IReadOnlyList<PalabraConTiempo> palabras, int maxLetras, int maxPalabras)
    {
        var frases = new List<List<PalabraConTiempo>>();
        var actual = new List<PalabraConTiempo>();
        for (var i = 0; i < palabras.Count; i++)
        {
            var p = palabras[i];
            actual.Add(p);
            var cierra = p.Texto.Length > 0 && ".?!…".Contains(p.Texto[^1]) && !Abreviaturas.Contains(p.Texto);
            var pausa = i + 1 < palabras.Count && palabras[i + 1].Inicio - p.Fin > 0.7;
            if (cierra || pausa)
            {
                frases.Add(actual);
                actual = [];
            }
        }
        if (actual.Count > 0)
            frases.Add(actual);

        var grupos = new List<List<PalabraConTiempo>>();
        foreach (var frase in frases)
        {
            var letras = frase.Sum(p => p.Texto.Length + 1) - 1;
            var trozos = Math.Max((int)Math.Ceiling((double)letras / maxLetras), (int)Math.Ceiling((double)frase.Count / maxPalabras));
            if (trozos <= 1)
            {
                grupos.Add(frase);
                continue;
            }
            // Cortes donde el acumulado de letras cruza cada fracción del total.
            var objetivo = (double)letras / trozos;
            var grupo = new List<PalabraConTiempo>();
            var acumulado = 0.0;
            var cortes = 1;
            foreach (var p in frase)
            {
                if (grupo.Count > 0 && cortes < trozos && acumulado + (p.Texto.Length + 1) / 2.0 > objetivo * cortes)
                {
                    grupos.Add(grupo);
                    grupo = [];
                    cortes++;
                }
                grupo.Add(p);
                acumulado += p.Texto.Length + 1;
            }
            grupos.Add(grupo);
        }
        return grupos;
    }

    private static string Tiempo(double s, char separador, bool centesimas = false)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return centesimas
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}{separador}{t.Milliseconds / 10:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separador}{t.Milliseconds:000}");
    }

    [GeneratedRegex(@"\[\[([^\[\]|]+)\|([^\[\]]+)\]\]")]
    private static partial Regex PronunciacionEnLinea();

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex Etiqueta();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacios();
}
