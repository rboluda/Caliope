using System.Globalization;

namespace Constantino.Core;

/// <summary>Formato de cifras para mostrar al usuario, con la cultura del idioma de la interfaz.</summary>
public static class Formato
{
    /// <summary>«8 GB», «4,3 GB», «0,3 GB»: un decimal solo por debajo de 10.</summary>
    public static string Gb(double valor) => $"{valor.ToString(valor < 10 ? "0.#" : "0", Idioma.Cultura)} GB";

    /// <summary>Un tamaño en la unidad que se lea mejor: «340 KB», «1,7 MB», «4,3 GB».</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1_000_000 => $"{Math.Max(1, (int)Math.Round(bytes / 1e3))} KB",
        < 1_000_000_000 => $"{(bytes / 1e6).ToString(bytes < 10_000_000 ? "0.#" : "0", Idioma.Cultura)} MB",
        _ => Gb(bytes / 1e9),
    };
}
