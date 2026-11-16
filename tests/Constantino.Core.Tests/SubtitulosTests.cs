using Constantino.Core.Audio;
using Constantino.Core.Motores;
using Constantino.Core.Narracion;

namespace Constantino.Core.Tests;

public class SubtitulosTests
{
    [Fact]
    public void El_texto_visible_quita_etiquetas_y_comentarios()
    {
        Assert.Equal("Vive en Xàtiva. Fin.",
            Subtitulos.TextoVisible("# inicio\n// nota\nVive en [[Xàtiva|Shátiva]]. [pause 2s] [whispers] Fin."));
    }

    [Fact]
    public void Las_palabras_escritas_toman_el_tiempo_de_lo_oido_aunque_haya_cifras()
    {
        // Whisper escribe las cifras a su manera; el guion, a la suya: se emparejan por cómo suenan.
        var oidas = new List<PalabraOida>
        {
            new("Pagó", 0.0, 0.4), new("mil", 0.5, 0.7), new("doscientos", 0.7, 1.2), new("cincuenta", 1.2, 1.6),
            new("euros.", 1.6, 2.0), new("Y", 2.5, 2.6), new("se", 2.6, 2.7), new("fue.", 2.7, 3.0),
        };
        var p = Subtitulos.Alinear("Pagó 1.250 €. Y se marchó.", oidas, espanol: true);
        Assert.Equal(["Pagó", "1.250", "€.", "Y", "se", "marchó."], p.Select(x => x.Texto));
        Assert.Equal(0.5, p[1].Inicio, 2);
        Assert.Equal(1.6, p[1].Fin, 2);
        // «marchó» no está en lo oído: ocupa el hueco que deja su vecina.
        Assert.True(p[5].Inicio >= p[4].Fin - 0.001);
        Assert.True(p.Zip(p.Skip(1)).All(par => par.Second.Inicio >= par.First.Inicio));
    }

    [Fact]
    public void Srt_por_frases_y_karaoke_por_palabras()
    {
        var palabras = new List<PalabraConTiempo>
        {
            new("Era", 0, 0.3), new("de", 0.3, 0.4), new("noche.", 0.4, 0.9), new("Nadie", 2.0, 2.4), new("hablaba.", 2.4, 3.0),
        };
        var srt = Subtitulos.Srt(palabras);
        Assert.Contains("1\n00:00:00,000 --> 00:00:00,900\nEra de noche.\n", srt);
        Assert.Contains("2\n00:00:02,000 --> 00:00:03,000\nNadie hablaba.\n", srt);
        var ass = Subtitulos.AssKaraoke(palabras);
        Assert.Contains("PlayResY: 1920", ass);
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:00.90,Karaoke,,0,0,0,,{\\k30}Era {\\k10}de {\\k50}noche.", ass);
    }

    [Fact]
    public void El_ruido_de_sala_sale_de_lo_mas_tranquilo_de_la_toma()
    {
        const int sr = 24000;
        var aleatorio = new Random(1);
        // Un segundo de «voz» fuerte y medio de fondo suave.
        var muestras = Enumerable.Range(0, sr).Select(i => (float)(0.3 * Math.Sin(i * 0.05)))
            .Concat(Enumerable.Range(0, sr / 2).Select(_ => (float)((aleatorio.NextDouble() - 0.5) * 0.002))).ToArray();
        var fondo = RuidoDeSala.Modelar([new Pcm(muestras, sr)], sr);
        Assert.NotNull(fondo);
        Assert.True(fondo!.Muestras.Max(Math.Abs) < 0.01);
        var pausa = RuidoDeSala.Rellenar(fondo, 1.5, 7);
        Assert.Equal(sr * 1.5, pausa.Muestras.Length);
        Assert.Equal(0, pausa.Muestras[0]);
        Assert.Contains(pausa.Muestras.Skip(sr / 2).Take(100), m => m != 0);

        // Solo voz, sin fondo tranquilo: no hay ruido de sala.
        Assert.Null(RuidoDeSala.Modelar([new Pcm([.. muestras.Take(sr)], sr)], sr));
    }
}
