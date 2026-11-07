using static Constantino.Core.Idioma;

namespace Constantino.Core.Catalogo;

/// <param name="Necesita">Componentes imprescindibles para esta función, además de la base.</param>
/// <param name="Opcionales">Componentes que la mejoran pero no hacen falta.</param>
public sealed record Funcion(string Id, string Nombre, string Descripcion, IReadOnlyList<string> Necesita, IReadOnlyList<string> Opcionales);

/// <summary>
/// Lo que se puede hacer con Calíope y qué necesita cada cosa: así se ordena «Componentes» (la base, una caja por
/// función y los motores que sustituyen al principal), en vez de por tipo de componente.
/// </summary>
public static class FuncionesCaliope
{
    /// <summary>Sin esto no funciona nada (además de una de las voces del grupo <see cref="GrupoVoces"/>).</summary>
    public static IReadOnlyList<string> Base { get; } = ["nucleo", "motor-qwen"];

    /// <summary>Las voces del motor principal: alternativas, basta con una.</summary>
    public const string GrupoVoces = "modelo-base";

    /// <summary>Motores que sustituyen al principal cuando se eligen en Narrar.</summary>
    public static IReadOnlyList<string> MotoresAlternativos { get; } = ["chatterbox", "voxcpm2"];

    public static IReadOnlyList<Funcion> Todas =>
    [
        new("narrar", T("Narrar y clonar voces"),
            T("Funciona con la base. La transcripción automática escribe sola lo que dice la grabación al clonar."), [], ["whisper"]),
        new("disenar", T("Diseñar voces"), T("Inventa voces nuevas a partir de una descripción."), ["modelo-qwen-voicedesign"], []),
        new("doblar", T("Doblar vídeos"), T("Del inglés al castellano, con la voz de cada persona del vídeo."),
            ["whisper", "separador-voz", "traductor", "chatterbox"], []),
        new("actas", T("Actas de reuniones"), T("De la grabación de una reunión a su acta, con quién dijo qué."), ["whisper", "traductor"], []),
    ];
}
