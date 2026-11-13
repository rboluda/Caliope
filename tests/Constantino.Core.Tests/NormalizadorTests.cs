using Constantino.Core.Narracion;

namespace Constantino.Core.Tests;

public class NormalizadorTests
{
    [Theory]
    // Cardinales y concordancia con la palabra siguiente.
    [InlineData("Tengo 3 perros.", "Tengo tres perros.")]
    [InlineData("Había 1 coche y 1 casa.", "Había un coche y una casa.")]
    [InlineData("Vinieron 21 personas y 21 niños.", "Vinieron veintiuna personas y veintiún niños.")]
    [InlineData("Unas 200 personas y 300 coches.", "Unas doscientas personas y trescientos coches.")]
    [InlineData("Fueron 3 días.", "Fueron tres días.")]
    [InlineData("Lo intentó 21 veces.", "Lo intentó veintiuna veces.")]
    [InlineData("El 1 de mayo.", "El uno de mayo.")]
    [InlineData("Cuenta hasta 100.", "Cuenta hasta cien.")]
    [InlineData("Eran 101 perros.", "Eran ciento un perros.")]
    [InlineData("Pesa 1.250 kilos.", "Pesa mil doscientos cincuenta kilos.")]
    [InlineData("Hay 21.000 personas.", "Hay veintiuna mil personas.")]
    [InlineData("Son 1.000.000 de habitantes.", "Son un millón de habitantes.")]
    [InlineData("Ganó 2.000.000 votos.", "Ganó dos millones de votos.")]
    [InlineData("Hay 3.500.250 árboles.", "Hay tres millones quinientos mil doscientos cincuenta árboles.")]
    [InlineData("Mide 3,5 metros.", "Mide tres coma cinco metros.")]
    [InlineData("Vale 0,05.", "Vale cero coma cero cinco.")]
    [InlineData("El agente 007.", "El agente cero cero siete.")]
    [InlineData("Versión 1.3.1 publicada.", "Versión uno punto tres punto uno publicada.")]
    [InlineData("Llama al 600123456.", "Llama al seis cero cero uno dos tres cuatro cinco seis.")]
    // Años.
    [InlineData("En 2021 hubo una nevada.", "En dos mil veintiuno hubo una nevada.")]
    [InlineData("Nació en 1990.", "Nació en mil novecientos noventa.")]
    // Fechas y horas.
    [InlineData("Fue el 13/11/2026.", "Fue el trece de noviembre de dos mil veintiséis.")]
    [InlineData("El 1-1-99.", "El uno de enero de mil novecientos noventa y nueve.")]
    [InlineData("Salió a las 2:30.", "Salió a las dos y media.")]
    [InlineData("A la 1:15.", "A la una y cuarto.")]
    [InlineData("A las 21:00 h.", "A las veintiuna horas.")]
    [InlineData("A las 14.30 h en punto.", "A las catorce y media en punto.")]
    [InlineData("A las 9:05.", "A las nueve y cinco.")]
    [InlineData("Llegó a las 8:00.", "Llegó a las ocho en punto.")]
    // Monedas y porcentajes.
    [InlineData("Cuesta 1.250 €.", "Cuesta mil doscientos cincuenta euros.")]
    [InlineData("Cuesta 1 €.", "Cuesta un euro.")]
    [InlineData("Cuesta 25,50 €.", "Cuesta veinticinco euros con cincuenta céntimos.")]
    [InlineData("Solo 0,99 €.", "Solo noventa y nueve céntimos.")]
    [InlineData("Pagó $5.", "Pagó cinco dólares.")]
    [InlineData("Costó 21 £.", "Costó veintiuna libras.")]
    [InlineData("Un premio de 3 M€.", "Un premio de tres millones de euros.")]
    [InlineData("Ganó 2.000.000 €.", "Ganó dos millones de euros.")]
    [InlineData("Subió un 15 %.", "Subió un quince por ciento.")]
    [InlineData("Bajó un 3,5%.", "Bajó un tres coma cinco por ciento.")]
    // Ordinales.
    [InlineData("Vive en el 3.º piso.", "Vive en el tercer piso.")]
    [InlineData("Quedó 2.ª en la carrera.", "Quedó segunda en la carrera.")]
    [InlineData("Fue el 1.º en llegar.", "Fue el primero en llegar.")]
    [InlineData("El 1er día.", "El primer día.")]
    [InlineData("La 21.ª edición.", "La vigésima primera edición.")]
    // Unidades y signos.
    [InlineData("A 120 km/h.", "A ciento veinte kilómetros por hora.")]
    [InlineData("Quedaban 1 km y 1 h.", "Quedaban un kilómetro y una hora.")]
    [InlineData("Hacía -5 °C.", "Hacía menos cinco grados.")]
    [InlineData("Hacía 25 ºC fuera.", "Hacía veinticinco grados fuera.")]
    [InlineData("Una caída de 3 m.", "Una caída de tres metros.")]
    // Rangos.
    [InlineData("Entre 1990-2000.", "Entre mil novecientos noventa a dos mil.")]
    [InlineData("Llama al 600-123-456.", "Llama al seiscientos-ciento veintitrés-cuatrocientos cincuenta y seis.")]
    // Romanos.
    [InlineData("En el siglo XIX.", "En el siglo diecinueve.")]
    [InlineData("En el s. XX.", "En el siglo veinte.")]
    [InlineData("El rey Felipe II.", "El rey Felipe segundo.")]
    [InlineData("Con Alfonso XIII.", "Con Alfonso trece.")]
    // Abreviaturas.
    [InlineData("El Sr. García y la Dra. López.", "El señor García y la doctora López.")]
    [InlineData("Vive en la C/ Mayor, n.º 5.", "Vive en la calle Mayor, número cinco.")]
    [InlineData("Peras, manzanas, etc. Y nada más.", "Peras, manzanas, etcétera. Y nada más.")]
    [InlineData("Peras, manzanas, etc. y uvas.", "Peras, manzanas, etcétera y uvas.")]
    [InlineData("Viajó a EE. UU. en verano.", "Viajó a Estados Unidos en verano.")]
    [InlineData("En el año 300 a. C.", "En el año trescientos antes de Cristo")]
    // Lo que no es para tocar.
    [InlineData("Un archivo MP3 y el COVID.", "Un archivo MP3 y el COVID.")]
    [InlineData("Sin cifras, nada cambia.", "Sin cifras, nada cambia.")]
    public void Lee_con_palabras(string escrito, string dicho) =>
        Assert.Equal(dicho, NormalizadorEspanol.Normalizar(escrito));

    [Fact]
    public void El_guion_en_espanol_lee_las_cifras_con_palabras_y_el_diccionario_manda()
    {
        var contexto = new ContextoGuion(Diccionario: [new Pronunciacion("112", "uno uno dos")], Idioma: "Spanish");
        var bloque = Guion.Leer("Llama al 112. Son 3 €.", 0.6, 1.5, contexto).Single();
        Assert.Equal("Llama al uno uno dos. Son tres euros.", bloque.TextoLeido);

        // Con una voz en otro idioma no se toca.
        var ingles = Guion.Leer("Call 3 friends.", 0.6, 1.5, new ContextoGuion(Idioma: "English")).Single();
        Assert.Equal("Call 3 friends.", ingles.TextoLeido);
    }

    [Fact]
    public void La_revision_entiende_las_cifras_que_escribe_Whisper()
    {
        var c = Revision.Comparar("Cuesta mil doscientos cincuenta euros. Llegó a las dos y media.",
            "Cuesta 1.250 €. Llegó a las 2:30.", espanol: true);
        Assert.Equal(0, c.Errores);
    }
}
