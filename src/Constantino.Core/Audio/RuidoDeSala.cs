namespace Constantino.Core.Audio;

/// <summary>
/// «Ruido de sala»: en vez de silencio digital (que suena a corte), las pausas se rellenan con el fondo de la propia toma,
/// sacado de sus tramos más tranquilos. Así las pausas suenan como la voz y no como un hueco.
/// </summary>
public static class RuidoDeSala
{
    private const double Tramo = 0.02, Fundido = 0.01, FundidoPausa = 0.04;

    /// <summary>Por encima de esto no es fondo, es voz: no se usa (−45 dBFS).</summary>
    private const double NivelMaximo = 0.0056;

    /// <summary>
    /// El fondo de las grabaciones: sus tramos de 20 ms más tranquilos (el 15 % de abajo, sin contar el silencio digital),
    /// unidos con fundidos. Null si no hay bastante (menos de 0,2 s) o si lo más tranquilo ya es voz.
    /// </summary>
    public static Pcm? Modelar(IEnumerable<Pcm> grabaciones, int frecuencia)
    {
        var largo = Math.Max(1, (int)(Tramo * frecuencia));
        var tramos = new List<(double Nivel, float[] Muestras)>();
        foreach (var g in grabaciones.Where(g => g.Frecuencia == frecuencia))
        {
            for (var i = 0; i + largo <= g.Muestras.Length; i += largo)
            {
                var trozo = g.Muestras.AsSpan(i, largo).ToArray();
                var nivel = Math.Sqrt(trozo.Average(m => (double)m * m));
                if (nivel > 1e-5)
                    tramos.Add((nivel, trozo));
            }
        }
        if (tramos.Count == 0)
            return null;
        var tranquilos = tramos.OrderBy(t => t.Nivel).Take(Math.Max(1, tramos.Count * 15 / 100)).ToList();
        if (tranquilos.Count * Tramo < 0.2 || tranquilos.Average(t => t.Nivel) > NivelMaximo)
            return null;
        return Unir(tranquilos.Select(t => t.Muestras), frecuencia, (int)(Fundido * frecuencia));
    }

    /// <summary>Una pausa de <paramref name="segundos"/> hecha con el fondo, empezando y acabando con un fundido.</summary>
    public static Pcm Rellenar(Pcm fondo, double segundos, int semilla)
    {
        var sr = fondo.Frecuencia;
        var total = (int)Math.Round(segundos * sr);
        var cruce = (int)(Fundido * sr);
        var aleatorio = new Random(semilla);
        var trozos = new List<float[]>();
        var acumulado = 0;
        while (acumulado < total + cruce)
        {
            // Trozos de hasta 0,3 s desde sitios al azar, para que no se note la repetición.
            var largo = Math.Min(fondo.Muestras.Length, Math.Max(cruce * 3, (int)(0.3 * sr)));
            var inicio = aleatorio.Next(0, Math.Max(1, fondo.Muestras.Length - largo));
            trozos.Add(fondo.Muestras.AsSpan(inicio, largo).ToArray());
            acumulado += largo - cruce;
        }
        var muestras = Unir(trozos, sr, cruce).Muestras.AsSpan(0, total).ToArray();
        var entrada = Math.Min(total / 2, (int)(FundidoPausa * sr));
        for (var i = 0; i < entrada; i++)
        {
            var g = (float)i / entrada;
            muestras[i] *= g;
            muestras[total - 1 - i] *= g;
        }
        return new Pcm(muestras, sr);
    }

    /// <summary>Une trozos solapando <paramref name="cruce"/> muestras con un fundido cruzado.</summary>
    private static Pcm Unir(IEnumerable<float[]> trozos, int frecuencia, int cruce)
    {
        var salida = new List<float>();
        foreach (var t in trozos)
        {
            var solape = Math.Min(cruce, Math.Min(salida.Count, t.Length));
            for (var i = 0; i < solape; i++)
            {
                var g = (float)(i + 1) / (solape + 1);
                var k = salida.Count - solape + i;
                salida[k] = salida[k] * (1 - g) + t[i] * g;
            }
            for (var i = solape; i < t.Length; i++)
                salida.Add(t[i]);
        }
        return new Pcm([.. salida], frecuencia);
    }
}
