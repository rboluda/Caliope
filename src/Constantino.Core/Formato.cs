using System.Globalization;

namespace Constantino.Core;

/// <summary>Formato de cifras para mostrar al usuario, siempre en español.</summary>
public static class Formato
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>«8 GB», «4,3 GB», «0,3 GB»: un decimal solo por debajo de 10.</summary>
    public static string Gb(double valor) => $"{valor.ToString(valor < 10 ? "0.#" : "0", Es)} GB";
}
