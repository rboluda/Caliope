using Constantino.Core.Audio;

namespace Constantino.Core.Tests;

public sealed class TramosAudioTests
{
    [Fact]
    public void Ordena_funde_los_que_se_pisan_y_descarta_los_clics()
    {
        var t = TramosAudio.Normalizar([new(20, 25), new(2, 6), new(5, 8), new(10, 10.1)]);

        Assert.Equal([new Tramo(2, 8), new Tramo(20, 25)], t);
        Assert.Equal(6 + 5 + TramosAudio.Pausa, TramosAudio.Duracion(t), 6);
    }

    [Fact]
    public void Une_los_tramos_en_orden_con_una_pausa_de_silencio()
    {
        const int sr = 1000;
        var muestras = Enumerable.Range(0, 10 * sr).Select(i => (float)(i / sr + 1)).ToArray(); // cada segundo, su número
        var audio = new Pcm(muestras, sr);

        var unido = TramosAudio.Unir(audio, [new(1, 2), new(5, 6)]);

        Assert.Equal(2 + TramosAudio.Pausa, unido.Segundos, 3);
        Assert.Equal(2f, unido.Muestras[0]);
        Assert.Equal(0f, unido.Muestras[sr + 100]);
        Assert.Equal(6f, unido.Muestras[^1]);
    }

    [Fact]
    public void Un_solo_tramo_es_el_recorte_de_siempre()
    {
        var audio = new Pcm(new float[5000], 1000);
        Assert.Equal(2, TramosAudio.Unir(audio, [new(1, 3)]).Segundos, 3);
    }
}
