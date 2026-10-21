using static Constantino.Core.Idioma;

namespace Constantino.Core.Voces;

public enum OrigenVoz
{
    /// <summary>Grabación traída de fuera (o generada en otra herramienta).</summary>
    Importada,
    /// <summary>Creada con VoiceDesign a partir de una descripción.</summary>
    Disenada,
    /// <summary>Grabada o importada en la pantalla de clonar.</summary>
    Clonada,
}

/// <summary>Una voz de la biblioteca: su grabación de referencia (referencia.wav) y lo que se sabe de ella.</summary>
/// <param name="Id">Nombre de la carpeta; no cambia aunque se renombre la voz.</param>
/// <param name="Transcripcion">
/// Lo que se dice exactamente en la grabación. Vacía si no se conoce: entonces solo se imita el timbre
/// (algo menos parecido, pero funciona).
/// </param>
/// <param name="Consentimiento">Quien la añadió confirmó que la voz es suya o tiene permiso para usarla.</param>
public sealed record Voz(
    string Id,
    string Nombre,
    string Idioma,
    OrigenVoz Origen,
    string Transcripcion,
    double DuracionSegundos,
    DateTimeOffset Creada,
    bool Consentimiento,
    string? Descripcion = null,
    int? Semilla = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SoloTimbre => string.IsNullOrEmpty(Transcripcion);
}

/// <summary>Idiomas que admite Qwen3-TTS: la clave es lo que espera el modelo.</summary>
public static class Idiomas
{
    public static IReadOnlyList<(string Clave, string Nombre)> Todos { get; } =
    [
        ("Spanish", T("Español")),
        ("English", T("Inglés")),
        ("French", T("Francés")),
        ("German", T("Alemán")),
        ("Italian", T("Italiano")),
        ("Portuguese", T("Portugués")),
        ("Russian", T("Ruso")),
        ("Chinese", T("Chino")),
        ("Japanese", T("Japonés")),
        ("Korean", T("Coreano")),
    ];

    public static bool Existe(string clave) => Todos.Any(i => i.Clave == clave);

    public static string Nombre(string clave) => Todos.FirstOrDefault(i => i.Clave == clave).Nombre ?? clave;
}
