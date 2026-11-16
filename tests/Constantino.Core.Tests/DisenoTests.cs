using Constantino.Core.IA;
using Constantino.Core.Motores;
using Constantino.Core.Voces;

namespace Constantino.Core.Tests;

public class DisenoTests
{
    private const string Texto = "Aquella noche de invierno, el viejo guardabosques encendió la chimenea y nos contó una historia.";

    [Fact]
    public void El_rango_de_tono_depende_de_lo_pedido()
    {
        Assert.Null(RevisionDiseno.RangoTono(SexoVoz.Indiferente, "grave", 40));
        var hombreGrave = RevisionDiseno.RangoTono(SexoVoz.Hombre, "grave", 40)!.Value;
        var mujerMedia = RevisionDiseno.RangoTono(SexoVoz.Mujer, "media", 40)!.Value;
        Assert.InRange(100, hombreGrave.Min, hombreGrave.Max);
        Assert.InRange(200, mujerMedia.Min, mujerMedia.Max);
        Assert.True(hombreGrave.Max < mujerMedia.Min);
        // Un niño, más agudo aunque se pida «grave».
        Assert.True(RevisionDiseno.RangoTono(SexoVoz.Hombre, "grave", 10)!.Value.Min > 150);
    }

    [Fact]
    public void Descarta_la_voz_que_no_encaja_con_lo_pedido()
    {
        var rango = RevisionDiseno.RangoTono(SexoVoz.Hombre, "media", 60);
        // La voz de las pruebas: «hombre de sesenta años» con 197 Hz.
        var aguda = RevisionDiseno.Problemas(new MedidasVoz(197, 0.8, 13), null, Texto, "Spanish", rango);
        Assert.Contains(aguda, p => p.Contains("aguda"));
        Assert.Empty(RevisionDiseno.Problemas(new MedidasVoz(118, 0.8, 13), null, Texto, "Spanish", rango));
        Assert.Contains(RevisionDiseno.Problemas(new MedidasVoz(118, 0.05, 13), null, Texto, "Spanish", rango), p => p.Contains("susurro"));
    }

    [Fact]
    public void Descarta_la_voz_que_se_salta_palabras()
    {
        var medidas = new MedidasVoz(118, 0.8, 13);
        Assert.Empty(RevisionDiseno.Problemas(medidas, Texto, Texto, "Spanish", null));
        var oido = "Aquella noche de invierno, el viejo encendió la chimenea.";
        Assert.Contains(RevisionDiseno.Problemas(medidas, oido, Texto, "Spanish", null), p => p.Contains("palabras"));
    }

    [Fact]
    public void Whisper_partiendo_mal_una_palabra_no_es_un_error_de_la_voz()
    {
        // Lo que pasó con las tres primeras variantes de la prueba real.
        Assert.True(RevisionDiseno.LeeElTexto(Texto,
            "Aquella noche de invierno, el viejo guardabosque se encendió la chimenea y nos contó una historia.", "Spanish"));
        Assert.False(RevisionDiseno.LeeElTexto(Texto, "Aquella noche de invierno, el viejo encendió la chimenea y nos contó una historia.", "Spanish"));
        Assert.False(RevisionDiseno.LeeElTexto(Texto, "Aquella noche de invierno, el viejo guardabosques encendió la chimenea.", "Spanish"));
    }

    [Fact]
    public void Mas_asi_comprueba_que_el_cambio_se_cumple()
    {
        var partida = new MedidasVoz(114, 0.8, 17.4);
        Assert.True(RevisionDiseno.Cumple(CambioDiseno.MasGrave, partida, partida with { TonoHz = 88 }));
        Assert.False(RevisionDiseno.Cumple(CambioDiseno.MasGrave, partida, partida with { TonoHz = 113 }));
        // En las pruebas, «más pausada» salió más rápida: eso no vale.
        Assert.False(RevisionDiseno.Cumple(CambioDiseno.MasPausada, partida, partida with { Duracion = 16.1 }));
        Assert.True(RevisionDiseno.Cumple(CambioDiseno.MasPausada, partida, partida with { Duracion = 19 }));
        Assert.True(RevisionDiseno.Cumple(CambioDiseno.MasIntensa, partida, partida with { TonoHz = 140 }));

        var problemas = RevisionDiseno.Problemas(partida with { TonoHz = 113 }, null, Texto, "Spanish", null,
            (CambioDiseno.MasGrave, partida));
        Assert.Contains(problemas, p => p.Contains("más grave"));
    }

