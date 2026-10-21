using static Constantino.Core.Idioma;

namespace Constantino.Core.Voces;

/// <summary>
/// La misma voz en otro tono (susurrando, tensa, alegre…), con su propia grabación de referencia.
/// En el guion se usa con la etiqueta [<see cref="Id"/>]; la variante «susurro» también con [whispers].
/// </summary>
/// <param name="Transcripcion">Vacía si se imita solo el timbre.</param>
/// <param name="Descripcion">La instrucción de VoiceDesign, si se generó así.</param>
public sealed record Variante(
    string Id,
    string Nombre,
    string Transcripcion,
    double DuracionSegundos,
    DateTimeOffset Creada,
    string? Descripcion = null)
{
    /// <summary>La etiqueta para el guion; con la interfaz en inglés, su nombre en inglés si lo tiene.</summary>
    public string Etiqueta => Id == CatalogoVariantes.Susurro
        ? "[whispers]"
        : $"[{(EsIngles ? CatalogoVariantes.EnIngles(Id) : Id)}]";
}

/// <summary>Tipos de variante habituales para narrar, con cómo pedírselos a VoiceDesign.</summary>
public sealed record TipoVariante(string Id, string Nombre, string Ingles);

public static class CatalogoVariantes
{
    public const string Susurro = "susurro";

    public static IReadOnlyList<TipoVariante> Tipos { get; } =
    [
        new(Susurro, T("Susurrando"), "Whispering in a hushed, breathy voice, very quietly, as if sharing a secret."),
        new("tenso", T("Tenso"), "Tense and anxious, speaking with suppressed fear, as if something terrible is about to happen."),
        new("asustado", T("Asustado"), "Frightened and trembling, breathing fast, terrified."),
        new("triste", T("Triste"), "Sad and melancholic, voice slightly breaking."),
        new("alegre", T("Alegre"), "Cheerful and warm, smiling while speaking."),
        new("grito", T("Gritando"), "Shouting loudly and urgently, alarmed."),
    ];

    /// <summary>Nombres en inglés de las etiquetas de variante: [scared] equivale a [asustado].</summary>
    private static readonly Dictionary<string, string> AliasIngles = new(StringComparer.Ordinal)
    {
        ["tense"] = "tenso",
        ["scared"] = "asustado",
        ["frightened"] = "asustado",
        ["sad"] = "triste",
        ["happy"] = "alegre",
        ["cheerful"] = "alegre",
        ["shout"] = "grito",
        ["shouting"] = "grito",
    };

    /// <summary>El id de la variante para una etiqueta en inglés (o la misma etiqueta si no es un alias).</summary>
    public static string DesdeIngles(string etiqueta) => AliasIngles.GetValueOrDefault(etiqueta, etiqueta);

    /// <summary>La primera etiqueta en inglés de una variante, o su id si no tiene.</summary>
    public static string EnIngles(string id) => AliasIngles.FirstOrDefault(a => a.Value == id).Key ?? id;

    /// <summary>Instrucción para generar la variante de una voz diseñada: su descripción más el estilo nuevo.</summary>
    public static string InstruccionPara(string descripcionVoz, TipoVariante tipo) =>
        $"{descripcionVoz.Trim().TrimEnd('.')}. {tipo.Ingles}";
}
