namespace Constantino.Core.Audio;

/// <summary>Un trozo de una grabación, en segundos.</summary>
public sealed record Tramo(double Inicio, double Fin)
{
    public double Duracion => Math.Max(0, Fin - Inicio);
}

/// <summary>
/// Varios tramos de una misma grabación que se usan juntos como muestra de voz (para saltarse la música, una tos o un
/// ruido que quedan en medio): se ordenan, se juntan los que se pisan y se unen con una pausa corta y natural.
/// </summary>
public static class TramosAudio
{
    /// <summary>La pausa entre tramos: como la de una respiración entre frases.</summary>
    public const double Pausa = 0.3;

    /// <summary>Menos que esto no es un tramo (un clic sin arrastrar).</summary>
    public const double Minimo = 0.3;

    /// <summary>Ordenados por inicio, sin los demasiado cortos y con los que se pisan o se tocan fundidos en uno.</summary>
    public static IReadOnlyList<Tramo> Normalizar(IEnumerable<Tramo> tramos)
    {
        var resultado = new List<Tramo>();
        foreach (var t in tramos.Where(t => t.Duracion >= Minimo).OrderBy(t => t.Inicio))
        {
            if (resultado.Count > 0 && t.Inicio <= resultado[^1].Fin)
                resultado[^1] = resultado[^1] with { Fin = Math.Max(resultado[^1].Fin, t.Fin) };
            else
                resultado.Add(t);
        }
        return resultado;
    }

    /// <summary>Lo que dura el audio unido: la voz de los tramos más las pausas entre ellos.</summary>
    public static double Duracion(IReadOnlyList<Tramo> tramos) =>
        tramos.Sum(t => t.Duracion) + Math.Max(0, tramos.Count - 1) * Pausa;

    /// <summary>Los tramos de <paramref name="audio"/>, en orden y separados por <see cref="Pausa"/>.</summary>
    public static Pcm Unir(Pcm audio, IReadOnlyList<Tramo> tramos)
    {
        if (tramos.Count == 1)
            return audio.Subrango(tramos[0].Inicio, tramos[0].Fin);
        var partes = new List<Pcm>();
        foreach (var t in tramos)
        {
            if (partes.Count > 0)
                partes.Add(Pcm.Silencio(Pausa, audio.Frecuencia));
            partes.Add(audio.Subrango(t.Inicio, t.Fin));
        }
        return Pcm.Unir(partes, audio.Frecuencia);
    }
}
