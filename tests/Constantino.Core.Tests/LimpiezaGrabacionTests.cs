using Constantino.Core.Audio;

namespace Constantino.Core.Tests;

public sealed class LimpiezaGrabacionTests
{
    [Fact]
    public void Igualar_deja_el_pico_a_menos_un_dB_sin_cambiar_la_forma()
    {
        var audio = new Pcm([0.1f, -0.4f, 0.2f], 24000);

        var igualado = LimpiezaGrabacion.Igualar(audio);

        Assert.Equal(Math.Pow(10, -1 / 20.0), igualado.Muestras.Max(Math.Abs), 4);
        Assert.Equal(-0.25, igualado.Muestras[0] / igualado.Muestras[1], 4);
    }

    [Fact]
    public void Igualar_no_toca_el_silencio()
    {
        var silencio = Pcm.Silencio(1, 24000);
        Assert.Same(silencio, LimpiezaGrabacion.Igualar(silencio));
    }

    [Theory]
    [InlineData(null, -50)]
    [InlineData(-35.4, -35)]
    [InlineData(-120.0, -80)]
    [InlineData(-5.0, -20)]
    public void El_nivel_de_ruido_queda_en_el_rango_que_admite_ffmpeg(double? medido, double esperado)
    {
        var args = LimpiezaGrabacion.QuitarRuido("a.wav", "b.wav", medido);
        Assert.Contains($"afftdn=nr=10:nf={esperado}:tn=1", args);
    }

    [Fact]
    public void Reparar_baja_antes_el_volumen_para_que_quepan_los_picos()
    {
        var args = LimpiezaGrabacion.Reparar("a.wav", "b.wav");
        Assert.Contains("volume=-6dB,adeclip", args);
        Assert.Equal("b.wav", args[^1]);
    }
}
