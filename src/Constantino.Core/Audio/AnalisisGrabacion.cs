using System.Globalization;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Audio;

/// <summary>Lo que se ha visto en una grabación, para aconsejar antes de usarla como voz.</summary>
/// <param name="InicioVoz">Primer momento con voz (segundos), con un pequeño margen.</param>
/// <param name="FinVoz">Último momento con voz (segundos), con un pequeño margen.</param>
/// <param name="PicoDb">El punto más alto, en dBFS.</param>
/// <param name="RuidoDb">Nivel del fondo en los tramos más silenciosos, en dB.</param>
public sealed record InformeGrabacion(
    double Duracion,
    double InicioVoz,
    double FinVoz,
    double PicoDb,
    double RuidoDb,
    double PorcentajeSaturado,
    IReadOnlyList<string> Avisos)
{
    public double DuracionVoz => Math.Max(0, FinVoz - InicioVoz);
    public bool HayVoz => DuracionVoz > 0;
}

/// <summary>
/// Revisa una grabación para clonar: dónde está la voz, si satura, si está muy baja, si hay ruido de fondo y si dura
/// lo adecuado. Los umbrales son prudentes: avisan, no impiden.
/// </summary>
public static class AnalisisGrabacion
{
    private const double Ventana = 0.02, Margen = 0.15;
    public const double IdealMinima = 8, IdealMaxima = 25;


    public static InformeGrabacion Analizar(Pcm audio)
    {
        var m = audio.Muestras;
        var n = Math.Max(1, (int)(Ventana * audio.Frecuencia));
        var ventanas = m.Length / n;
        var avisos = new List<string>();
        if (ventanas == 0)
            return new InformeGrabacion(audio.Segundos, 0, 0, -120, -120, 0, [T("La grabación está vacía.")]);

        var niveles = new double[ventanas];
        for (var v = 0; v < ventanas; v++)
        {
            double suma = 0;
            for (var i = v * n; i < (v + 1) * n; i++)
                suma += m[i] * m[i];
            niveles[v] = Db(Math.Sqrt(suma / n));
        }

        var pico = Db(m.Max(Math.Abs));
        var saturadas = m.Count(x => Math.Abs(x) >= 0.99f) * 100.0 / m.Length;
        // El fondo: el nivel de los tramos más silenciosos (percentil 10).
        var ordenados = niveles.OrderBy(x => x).ToArray();
        var ruido = ordenados[(int)(ordenados.Length * 0.1)];
        var maximo = ordenados[^1];
        // Si casi no hay pausas, el percentil 10 también es voz: entonces no se puede medir el fondo.
        var fondoMedible = maximo - ruido >= 15;
        // Hay voz donde el nivel supera claramente al fondo.
        var umbral = Math.Max(Math.Min(ruido + 12, maximo - 25), -50);
        var primera = Array.FindIndex(niveles, x => x > umbral);
        var ultima = Array.FindLastIndex(niveles, x => x > umbral);
        double inicio = 0, fin = 0;
        if (primera >= 0)
        {
            inicio = Math.Max(0, primera * Ventana - Margen);
            fin = Math.Min(audio.Segundos, (ultima + 1) * Ventana + Margen);
        }

        if (primera < 0)
            avisos.Add(T("No se oye voz: comprueba el micrófono o elige otro audio."));
        if (saturadas > 0.1)
            avisos.Add(T("La grabación satura (se distorsiona en los momentos fuertes). Baja el volumen del micrófono o aléjate un poco."));
        else if (pico < -20 && primera >= 0)
            avisos.Add(T("La grabación está muy baja. Acércate al micrófono o sube su volumen."));
        if (fondoMedible && ruido > -45 && primera >= 0)
            avisos.Add(T("Hay ruido de fondo (unos {0:0} dB). Graba en un sitio más silencioso para que la voz salga limpia.", ruido));

        var voz = fin - inicio;
        if (primera >= 0 && voz < 3)
            avisos.Add(T("Hay muy poca voz: hacen falta al menos 3 segundos (lo ideal, 10–20)."));
        else if (primera >= 0 && voz < IdealMinima)
            avisos.Add(T("Hay {0} de voz. Funciona, pero con 10–20 s el parecido es mejor.", Segundos(voz)));
        else if (voz > IdealMaxima)
            avisos.Add(T("Hay {0} de voz: selecciona un tramo de 10–20 s con buena calidad.", Segundos(voz)));

        return new InformeGrabacion(audio.Segundos, inicio, fin, pico, ruido, saturadas, avisos);
    }

    private static double Db(double amplitud) => 20 * Math.Log10(amplitud + 1e-9);

    private static string Segundos(double s) => $"{s.ToString("0.#", Idioma.Cultura)} s";
}
