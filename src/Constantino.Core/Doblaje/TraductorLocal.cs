using System.Text.Json;
using System.Text.Json.Nodes;
using Constantino.Core.IA;

namespace Constantino.Core.Doblaje;

/// <summary>
/// Traductor con un modelo de lenguaje local (<see cref="ServidorIA"/>), que se arranca la primera vez que se usa y se
/// cierra al desecharlo, para dejar libre la tarjeta. Traduce por tandas con el guion como contexto, revisa
/// su propia traducción en una segunda pasada y acorta frases que no caben. Reglas probadas en la fase 0 del doblaje.
/// </summary>
public sealed class TraductorLocal(string servidor, string modelo, bool usaGpu) : ITraductorDoblaje, IAsyncDisposable
{
    public const int Tanda = 40;

    private const string Sistema = """
        Eres un traductor profesional de doblaje del inglés al castellano de España, nativo de Madrid. Traduces guiones de vídeos que se van a leer en voz alta.
        Reglas:
        1. Castellano de España: «vosotros» al hablar a la audiencia («no olvidéis», «quedaos»), «vale», «coche», «ordenador». Nunca «ustedes», «ahorita», «carro».
        2. Nada de anglicismos ni calcos: «Okay» → «Vale»; «Oh my God» → «Madre mía»; «No take-backs» → «Y sin echarse atrás». Traduce el sentido y la intención, no las palabras.
        3. Concordancia perfecta de género y número («la magia», «lo pide») y la misma persona verbal que el original.
        4. Números, cantidades y fechas escritos con letras, como se dicen («cien dólares», «mil novecientos cincuenta y dos»).
        5. Conserva los nombres propios y el sentido exacto: no inventes ni quites información importante.
        6. Cada frase tiene un máximo de caracteres porque debe caber en el tiempo del original: no lo superes. Si no cabe, quita relleno («la verdad», «un poco», «así que»), nunca lo esencial.
        7. «persona» indica quién dice cada frase. En los diálogos, mantén el trato entre ellos (tú o usted) y concuerda bien quién habla con quién.
        Responde solo con JSON.
        """;

    private const string Formato = "Devuelve {\"frases\": [{\"id\": n, \"es\": \"...\"}]} con todas las frases, en orden.";

    private readonly ServidorIA _ia = new(servidor, modelo, usaGpu);
    // Para acortar frases mientras se generan las voces: en el procesador, para no quitarle la tarjeta al motor de voz
    // (los dos juntos no caben en 8 GB). Son frases cortas: tarda unos segundos.
    private readonly ServidorIA _iaProcesador = new(servidor, modelo, usaGpu: false, contexto: 2048);

    public async Task<IReadOnlyDictionary<int, string>> TraducirAsync(IReadOnlyList<FraseATraducir> frases, CancellationToken ct)
    {
        var resultado = new Dictionary<int, string>();
        for (var inicio = 0; inicio < frases.Count; inicio += Tanda)
        {
            var tanda = frases.Skip(inicio).Take(Tanda).ToList();
            var anteriores = frases.Skip(Math.Max(0, inicio - 20)).Take(Math.Min(20, inicio)).Select(f => f.Original).ToList();
            var datos = tanda.Select(f => new { id = f.Id, persona = f.Hablante, max_caracteres = f.MaxCaracteres, en = f.Original }).ToList();
            var pedido = "Traduce cada frase del guion. " + Formato + "\n"
                         + (anteriores.Count > 0 ? "Contexto (frases anteriores, no las devuelvas): " + JsonSerializer.Serialize(anteriores) + "\n" : "")
                         + "\n" + JsonSerializer.Serialize(datos);
            var primera = LeerFrases(await PreguntarAsync(pedido, 4096, ct).ConfigureAwait(false));

            var revision = tanda.Select(f => new { id = f.Id, persona = f.Hablante, max_caracteres = f.MaxCaracteres, en = f.Original, es = primera.GetValueOrDefault(f.Id, "") }).ToList();
            pedido = "Revisa estas traducciones como un corrector de doblaje exigente. Corrige errores de gramática y concordancia, "
                     + "calcos del inglés, palabras de Latinoamérica, sentido cambiado y frases que superan max_caracteres. Si una está "
                     + "bien, déjala igual. " + Formato + "\n\n" + JsonSerializer.Serialize(revision);
            var revisada = LeerFrases(await PreguntarAsync(pedido, 4096, ct).ConfigureAwait(false));
            foreach (var f in tanda)
            {
                var es = revisada.GetValueOrDefault(f.Id) ?? primera.GetValueOrDefault(f.Id);
                if (!string.IsNullOrWhiteSpace(es))
                    resultado[f.Id] = es.Trim();
            }
        }
        // Lo siguiente es generar las voces: la tarjeta queda libre para el motor.
        await _ia.DisposeAsync().ConfigureAwait(false);
        return resultado;
    }

    public async Task<string> AcortarAsync(string original, string traduccion, int maxCaracteres, CancellationToken ct)
    {
        var pedido = $"Esta frase traducida tiene {traduccion.Length} caracteres y tiene que caber en {maxCaracteres}. "
                     + $"Reescríbela más corta, natural y con el mismo sentido. Original: «{original}». Traducción: «{traduccion}». "
                     + "Devuelve {\"es\": \"...\"}.";
        var respuesta = await _iaProcesador.PreguntarAsync(Sistema, pedido, 256, json: true, ct).ConfigureAwait(false);
        return JsonNode.Parse(respuesta)?["es"]?.GetValue<string>() ?? traduccion;
    }

    /// <summary>Las frases de una respuesta {"frases": [{"id", "es"}]}, tolerando ids escritos como texto.</summary>
    internal static Dictionary<int, string> LeerFrases(string json)
    {
        var frases = new Dictionary<int, string>();
        if (JsonNode.Parse(json)?["frases"] is not JsonArray lista)
            return frases;
        foreach (var f in lista.OfType<JsonObject>())
        {
            var id = f["id"] is JsonValue v && (v.TryGetValue<int>(out var n) || int.TryParse(v.ToString(), out n)) ? n : -1;
            if (id >= 0 && f["es"]?.GetValue<string>() is { } es)
                frases[id] = es;
        }
        return frases;
    }

    private Task<string> PreguntarAsync(string pedido, int maxTokens, CancellationToken ct) =>
        _ia.PreguntarAsync(Sistema, pedido, maxTokens, json: true, ct);

    public async ValueTask DisposeAsync()
    {
        await _ia.DisposeAsync().ConfigureAwait(false);
        await _iaProcesador.DisposeAsync().ConfigureAwait(false);
    }
}
