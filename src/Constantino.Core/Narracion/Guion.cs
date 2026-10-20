using System.Text.RegularExpressions;

namespace Constantino.Core.Narracion;

/// <summary>Un fragmento del bloque: texto a leer o un silencio.</summary>
public abstract record Evento;

/// <param name="Susurro">Se lee con la variante de susurro (o más bajo si no la hay).</param>
/// <param name="Variante">Variante de la voz con la que se lee (null = la voz normal). Con susurro, «susurro».</param>
public sealed record EventoVoz(string Texto, bool Susurro = false, string? Variante = null) : Evento;

public sealed record EventoSilencio(double Segundos) : Evento;

/// <summary>Un bloque del guion: un párrafo, que da un archivo de audio.</summary>
/// <param name="Nombre">De la línea «# nombre»; «bloque» si no la hay.</param>
public sealed record Bloque(int Indice, string Nombre, string Texto, IReadOnlyList<Evento> Eventos, IReadOnlyList<string> Avisos)
{
    public string Archivo => $"{Indice:00}_{Nombre}";

    /// <summary>Lo que se va a leer, sin etiquetas (para mostrarlo).</summary>
    public string TextoLeido => string.Join(" ", Eventos.OfType<EventoVoz>().Select(e => e.Texto));

    public bool Vacio => !Eventos.OfType<EventoVoz>().Any();
}

/// <summary>
/// Lee guiones de narración:
/// <list type="bullet">
/// <item>Los bloques se separan con una o más líneas en blanco.</item>
/// <item>Si la primera línea de un bloque empieza por «#», es su nombre.</item>
/// <item>Las líneas que empiezan por «//» son comentarios.</item>
/// <item>[pause] y [long pause] son silencios; [whispers] afecta al texto siguiente hasta otra etiqueta,
/// el final de la línea o [/whispers]. Las etiquetas con el nombre de una variante de la voz ([tenso], [alegre]…)
/// funcionan igual. Cualquier otra etiqueta se quita y se avisa.</item>
/// </list>
/// </summary>
public static partial class Guion
{
    private static readonly HashSet<string> EtiquetasSusurro = ["whispers", "whisper", "whispering", "susurro", "susurra", "susurrando"];
    private static readonly HashSet<string> EtiquetasFinSusurro = ["/whispers", "/whisper", "/susurro", "normal"];

    /// <param name="variantes">Ids de las variantes de la voz: sus etiquetas se reconocen en el guion.</param>
    public static IReadOnlyList<Bloque> Leer(string texto, double pausa = 0.6, double pausaLarga = 1.5,
        IReadOnlyCollection<string>? variantes = null)
    {
        var lineas = texto.TrimStart('﻿').Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        var trozos = SeparadorBloques().Split(string.Join("\n", lineas).Trim());
        var bloques = new List<Bloque>();
        foreach (var trozo in trozos.Where(t => t.Trim().Length > 0))
            bloques.Add(LeerBloque(bloques.Count + 1, trozo.Trim().Split('\n'), pausa, pausaLarga, variantes ?? []));
        return bloques;
    }

    private static Bloque LeerBloque(int indice, string[] lineas, double pausa, double pausaLarga, IReadOnlyCollection<string> variantes)
    {
        var nombre = "bloque";
        if (lineas.Length > 0 && lineas[0].TrimStart().StartsWith('#'))
        {
            nombre = Textos.Slug(lineas[0].TrimStart().TrimStart('#'), "bloque");
            lineas = lineas[1..];
        }
        var eventos = new List<Evento>();
        var avisos = new List<string>();
        foreach (var linea in lineas)
        {
            // Un salto de línea vuelve a la voz normal.
            var susurro = false;
            string? variante = null;
            var pos = 0;
            foreach (Match m in Etiqueta().Matches(linea))
            {
                AnadirVoz(eventos, linea[pos..m.Index], susurro, variante);
                pos = m.Index + m.Length;
                var etiqueta = NormalizarEtiqueta(m.Groups[1].Value);
                var comoVariante = Textos.Slug(etiqueta.TrimStart('/'), "");
                if (etiqueta == "pause" || etiqueta == "long pause")
                {
                    AnadirSilencio(eventos, etiqueta == "pause" ? pausa : pausaLarga);
                    (susurro, variante) = (false, null);
                }
                else if (EtiquetasSusurro.Contains(etiqueta))
                {
                    (susurro, variante) = (true, "susurro");
                }
                else if (EtiquetasFinSusurro.Contains(etiqueta) || (etiqueta.StartsWith('/') && variantes.Contains(comoVariante)))
                {
                    (susurro, variante) = (false, null);
                }
                else if (variantes.Contains(comoVariante))
                {
                    (susurro, variante) = (false, comoVariante);
                }
                else
                {
                    avisos.Add($"Etiqueta desconocida eliminada: [{m.Groups[1].Value}]");
                }
            }
            AnadirVoz(eventos, linea[pos..], susurro, variante);
        }
        return new Bloque(indice, nombre, string.Join("\n", lineas).Trim(), eventos, avisos);
    }

    private static void AnadirVoz(List<Evento> eventos, string texto, bool susurro, string? variante)
    {
        texto = Espacios().Replace(texto, " ").Trim();
        if (texto.Length == 0)
            return;
        if (eventos.Count > 0 && eventos[^1] is EventoVoz anterior && anterior.Susurro == susurro && anterior.Variante == variante)
            eventos[^1] = anterior with { Texto = anterior.Texto + " " + texto };
        else
            eventos.Add(new EventoVoz(texto, susurro, variante));
    }

    private static void AnadirSilencio(List<Evento> eventos, double segundos)
    {
        if (eventos.Count > 0 && eventos[^1] is EventoSilencio anterior)
            eventos[^1] = new EventoSilencio(anterior.Segundos + segundos);
        else
            eventos.Add(new EventoSilencio(segundos));
    }

    private static string NormalizarEtiqueta(string etiqueta) =>
        GuionesYEspacios().Replace(etiqueta.Trim().ToLowerInvariant(), " ").Replace("/ ", "/");

    /// <summary>Agrupa frases completas en trozos de como mucho <paramref name="maxCaracteres"/> (una frase larga no se parte).</summary>
    public static IReadOnlyList<string> PartirEnFrases(string texto, int maxCaracteres)
    {
        if (texto.Length <= maxCaracteres)
            return [texto];
        var trozos = new List<string>();
        var actual = "";
        foreach (var frase in FinDeFrase().Split(texto))
        {
            if (actual.Length > 0 && actual.Length + 1 + frase.Length > maxCaracteres)
            {
                trozos.Add(actual);
                actual = frase;
            }
            else
            {
                actual = (actual + " " + frase).Trim();
            }
        }
        if (actual.Length > 0)
            trozos.Add(actual);
        return trozos;
    }

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex SeparadorBloques();

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex Etiqueta();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacios();

    [GeneratedRegex(@"[\s_-]+")]
    private static partial Regex GuionesYEspacios();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex FinDeFrase();
}