    [Fact]
    public void Mas_asi_retoca_la_descripcion()
    {
        const string original = "A man around 40 years old. Deep voice.";
        Assert.Equal(original, RevisionDiseno.Instruccion(original, CambioDiseno.Igual, false));
        Assert.Equal("A man around 40 years old. Deep voice. Make the voice a little deeper and lower-pitched.",
            RevisionDiseno.Instruccion(original, CambioDiseno.MasGrave, false));
        Assert.Contains("noticeably", RevisionDiseno.Instruccion(original, CambioDiseno.MasPausada, true));
        Assert.True(RevisionDiseno.CambiaElTono(CambioDiseno.MasMayor));
        Assert.False(RevisionDiseno.CambiaElTono(CambioDiseno.MasPausada));
    }

    [Fact]
    public void Lee_la_respuesta_de_la_IA()
    {
        var d = DescriptorVoz.Leer("""
            ```json
            {"sexo": "hombre", "edad": 72, "timbre": "grave", "textura": "ronca", "ritmo": "muy-pausado",
             "tono": "narrativo", "acento": "castellano", "extra": "Like a Galician grandfather telling stories by the fire, with a slight Galician accent"}
            ```
            """);
        Assert.Equal(new DescripcionGuiada(SexoVoz.Hombre, 72, "grave", "ronca", "muy-pausado", "narrativo", "castellano",
            "Like a Galician grandfather telling stories by the fire, with a slight Galician accent"), d);
    }

    [Fact]
    public void Lo_que_la_IA_no_da_bien_toma_un_valor_neutro()
    {
        var d = DescriptorVoz.Leer("""{"sexo": "señor", "edad": "muchos", "timbre": "grave", "textura": "de terciopelo", "acento": "gallego"}""");
        Assert.Equal(SexoVoz.Indiferente, d.Sexo);
        Assert.Equal(40, d.Edad);
        Assert.Equal("grave", d.Timbre);
        Assert.Equal("calida", d.Textura);
        Assert.Null(d.Acento);

        var nada = DescriptorVoz.Leer("No sé qué decirte.");
        Assert.Equal(new DescripcionGuiada(SexoVoz.Indiferente, 40, "media", "calida", "pausado", "narrativo", null, ""), nada);
    }

    [Fact]
    public async Task Pide_a_la_IA_solo_claves_del_catalogo()
    {
        var ia = new IAFalsa("""{"sexo": "mujer", "edad": 30, "timbre": "aguda", "textura": "clara", "ritmo": "agil", "tono": "alegre", "acento": null, "extra": ""}""");
        var d = await DescriptorVoz.ConvertirAsync(ia, "  una chica joven y alegre  ", CancellationToken.None);
        Assert.Equal(SexoVoz.Mujer, d.Sexo);
        Assert.Equal("una chica joven y alegre", ia.Pedido);
        Assert.Contains("\"muy-pausado\"", ia.Sistema);
        Assert.Contains("\"castellano\"", ia.Sistema);
    }

    private sealed class IAFalsa(string respuesta) : IModeloLenguaje
    {
        public string Sistema { get; private set; } = "";
        public string Pedido { get; private set; } = "";

        public Task<string> PreguntarAsync(string sistema, string pedido, int maxTokens, bool json, CancellationToken ct, double temperatura = 0.3)
        {
            (Sistema, Pedido) = (sistema, pedido);
            return Task.FromResult(respuesta);
        }
    }
}
