using System.Text;
using System.Text.Json.Nodes;
using Constantino.Core.IA;

namespace Constantino.Core.Voces;

/// <summary>
/// Convierte una descripción libre en español («un abuelo gallego que cuenta historias junto al fuego») en las opciones
/// de la descripción guiada, con la IA local. Así el diseñador recibe la descripción en inglés que mejor entiende, y
/// Calíope puede comprobar después si la voz encaja con lo pedido (sexo, timbre, edad).
/// </summary>
public static class DescriptorVoz
{
    public static async Task<DescripcionGuiada> ConvertirAsync(IModeloLenguaje ia, string descripcion, CancellationToken ct)
    {
        var respuesta = await ia.PreguntarAsync(Sistema(), descripcion.Trim(), 300, json: true, ct, temperatura: 0.2)
            .ConfigureAwait(false);
        return Leer(respuesta);
    }

    /// <summary>Las instrucciones para la IA, con las claves permitidas sacadas del propio catálogo.</summary>
    private static string Sistema()
    {
        static string Lista(IReadOnlyList<OpcionDiseno> opciones) =>
            string.Join(", ", opciones.Select(o => $"\"{o.Clave}\" ({o.Ingles})"));

        var sb = new StringBuilder();
        sb.AppendLine("Conviertes la descripción de una voz, escrita en español por un usuario, en las opciones de un diseñador de voces.");
        sb.AppendLine("Responde SOLO con un objeto JSON con estas claves:");
        sb.AppendLine("- \"sexo\": \"hombre\", \"mujer\" o \"indiferente\".");
        sb.AppendLine("- \"edad\": número de años (15 a 90). Si no lo dice, estima uno razonable.");
        sb.AppendLine($"- \"timbre\": una de {Lista(CatalogoDiseno.Timbres)}.");
        sb.AppendLine($"- \"textura\": una de {Lista(CatalogoDiseno.Texturas)}.");
        sb.AppendLine($"- \"ritmo\": una de {Lista(CatalogoDiseno.Ritmos)}.");
        sb.AppendLine($"- \"tono\": una de {Lista(CatalogoDiseno.Tonos)}.");
        sb.AppendLine($"- \"acento\": una de {Lista(CatalogoDiseno.Acentos)}, o null si habla otro idioma. Si es de España con un " +
                      "acento que no está en la lista (gallego, vasco, catalán, asturiano…), usa \"castellano\" y escribe ese acento en \"extra\".");
        sb.AppendLine("- \"extra\": UNA frase corta EN INGLÉS (como mucho 25 palabras) con lo que no recogen las opciones: el personaje, " +
                      "la situación, la emoción, un acento regional que no esté en la lista (por ejemplo \"with a slight Galician accent\"). " +
                      "Empieza por \"Like\" o \"With\". Cadena vacía si no hay nada más.");
        sb.AppendLine("Elige siempre la opción más cercana de cada lista; no inventes claves.");
        return sb.ToString();
    }

    /// <summary>Lee la respuesta; lo que falte o no sea válido toma un valor neutro.</summary>
    public static DescripcionGuiada Leer(string respuesta)
    {
        JsonObject? raiz = null;
        try
        {
            // Por si la IA envuelve el JSON en texto o en un bloque de código.
            var inicio = respuesta.IndexOf('{');
            var fin = respuesta.LastIndexOf('}');
            if (inicio >= 0 && fin > inicio)
                raiz = JsonNode.Parse(respuesta[inicio..(fin + 1)]) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        string? Texto(string clave) =>
            raiz?[clave] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        string Clave(IReadOnlyList<OpcionDiseno> opciones, string clave, string porDefecto) =>
            CatalogoDiseno.Buscar(opciones, Texto(clave)?.ToLowerInvariant())?.Clave ?? porDefecto;

        var sexo = Texto("sexo")?.ToLowerInvariant() switch
        {
            "hombre" => SexoVoz.Hombre,
            "mujer" => SexoVoz.Mujer,
            _ => SexoVoz.Indiferente,
        };
        var edad = raiz?["edad"] is JsonValue e && (e.TryGetValue<int>(out var n) || int.TryParse(e.ToString(), out n)) ? n : 40;
        var acento = CatalogoDiseno.Buscar(CatalogoDiseno.Acentos, Texto("acento")?.ToLowerInvariant())?.Clave;
        var extra = Texto("extra") ?? "";
        return new DescripcionGuiada(
            sexo,
            Math.Clamp(edad, 15, 90),
            Clave(CatalogoDiseno.Timbres, "timbre", "media"),
            Clave(CatalogoDiseno.Texturas, "textura", "calida"),
            Clave(CatalogoDiseno.Ritmos, "ritmo", "pausado"),
            Clave(CatalogoDiseno.Tonos, "tono", "narrativo"),
            acento,
            extra.Length > 200 ? extra[..200] : extra);
    }
}
