using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using static Constantino.Core.Idioma;

namespace Constantino.Core.IA;

/// <summary>Un modelo de lenguaje al que se le pregunta (en las pruebas, uno falso).</summary>
public interface IModeloLenguaje
{
    /// <summary>Una pregunta con sus instrucciones; con <paramref name="json"/>, la respuesta es un objeto JSON.</summary>
    Task<string> PreguntarAsync(string sistema, string pedido, int maxTokens, bool json, CancellationToken ct, double temperatura = 0.3);
}

/// <summary>
/// Un modelo de lenguaje local: arranca llama-server (llama.cpp) con el modelo GGUF la primera vez que se usa y lo
/// cierra al desecharlo, para dejar libre la tarjeta. Lo usan el traductor del doblaje y el redactor de actas.
/// </summary>
/// <param name="contexto">Tokens de contexto. Con más de 16k, la caché va en 8 bits para que quepa en la tarjeta.</param>
public sealed class ServidorIA(string servidor, string modelo, bool usaGpu, int contexto = 16384) : IModeloLenguaje, IAsyncDisposable
{
    private Process? _proceso;
    private HttpClient? _http;
    private readonly StringBuilder _salida = new();

    /// <summary>Una pregunta con sus instrucciones; con <paramref name="json"/>, la respuesta es un objeto JSON.</summary>
    public async Task<string> PreguntarAsync(string sistema, string pedido, int maxTokens, bool json, CancellationToken ct,
        double temperatura = 0.3)
    {
        var http = await ArrancarAsync(ct).ConfigureAwait(false);
        var cuerpo = new Dictionary<string, object>
        {
            ["messages"] = new object[] { new { role = "system", content = sistema }, new { role = "user", content = pedido } },
            ["temperature"] = temperatura,
            ["max_tokens"] = maxTokens,
            ["chat_template_kwargs"] = new { enable_thinking = false },
        };
        if (json)
            cuerpo["response_format"] = new { type = "json_object" };
        using var respuesta = await http.PostAsJsonAsync("/v1/chat/completions", cuerpo, ct).ConfigureAwait(false);
        respuesta.EnsureSuccessStatusCode();
        var datos = await respuesta.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false);
        return datos?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
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
        var argumentos = new List<string> { "-m", modelo, "-ngl", usaGpu ? "99" : "0", "-c", contexto.ToString(), "--host", "127.0.0.1",
            "--port", puerto.ToString(), "--jinja", "--no-webui" };
        if (contexto > 16384)
            argumentos.AddRange(["-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0"]);
        foreach (var a in argumentos)
            inicio.ArgumentList.Add(a);
        _proceso = Process.Start(inicio) ?? throw new InvalidOperationException(T("No se ha podido arrancar el traductor."));
        _proceso.OutputDataReceived += (_, e) => Anotar(e.Data);
        _proceso.ErrorDataReceived += (_, e) => Anotar(e.Data);
        _proceso.BeginOutputReadLine();
        _proceso.BeginErrorReadLine();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{puerto}"), Timeout = TimeSpan.FromMinutes(20) };

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
