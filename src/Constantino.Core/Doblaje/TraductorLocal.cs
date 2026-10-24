using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Doblaje;

/// <summary>
/// Traductor con un modelo de lenguaje local: arranca llama-server (llama.cpp) con el modelo GGUF la primera vez que
/// se usa y lo cierra al desecharlo, para dejar libre la tarjeta. Traduce por tandas con el guion como contexto, revisa
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
        Responde solo con JSON.
        """;

    private const string Formato = "Devuelve {\"frases\": [{\"id\": n, \"es\": \"...\"}]} con todas las frases, en orden.";

    private Process? _proceso;
    private HttpClient? _http;
    private readonly StringBuilder _salida = new();

    public async Task<IReadOnlyDictionary<int, string>> TraducirAsync(IReadOnlyList<FraseATraducir> frases, CancellationToken ct)
    {
        var resultado = new Dictionary<int, string>();
        for (var inicio = 0; inicio < frases.Count; inicio += Tanda)
        {
            var tanda = frases.Skip(inicio).Take(Tanda).ToList();
            var anteriores = frases.Skip(Math.Max(0, inicio - 20)).Take(Math.Min(20, inicio)).Select(f => f.Original).ToList();
            var datos = tanda.Select(f => new { id = f.Id, max_caracteres = f.MaxCaracteres, en = f.Original }).ToList();
            var pedido = "Traduce cada frase del guion. " + Formato + "\n"
                         + (anteriores.Count > 0 ? "Contexto (frases anteriores, no las devuelvas): " + JsonSerializer.Serialize(anteriores) + "\n" : "")
                         + "\n" + JsonSerializer.Serialize(datos);
            var primera = LeerFrases(await PreguntarAsync(pedido, 4096, ct).ConfigureAwait(false));

            var revision = tanda.Select(f => new { id = f.Id, max_caracteres = f.MaxCaracteres, en = f.Original, es = primera.GetValueOrDefault(f.Id, "") }).ToList();
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
        return resultado;
    }

    public async Task<string> AcortarAsync(string original, string traduccion, int maxCaracteres, CancellationToken ct)
    {
        var pedido = $"Esta frase traducida tiene {traduccion.Length} caracteres y tiene que caber en {maxCaracteres}. "
                     + $"Reescríbela más corta, natural y con el mismo sentido. Original: «{original}». Traducción: «{traduccion}». "
                     + "Devuelve {\"es\": \"...\"}.";
        var respuesta = await PreguntarAsync(pedido, 256, ct).ConfigureAwait(false);
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

    private async Task<string> PreguntarAsync(string pedido, int maxTokens, CancellationToken ct)
    {
        var http = await ArrancarAsync(ct).ConfigureAwait(false);
        var cuerpo = new
        {
            messages = new object[] { new { role = "system", content = Sistema }, new { role = "user", content = pedido } },
            temperature = 0.3,
            max_tokens = maxTokens,
            response_format = new { type = "json_object" },
            chat_template_kwargs = new { enable_thinking = false },
        };
        using var respuesta = await http.PostAsJsonAsync("/v1/chat/completions", cuerpo, ct).ConfigureAwait(false);
        respuesta.EnsureSuccessStatusCode();
        var json = await respuesta.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false);
        return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException(T("El traductor no ha devuelto nada."));
    }

    private async Task<HttpClient> ArrancarAsync(CancellationToken ct)
    {
        if (_http is not null && _proceso is { HasExited: false })
            return _http;
        if (!File.Exists(servidor) || !File.Exists(modelo))
            throw new InvalidOperationException(T("Falta el traductor: instálalo desde Componentes."));
        var puerto = PuertoLibre();
        var inicio = new ProcessStartInfo(servidor)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(servidor)!,
        };
        foreach (var a in new[] { "-m", modelo, "-ngl", usaGpu ? "99" : "0", "-c", "16384", "--host", "127.0.0.1",
                     "--port", puerto.ToString(), "--jinja", "--no-webui" })
            inicio.ArgumentList.Add(a);
        _proceso = Process.Start(inicio) ?? throw new InvalidOperationException(T("No se ha podido arrancar el traductor."));
        _proceso.OutputDataReceived += (_, e) => Anotar(e.Data);
        _proceso.ErrorDataReceived += (_, e) => Anotar(e.Data);
        _proceso.BeginOutputReadLine();
        _proceso.BeginErrorReadLine();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{puerto}"), Timeout = TimeSpan.FromMinutes(10) };

        var limite = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();
            if (_proceso.HasExited)
                throw new InvalidOperationException(T("El traductor se ha cerrado al arrancar: {0}", UltimasLineas()));
            try
            {
                using var salud = await _http.GetAsync("/health", ct).ConfigureAwait(false);
                if (salud.StatusCode == HttpStatusCode.OK)
                    return _http;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(T("El traductor no ha respondido a tiempo."));
    }

    private void Anotar(string? linea)
    {
        if (linea is null)
            return;
        lock (_salida)
        {
            _salida.AppendLine(linea);
            if (_salida.Length > 20_000)
                _salida.Remove(0, _salida.Length - 10_000);
        }
    }

    private string UltimasLineas()
    {
        lock (_salida)
            return string.Join(" · ", _salida.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)).Trim();
    }

    private static int PuertoLibre()
    {
        var oyente = new TcpListener(IPAddress.Loopback, 0);
        oyente.Start();
        var puerto = ((IPEndPoint)oyente.LocalEndpoint).Port;
        oyente.Stop();
        return puerto;
    }

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        _http = null;
        try
        {
            if (_proceso is { HasExited: false })
                _proceso.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        _proceso?.Dispose();
        _proceso = null;
        return ValueTask.CompletedTask;
    }
}
