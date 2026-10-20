using System.Text.RegularExpressions;

namespace Constantino.Core.Narracion;

/// <summary>Un fragmento del bloque: texto a leer o un silencio.</summary>
public abstract record Evento;

/// <param name="Susurro">Se lee con la variante de susurro (o más bajo si no la hay).</param>
public sealed record EventoVoz(string Texto, bool Susurro = false) : Evento;

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
/// el final de la línea o [/whispers]. Cualquier otra etiqueta se quita y se avisa.</item>
/// </list>
/// </summary>
public static partial class Guion
{
    private static readonly HashSet<string> EtiquetasSusurro = ["whispers", "whisper", "whispering", "susurro", "susurra", "susurrando"];
    private static readonly HashSet<string> EtiquetasFinSusurro = ["/whispers", "/whisper", "/susurro", "normal"];

    public static IReadOnlyList<Bloque> Leer(string texto, double pausa = 0.6, double pausaLarga = 1.5)
    {
        var lineas = texto.TrimStart('﻿').Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        var trozos = SeparadorBloques().Split(string.Join("\n", lineas).Trim());
        var bloques = new List<Bloque>();
        foreach (var trozo in trozos.Where(t => t.Trim().Length > 0))
            bloques.Add(LeerBloque(bloques.Count + 1, trozo.Trim().Split('\n'), pausa, pausaLarga));
        return bloques;
    }

    private static Bloque LeerBloque(int indice, string[] lineas, double pausa, double pausaLarga)
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
            var susurro = false; // un salto de línea cierra el susurro
            var pos = 0;
            foreach (Match m in Etiqueta().Matches(linea))
            {
                AnadirVoz(eventos, linea[pos..m.Index], susurro);
                pos = m.Index + m.Length;
                var etiqueta = NormalizarEtiqueta(m.Groups[1].Value);
                if (etiqueta == "pause")
                {
                    AnadirSilencio(eventos, pausa);
                    susurro = false;
                }
                else if (etiqueta == "long pause")
                {
                    AnadirSilencio(eventos, pausaLarga);
                    susurro = false;
                }
                else if (EtiquetasSusurro.Contains(etiqueta))
                {
                    susurro = true;
                }
                else if (EtiquetasFinSusurro.Contains(etiqueta))
                {
                    susurro = false;
                }
                else
                {
                    avisos.Add($"Etiqueta desconocida eliminada: [{m.Groups[1].Value}]");
                }
            }
            AnadirVoz(eventos, linea[pos..], susurro);
        }
        return new Bloque(indice, nombre, string.Join("\n", lineas).Trim(), eventos, avisos);
    }

    private static void AnadirVoz(List<Evento> eventos, string texto, bool susurro)
    {
        texto = Espacios().Replace(texto, " ").Trim();
        if (texto.Length == 0)
            return;
        if (eventos.Count > 0 && eventos[^1] is EventoVoz anterior && anterior.Susurro == susurro)
            eventos[^1] = anterior with { Texto = anterior.Texto + " " + texto };
        else
            eventos.Add(new EventoVoz(texto, susurro));
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
