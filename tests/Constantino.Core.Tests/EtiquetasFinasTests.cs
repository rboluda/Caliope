using Constantino.Core.Narracion;

namespace Constantino.Core.Tests;

public class EtiquetasFinasTests
{
    private static Bloque Leer(string guion, params string[] variantes) =>
        Guion.Leer(guion, 0.6, 1.5, new ContextoGuion(variantes)).Single();

    [Theory]
    [InlineData("[pause 2s]", 2)]
    [InlineData("[pausa 0,5]", 0.5)]
    [InlineData("[pausa: 1.5 segundos]", 1.5)]
    [InlineData("[pause 60s]", 10)]
    public void Pausa_con_duracion_exacta(string etiqueta, double segundos)
    {
        var b = Leer($"Hola. {etiqueta} Adiós.");
        Assert.Equal(segundos, b.Eventos.OfType<EventoSilencio>().Single().Segundos, 3);
        Assert.Empty(b.Avisos);
    }

    [Fact]
    public void Velocidad_hasta_el_final_de_la_linea_o_normal()
    {
        var b = Leer("Normal. [lento] Despacio. [normal] Otra vez normal.\n[velocidad 1,2] Rápido.\nSiguiente línea.");
        var voces = b.Eventos.OfType<EventoVoz>().ToList();
        Assert.Equal([1.0, 0.85, 1.0, 1.2, 1.0], voces.Select(v => v.Velocidad));
        Assert.Equal("Despacio.", voces[1].Texto);
        Assert.Equal(1.15, Leer("[rápido] Ya.").Eventos.OfType<EventoVoz>().Single().Velocidad);
        Assert.Equal(1.3, Leer("[speed:2] Ya.").Eventos.OfType<EventoVoz>().Single().Velocidad);
    }

    [Fact]
    public void Pronunciacion_dentro_del_texto()
    {
        var b = Leer("Vive en [[Xàtiva|Shátiva]], cerca de [[Ontinyent|Ontiñén]].");
        Assert.Equal("Vive en Shátiva, cerca de Ontiñén.", b.TextoLeido);
        Assert.Empty(b.Avisos);
    }

    [Fact]
    public void Emocion_con_la_variante_de_la_voz()
    {
        var con = Leer("[emoción: miedo] No abras la puerta.", "asustado");
        Assert.Equal("asustado", con.Eventos.OfType<EventoVoz>().Single().Variante);
        Assert.True(Leer("[emotion: whisper] Ven.").Eventos.OfType<EventoVoz>().Single().Susurro);

        var sin = Leer("[emoción: tristeza] Se fue.");
        Assert.Null(sin.Eventos.OfType<EventoVoz>().Single().Variante);
        Assert.Contains(sin.Avisos, a => a.Contains("tristeza"));
    }

    [Fact]
    public void La_velocidad_entra_en_la_huella_solo_si_no_es_la_normal()
    {
        var ajustes = new AjustesNarracion();
        var normal = Huellas.DeBloque(Leer("Hola."), ajustes, "voz", "modelo");
        Assert.Equal(normal, Huellas.DeBloque(Leer("[normal] Hola."), ajustes, "voz", "modelo"));
        Assert.NotEqual(normal, Huellas.DeBloque(Leer("[lento] Hola."), ajustes, "voz", "modelo"));
    }
}
