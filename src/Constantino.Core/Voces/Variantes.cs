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
    public string Etiqueta => Id == CatalogoVariantes.Susurro ? "[whispers]" : $"[{Id}]";
}

/// <summary>Tipos de variante habituales para narrar, con cómo pedírselos a VoiceDesign.</summary>
public sealed record TipoVariante(string Id, string Nombre, string Ingles);

public static class CatalogoVariantes
{
    public const string Susurro = "susurro";

    public static IReadOnlyList<TipoVariante> Tipos { get; } =
    [
        new(Susurro, "Susurrando", "Whispering in a hushed, breathy voice, very quietly, as if sharing a secret."),
        new("tenso", "Tenso", "Tense and anxious, speaking with suppressed fear, as if something terrible is about to happen."),
        new("asustado", "Asustado", "Frightened and trembling, breathing fast, terrified."),
        new("triste", "Triste", "Sad and melancholic, voice slightly breaking."),
        new("alegre", "Alegre", "Cheerful and warm, smiling while speaking."),
        new("grito", "Gritando", "Shouting loudly and urgently, alarmed."),
    ];

    /// <summary>Instrucción para generar la variante de una voz diseñada: su descripción más el estilo nuevo.</summary>
    public static string InstruccionPara(string descripcionVoz, TipoVariante tipo) =>
        $"{descripcionVoz.Trim().TrimEnd('.')}. {tipo.Ingles}";
}
