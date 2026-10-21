using Constantino.Core.Narracion;

namespace Constantino.Core.Tests;

public class RevisionTests
{
    private const string Texto = "Sábado por la noche. Diluvia. En la puerta de la discoteca hay una chica empapada, temblando de frío.";

    [Fact]
    public void Lo_que_se_oye_igual_salvo_tildes_y_puntuacion_esta_bien()
    {
        var c = Revision.Comparar(Texto, "sabado por la noche diluvia en la puerta de la discoteca hay una chica empapada temblando de frio");

        Assert.Equal(0, c.Errores);
        Assert.False(c.MalLeido);
    }

    [Fact]
    public void Un_par_de_palabras_mal_oidas_se_toleran()
    {
        var c = Revision.Comparar(Texto, "Sábado por la noche. Diluvia. En la puerta de la discoteca hay una niña empapada, temblando del frío.");

        Assert.Equal(2, c.Errores);
        Assert.False(c.MalLeido);
    }

    [Fact]
    public void Una_frase_cortada_se_detecta()
    {
        var c = Revision.Comparar(Texto, "Sábado por la noche.");

        Assert.True(c.MalLeido);
        Assert.Equal(14, c.Errores); // 18 palabras, se oyen 4
    }

    [Fact]
    public void Texto_inventado_se_detecta() =>
        Assert.True(Revision.Comparar("Hola a todos.", "Hola a todos, bienvenidos al canal de misterio de hoy").MalLeido);

    [Fact]
    public void Una_cifra_vale_como_la_lean()
    {
        Assert.Equal(0, Revision.Comparar("Ocurrió en 1990 en la sierra.", "Ocurrió en mil novecientos noventa en la sierra.").Errores);
        Assert.Equal(0, Revision.Comparar("Tenía 7 años.", "Tenía siete años.").Errores);
        Assert.Equal(1, Revision.Comparar("Tenía 7 años.", "Tenía siete meses.").Errores);
    }
}
