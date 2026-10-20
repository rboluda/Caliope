namespace Constantino.Core.Motores;

/// <summary>Los motores que pueden leer un texto con una voz de la biblioteca.</summary>
public static class MotoresDeVoz
{
    public const string Qwen = "qwen";
    public const string Chatterbox = "chatterbox";

    /// <summary>Componente del catálogo que instala cada motor.</summary>
    public static string Componente(string motor) => motor == Chatterbox ? "chatterbox" : "motor-qwen";

    public static string Nombre(string motor) => motor == Chatterbox ? "Chatterbox" : "Qwen3-TTS";
}
