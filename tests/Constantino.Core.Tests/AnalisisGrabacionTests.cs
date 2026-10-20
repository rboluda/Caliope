using Constantino.Core.Audio;

namespace Constantino.Core.Tests;

public class AnalisisGrabacionTests
{
    private const int Sr = 24000;

    /// <summary>Silencio (con un fondo opcional) + «voz» (un tono) + silencio.</summary>
    private static Pcm Grabacion(double antes, double voz, double despues, float amplitud = 0.3f, float fondo = 0f)
    {
        var total = (int)((antes + voz + despues) * Sr);
        var m = new float[total];
        var aleatorio = new Random(1);
        for (var i = 0; i < total; i++)
        {
            var t = (double)i / Sr;
            m[i] = fondo * (float)(aleatorio.NextDouble() * 2 - 1);
            if (t >= antes && t < antes + voz)
                m[i] += amplitud * MathF.Sin(2 * MathF.PI * 180 * i / Sr);
        }
        return new Pcm(m, Sr);
    }

    [Fact]
    public void Encuentra_la_voz_y_no_avisa_si_todo_esta_bien()
    {
        var informe = AnalisisGrabacion.Analizar(Grabacion(2, 14, 3));

        Assert.InRange(informe.InicioVoz, 1.8, 2.0);
        Assert.InRange(informe.FinVoz, 16.0, 16.2);
        Assert.Empty(informe.Avisos);
    }

    [Fact]
    public void Avisa_de_saturacion()
    {
        var informe = AnalisisGrabacion.Analizar(Grabacion(1, 12, 1, amplitud: 1.5f));

        Assert.Contains(informe.Avisos, a => a.Contains("satura"));
    }

    [Fact]
    public void Avisa_si_esta_muy_baja()
    {
        var informe = AnalisisGrabacion.Analizar(Grabacion(1, 12, 1, amplitud: 0.03f));

        Assert.Contains(informe.Avisos, a => a.Contains("muy baja"));
    }

    [Fact]
    public void Avisa_del_ruido_de_fondo()
    {
        var informe = AnalisisGrabacion.Analizar(Grabacion(2, 12, 2, fondo: 0.02f));

        Assert.Contains(informe.Avisos, a => a.Contains("ruido de fondo"));
    }

    [Fact]
    public void Una_voz_continua_sin_pausas_no_se_toma_por_ruido()
    {
        var informe = AnalisisGrabacion.Analizar(Grabacion(0, 15, 0));

        Assert.DoesNotContain(informe.Avisos, a => a.Contains("ruido"));
        Assert.InRange(informe.DuracionVoz, 14.5, 15.1);
    }

    [Theory]
    [InlineData(2, "muy poca voz")]
    [InlineData(5, "Funciona, pero")]
    [InlineData(40, "selecciona un tramo")]
    public void Avisa_de_duraciones_poco_adecuadas(double segundos, string aviso)
    {
        var informe = AnalisisGrabacion.Analizar(Grabacion(1, segundos, 1));

        Assert.Contains(informe.Avisos, a => a.Contains(aviso));
    }

    [Fact]
    public void Sin_voz_lo_dice()
    {
        var informe = AnalisisGrabacion.Analizar(new Pcm(new float[Sr * 3], Sr));

        Assert.False(informe.HayVoz);
        Assert.Contains(informe.Avisos, a => a.Contains("No se oye voz"));
    }

    [Fact]
    public void Subrango_recorta_por_segundos_y_respeta_los_limites()
    {
        var p = new Pcm(Enumerable.Range(0, 10).Select(i => (float)i).ToArray(), 10);

        Assert.Equal([2f, 3f, 4f], p.Subrango(0.2, 0.5).Muestras);
        Assert.Equal([8f, 9f], p.Subrango(0.8, 5).Muestras);
        Assert.Empty(p.Subrango(0.5, 0.2).Muestras);
    }
}
