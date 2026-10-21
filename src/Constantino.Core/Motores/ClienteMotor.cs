using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Motores;

/// <summary>Aviso de avance de una petición. <paramref name="Valor"/> es null si no se puede medir.</summary>
public sealed record EventoMotor(double? Valor, string Mensaje);

/// <summary>Error que devuelve el motor: un código conocido y un mensaje.</summary>
public sealed class MotorException(string codigo, string mensaje, string? detalle = null) : Exception(mensaje)
{
    public string Codigo { get; } = codigo;
    public string? Detalle { get; } = detalle;
}

/// <summary>El proceso del motor terminó o no respondió.</summary>
public sealed class MotorCaidoException(string mensaje, string? diagnostico = null) : Exception(mensaje)
{
    public string? Diagnostico { get; } = diagnostico;
}

/// <summary>
/// Habla con un motor de voz (proceso Python) por JSON de una línea. Las peticiones se atienden en orden;
/// cada una recibe sus avisos de progreso y termina con un resultado o un error.
/// </summary>
public sealed class ClienteMotor : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const int LineasDiagnostico = 60;

    private readonly TextWriter _haciaMotor;
    private readonly TextReader _desdeMotor;
    private readonly Process? _proceso;
    private readonly ConcurrentDictionary<string, Pendiente> _pendientes = new();
    private readonly SemaphoreSlim _escritura = new(1, 1);
    private readonly TaskCompletionSource<JsonObject> _listo = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _diagnostico = new();
    private readonly Task _lector;
    private volatile bool _caido;

    private sealed record Pendiente(TaskCompletionSource<JsonElement> Fin, IProgress<EventoMotor>? Progreso);

    /// <summary>Para pruebas: conecta con un motor a través de dos flujos de texto ya abiertos.</summary>
    public ClienteMotor(TextWriter haciaMotor, TextReader desdeMotor) : this(haciaMotor, desdeMotor, null)
    {
    }

    private ClienteMotor(TextWriter haciaMotor, TextReader desdeMotor, Process? proceso)
    {
        _haciaMotor = haciaMotor;
        _desdeMotor = desdeMotor;
        _proceso = proceso;
        _lector = Task.Run(LeerAsync);
    }

    /// <summary>Lo que el motor contó al arrancar (versión, nombre…).</summary>
    public Task<JsonObject> Listo => _listo.Task;

    public bool Vivo => !_caido && _proceso is not { HasExited: true };

    /// <summary>Cada línea de diagnóstico (stderr) del motor, según llega.</summary>
    public event Action<string>? Diagnostico;

    public string UltimoDiagnostico
    {
        get
        {
            lock (_diagnostico)
                return string.Join(Environment.NewLine, _diagnostico);
        }
    }

    /// <summary>Arranca <c>python -m constantino_motor</c> y espera a que diga que está listo.</summary>
    public static async Task<ClienteMotor> ArrancarAsync(string python, string carpetaMotores,
        IReadOnlyDictionary<string, string> entorno, TimeSpan espera, CancellationToken ct)
    {
        var inicio = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = carpetaMotores,
        };
        inicio.ArgumentList.Add("-m");
        inicio.ArgumentList.Add("constantino_motor");
        foreach (var (clave, valor) in entorno)
            inicio.Environment[clave] = valor;
        inicio.Environment["PYTHONPATH"] = carpetaMotores;

        var proceso = Process.Start(inicio) ?? throw new MotorCaidoException(T("No se ha podido arrancar el motor de voz."));
        var cliente = new ClienteMotor(proceso.StandardInput, proceso.StandardOutput, proceso);
        proceso.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                cliente.Anotar(e.Data);
        };
        proceso.BeginErrorReadLine();

        try
        {
            await cliente.Listo.WaitAsync(espera, ct).ConfigureAwait(false);
            return cliente;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or MotorCaidoException)
        {
            var diagnostico = cliente.UltimoDiagnostico;
            await cliente.DisposeAsync().ConfigureAwait(false);
            if (e is OperationCanceledException)
                throw;
            throw new MotorCaidoException(
                e is TimeoutException ? T("El motor de voz no ha respondido al arrancar.") : e.Message, diagnostico);
        }
    }

    /// <summary>Envía una petición y espera su resultado. Si se cancela, el motor se detiene (no se puede interrumpir a medias).</summary>
    public async Task<JsonElement> PedirAsync(string op, object? parametros = null, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default)
    {
        if (_caido)
            throw new MotorCaidoException(T("El motor de voz se ha detenido."), UltimoDiagnostico);
        var id = Guid.NewGuid().ToString("N")[..12];
        var pendiente = new Pendiente(new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), progreso);
        _pendientes[id] = pendiente;

        var peticion = new JsonObject
        {
            ["id"] = id,
            ["op"] = op,
            ["params"] = parametros is null ? new JsonObject() : JsonSerializer.SerializeToNode(parametros, parametros.GetType(), Json),
        };
        await _escritura.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _haciaMotor.WriteLineAsync(peticion.ToJsonString()).ConfigureAwait(false);
            await _haciaMotor.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            _pendientes.TryRemove(id, out _);
            throw new MotorCaidoException(T("El motor de voz se ha detenido."), UltimoDiagnostico + Environment.NewLine + e.Message);
        }
        finally
        {
            _escritura.Release();
        }

        using var registro = ct.Register(() =>
        {
            if (_pendientes.TryRemove(id, out var p))
            {
                p.Fin.TrySetCanceled(ct);
                Detener();
            }
        });
        return await pendiente.Fin.Task.ConfigureAwait(false);
    }

    /// <summary>Atajo que deserializa el resultado.</summary>
    public async Task<T> PedirAsync<T>(string op, object? parametros = null, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default)
    {
        var datos = await PedirAsync(op, parametros, progreso, ct).ConfigureAwait(false);
        return datos.Deserialize<T>(Json) ?? throw new MotorException("respuesta_vacia", Idioma.T("El motor no devolvió datos para «{0}».", op));
    }

    private async Task LeerAsync()
    {
        try
        {
            string? linea;
            while ((linea = await _desdeMotor.ReadLineAsync().ConfigureAwait(false)) is not null)
                Procesar(linea);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        Caer();
    }

    private void Procesar(string linea)
    {
        JsonElement mensaje;
        try
        {
            mensaje = JsonDocument.Parse(linea).RootElement.Clone();
        }
        catch (JsonException)
        {
            // stdout es solo del protocolo; si llega otra cosa, se guarda como diagnóstico.
            Anotar(linea);
            return;
        }
        var tipo = mensaje.TryGetProperty("tipo", out var t) ? t.GetString() : null;
        var id = mensaje.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;

        if (tipo == "listo")
        {
            _listo.TrySetResult(JsonNode.Parse(mensaje.GetProperty("datos").GetRawText())!.AsObject());
            return;
        }
        if (id is null || !_pendientes.TryGetValue(id, out var pendiente))
        {
            if (tipo == "error")
                Anotar($"Error sin petición: {Texto(mensaje, "mensaje")}");
            return;
        }
        switch (tipo)
        {
            case "progreso":
                double? valor = mensaje.TryGetProperty("valor", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                pendiente.Progreso?.Report(new EventoMotor(valor, Texto(mensaje, "mensaje") ?? ""));
                break;
            case "resultado":
                if (_pendientes.TryRemove(id, out _))
                    pendiente.Fin.TrySetResult(mensaje.TryGetProperty("datos", out var d) ? d : default);
                break;
            case "error":
                if (_pendientes.TryRemove(id, out _))
                {
                    pendiente.Fin.TrySetException(new MotorException(
                        Texto(mensaje, "codigo") ?? "desconocido", Texto(mensaje, "mensaje") ?? T("Error del motor."), Texto(mensaje, "detalle")));
                }
                break;
        }
    }

    private static string? Texto(JsonElement e, string propiedad) =>
        e.TryGetProperty(propiedad, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private void Anotar(string linea)
    {
        lock (_diagnostico)
        {
            _diagnostico.Enqueue(linea);
            while (_diagnostico.Count > LineasDiagnostico)
                _diagnostico.Dequeue();
        }
        Diagnostico?.Invoke(linea);
    }

    private void Caer()
    {
        _caido = true;
        var diagnostico = UltimoDiagnostico;
        var error = new MotorCaidoException(T("El motor de voz se ha detenido de forma inesperada."), diagnostico);
        _listo.TrySetException(error);
        foreach (var id in _pendientes.Keys)
        {
            if (_pendientes.TryRemove(id, out var p))
                p.Fin.TrySetException(error);
        }
    }

    private void Detener()
    {
        try
        {
            if (_proceso is { HasExited: false })
                _proceso.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_proceso is not null && Vivo)
        {
            try
            {
                // Despedida ordenada; si no responde en unos segundos, se fuerza.
                using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _haciaMotor.WriteLineAsync("""{"id":"fin","op":"salir"}""").ConfigureAwait(false);
                await _haciaMotor.FlushAsync(limite.Token).ConfigureAwait(false);
                await _proceso.WaitForExitAsync(limite.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        Detener();
        if (_proceso is null)
            _haciaMotor.Dispose();
        await _lector.ConfigureAwait(false);
        _proceso?.Dispose();
        _escritura.Dispose();
    }
}
