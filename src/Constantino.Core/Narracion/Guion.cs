using System.Text.RegularExpressions;
using Constantino.Core.Voces;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Narracion;

/// <summary>Un fragmento del bloque: texto a leer o un silencio.</summary>
public abstract record Evento;

/// <param name="Susurro">Se lee con la variante de susurro (o más bajo si no la hay).</param>
/// <param name="Variante">Variante de la voz con la que se lee (null = la voz normal). Con susurro, «susurro».</param>
/// <param name="VozId">Otra voz de la biblioteca ([voz: …]); null = la voz del proyecto.</param>
public sealed record EventoVoz(string Texto, bool Susurro = false, string? Variante = null, string? VozId = null) : Evento;

public sealed record EventoSilencio(double Segundos) : Evento;

/// <summary>Un efecto de sonido ([sfx: trueno]) que suena en ese punto, con la voz en pausa.</summary>
public sealed record EventoEfecto(string Nombre) : Evento;

/// <summary>Una sustitución del diccionario de pronunciación: cómo se escribe → cómo debe sonar.</summary>
public sealed record Pronunciacion(string Escrito, string Dicho);

/// <summary>Lo que el guion necesita saber del proyecto para entender sus etiquetas.</summary>
/// <param name="Variantes">Ids de las variantes de la voz del proyecto.</param>
/// <param name="Voces">Otras voces utilizables con [voz: …]: nombre o id normalizado (slug) → id.</param>
/// <param name="Efectos">Efectos disponibles para [sfx: …] (nombres normalizados).</param>
/// <param name="Diccionario">Sustituciones de pronunciación (palabras completas, sin distinguir mayúsculas).</param>
public sealed record ContextoGuion(
    IReadOnlyCollection<string>? Variantes = null,
    IReadOnlyDictionary<string, string>? Voces = null,
    IReadOnlyCollection<string>? Efectos = null,
    IReadOnlyList<Pronunciacion>? Diccionario = null);

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
/// funcionan igual. [voz: Nombre] lee con otra voz de la biblioteca hasta el final de la línea; [normal] vuelve a la
/// voz del proyecto. [sfx: nombre] inserta un efecto de sonido. Cualquier otra etiqueta se quita y se avisa.</item>
/// <item>El diccionario de pronunciación sustituye palabras antes de leerlas.</item>
/// </list>
/// </summary>
public static partial class Guion
{
    /// <summary>Nombres en inglés de los efectos que trae Calíope.</summary>
    private static readonly Dictionary<string, string> EfectosEnIngles = new(StringComparer.Ordinal)
    {
        ["thunder"] = "trueno",
        ["heartbeat"] = "latido",
        ["clock"] = "reloj",
        ["bell"] = "campana",
        ["gust"] = "rafaga",
        ["wind-gust"] = "rafaga",
    };

    private static readonly HashSet<string> EtiquetasSusurro = ["whispers", "whisper", "whispering", "susurro", "susurra", "susurrando"];
    private static readonly HashSet<string> EtiquetasFinSusurro = ["/whispers", "/whisper", "/susurro", "normal"];

    /// <param name="variantes">Ids de las variantes de la voz: sus etiquetas se reconocen en el guion.</param>
    public static IReadOnlyList<Bloque> Leer(string texto, double pausa = 0.6, double pausaLarga = 1.5,
        IReadOnlyCollection<string>? variantes = null) =>
        Leer(texto, pausa, pausaLarga, new ContextoGuion(variantes));

    public static IReadOnlyList<Bloque> Leer(string texto, double pausa, double pausaLarga, ContextoGuion contexto)
    {
        var lineas = texto.TrimStart('﻿').Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        var trozos = SeparadorBloques().Split(string.Join("\n", lineas).Trim());
        var bloques = new List<Bloque>();
        foreach (var trozo in trozos.Where(t => t.Trim().Length > 0))
            bloques.Add(LeerBloque(bloques.Count + 1, trozo.Trim().Split('\n'), pausa, pausaLarga, contexto));
        return bloques;
    }

