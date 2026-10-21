using static Constantino.Core.Idioma;

namespace Constantino.Core.Motores;

/// <summary>Los motores que pueden leer un texto con una voz de la biblioteca.</summary>
public static class MotoresDeVoz
{
    public const string Qwen = "qwen";
    public const string Chatterbox = "chatterbox";
    public const string VoxCpm = "voxcpm";

    /// <summary>Todos, del principal a los opcionales.</summary>
    public static readonly IReadOnlyList<string> Todos = [Qwen, Chatterbox, VoxCpm];

    /// <summary>Componente del catálogo que instala cada motor.</summary>
    public static string Componente(string motor) => motor switch
    {
        Chatterbox => "chatterbox",
        VoxCpm => "voxcpm2",
        _ => "motor-qwen",
    };

    /// <summary>Carpeta de su entorno de Python dentro de «entornos».</summary>
    public static string Entorno(string motor) => motor switch
    {
        Chatterbox => "chatterbox",
        VoxCpm => "voxcpm2",
        _ => "qwen3tts",
    };

    /// <summary>Nombre del programa de terceros que hay detrás.</summary>
    public static string Nombre(string motor) => motor switch
    {
        Chatterbox => "Chatterbox",
        VoxCpm => "VoxCPM2",
        _ => "Qwen3-TTS",
    };

    /// <summary>Nombre para elegirlo en las listas: qué es para el usuario y, entre paréntesis, de quién es.</summary>
    public static string NombreVisible(string motor) => motor switch
    {
        Chatterbox => T("Castellano rápido (Chatterbox)"),
        VoxCpm => T("Máximo parecido (VoxCPM2)"),
        _ => T("Principal (Qwen3-TTS)"),
    };

    /// <summary>El motor cuyo componente o modelo es <paramref name="componente"/>, o null si no es de ninguno.</summary>
    public static string? DeComponente(string componente) =>
        Todos.Skip(1).FirstOrDefault(m => Componente(m) == componente);

    /// <summary>Solo Chatterbox deja ajustar la intensidad emocional.</summary>
    public static bool TieneIntensidad(string motor) => motor == Chatterbox;

    /// <summary>VoxCPM2 no tiene temperatura: la «expresividad» no le afecta.</summary>
    public static bool TieneExpresividad(string motor) => motor != VoxCpm;
}