    private static Bloque LeerBloque(int indice, string[] lineas, double pausa, double pausaLarga, ContextoGuion contexto)
    {
        var variantes = contexto.Variantes ?? [];
        var diccionario = contexto.Diccionario ?? [];
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
            // Un salto de línea vuelve a la voz normal (y a la voz del proyecto).
            var susurro = false;
            string? variante = null;
            string? voz = null;
            var pos = 0;
            foreach (Match m in Etiqueta().Matches(linea))
            {
                AnadirVoz(eventos, linea[pos..m.Index], susurro, variante, voz, diccionario);
                pos = m.Index + m.Length;
                var etiqueta = NormalizarEtiqueta(m.Groups[1].Value);
                var comoVariante = CatalogoVariantes.DesdeIngles(Textos.Slug(etiqueta.TrimStart('/'), ""));
                if ((Parametro(etiqueta, "voz") ?? Parametro(etiqueta, "voice")) is { } nombreVoz)
                {
                    var clave = Textos.Slug(nombreVoz, "");
                    if (clave is "normal" or "principal" or "narrador" or "main" or "narrator")
                        voz = null;
                    else if (contexto.Voces is not null && contexto.Voces.TryGetValue(clave, out var id))
                        voz = id;
                    else
                    {
                        voz = null;
                        avisos.Add(T("Voz desconocida en [{0}]: se lee con la voz del proyecto.", m.Groups[1].Value));
                    }
                    (susurro, variante) = (false, null);
                }
                else if ((Parametro(etiqueta, "sfx") ?? Parametro(etiqueta, "efecto")) is { } nombreEfecto)
                {
                    var efecto = Textos.Slug(nombreEfecto, "");
                    // Los efectos incluidos también con su nombre en inglés ([sfx: thunder]), si no hay uno propio así.
                    if (contexto.Efectos?.Contains(efecto) != true && EfectosEnIngles.TryGetValue(efecto, out var espanol))
                        efecto = espanol;
                    if (contexto.Efectos?.Contains(efecto) == true)
                        eventos.Add(new EventoEfecto(efecto));
                    else
                        avisos.Add(T("Efecto desconocido eliminado: [{0}]", m.Groups[1].Value));
                }
                else if (etiqueta == "pause" || etiqueta == "long pause")
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
                    if (etiqueta == "normal")
                        voz = null;
                }
                else if (variantes.Contains(comoVariante))
                {
                    (susurro, variante) = (false, comoVariante);
                }
                else
                {
                    avisos.Add(T("Etiqueta desconocida eliminada: [{0}]", m.Groups[1].Value));
                }
            }
            AnadirVoz(eventos, linea[pos..], susurro, variante, voz, diccionario);
        }
        return new Bloque(indice, nombre, string.Join("\n", lineas).Trim(), eventos, avisos);
    }

    private static void AnadirVoz(List<Evento> eventos, string texto, bool susurro, string? variante, string? voz,
        IReadOnlyList<Pronunciacion> diccionario)
    {
        texto = Espacios().Replace(texto, " ").Trim();
        if (texto.Length == 0)
            return;
        texto = Pronunciar(texto, diccionario);
        if (eventos.Count > 0 && eventos[^1] is EventoVoz anterior && anterior.Susurro == susurro && anterior.Variante == variante
            && anterior.VozId == voz)
            eventos[^1] = anterior with { Texto = anterior.Texto + " " + texto };
        else
            eventos.Add(new EventoVoz(texto, susurro, variante, voz));
    }

    /// <summary>Aplica el diccionario: palabras completas, sin distinguir mayúsculas.</summary>
    public static string Pronunciar(string texto, IReadOnlyList<Pronunciacion> diccionario)
    {
        foreach (var p in diccionario.Where(p => !string.IsNullOrWhiteSpace(p.Escrito)))
            texto = Regex.Replace(texto, $@"(?<![\w]){Regex.Escape(p.Escrito.Trim())}(?![\w])", p.Dicho.Trim(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return texto;
    }

    /// <summary>«voz: anciano» → «anciano» si la etiqueta es de ese tipo; null si no.</summary>
    private static string? Parametro(string etiqueta, string tipo) =>
        etiqueta.StartsWith(tipo + ":", StringComparison.Ordinal) ? etiqueta[(tipo.Length + 1)..].Trim() : null;

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
