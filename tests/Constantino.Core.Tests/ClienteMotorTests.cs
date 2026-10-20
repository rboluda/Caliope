using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Constantino.Core.Motores;

namespace Constantino.Core.Tests;

/// <summary>El cliente contra un motor simulado en memoria (dos tuberías anónimas).</summary>
public sealed class ClienteMotorTests : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _haciaMotor = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _desdeMotor = new(PipeDirection.Out);
    private readonly StreamReader _motorLee;
    private readonly StreamWriter _motorEscribe;
    private readonly ClienteMotor _cliente;

    public ClienteMotorTests()
    {
        _motorLee = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _haciaMotor.ClientSafePipeHandle), Encoding.UTF8);
        _motorEscribe = new StreamWriter(_desdeMotor, new UTF8Encoding(false)) { AutoFlush = true };
        var clienteLee = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _desdeMotor.ClientSafePipeHandle), Encoding.UTF8);
        _cliente = new ClienteMotor(new StreamWriter(_haciaMotor, new UTF8Encoding(false)), clienteLee);
    }

    public async ValueTask DisposeAsync()
    {
        _motorEscribe.Dispose();
        await _cliente.DisposeAsync();
    }

    private async Task<JsonObject> LeerPeticionAsync() =>
        JsonNode.Parse((await _motorLee.ReadLineAsync())!)!.AsObject();

    [Fact]
    public async Task Recibe_listo_progreso_y_resultado()
    {
        await _motorEscribe.WriteLineAsync("""{"id": null, "tipo": "listo", "datos": {"motor": "falso"}}""");
        Assert.Equal("falso", (string?)(await _cliente.Listo)["motor"]);

        var avisos = new List<EventoMotor>();
        var tarea = _cliente.PedirAsync<ResultadoSintesis>("sintetizar",
            new ParametrosSintesis("hola", "Spanish", "ref.wav", "t", "s.wav", MaxTokens: 100),
            new Constantino.Core.Instalaciones.ProgresoSincrono<EventoMotor>(avisos.Add));

        var peticion = await LeerPeticionAsync();
        Assert.Equal("sintetizar", (string?)peticion["op"]);
        Assert.Equal("ref.wav", (string?)peticion["params"]!["referencia_wav"]);
        Assert.Equal(100, (int?)peticion["params"]!["max_tokens"]);
        Assert.Null(peticion["params"]!["temperatura"]);

        var id = (string?)peticion["id"];
        await _motorEscribe.WriteLineAsync($$"""{"id": "{{id}}", "tipo": "progreso", "valor": null, "mensaje": "Generando"}""");
        await _motorEscribe.WriteLineAsync("aviso suelto que no es JSON");
        await _motorEscribe.WriteLineAsync($$$"""{"id": "{{{id}}}", "tipo": "resultado", "datos": {"salida": "s.wav", "duracion": 1.5, "segundos": 2.0, "frecuencia": 24000, "vram_pico_gib": 4.6}}""");

        var r = await tarea;
        Assert.Equal(1.5, r.Duracion);
        Assert.Equal(4.6, r.VramPicoGib);
        Assert.Equal("Generando", Assert.Single(avisos).Mensaje);
        Assert.Contains("aviso suelto", _cliente.UltimoDiagnostico);
    }

    [Fact]
    public async Task Un_error_del_motor_llega_como_excepcion_con_codigo()
    {
        var tarea = _cliente.PedirAsync("cargar", new ParametrosCarga("Qwen/x", "abc"));
        var id = (string?)(await LeerPeticionAsync())["id"];
        await _motorEscribe.WriteLineAsync($$"""{"id": "{{id}}", "tipo": "error", "codigo": "sin_memoria", "mensaje": "CUDA out of memory", "detalle": "tb"}""");

        var e = await Assert.ThrowsAsync<MotorException>(() => tarea);
        Assert.Equal("sin_memoria", e.Codigo);
        Assert.Equal("tb", e.Detalle);
    }

    [Fact]
    public async Task Si_el_motor_muere_las_peticiones_pendientes_fallan()
    {
        var tarea = _cliente.PedirAsync("info");
        await LeerPeticionAsync();
        await _motorEscribe.WriteLineAsync("Traceback: algo explotó");
        _motorEscribe.Dispose();

        var e = await Assert.ThrowsAsync<MotorCaidoException>(() => tarea);
        Assert.Contains("algo explotó", e.Diagnostico);
        Assert.False(_cliente.Vivo);
        await Assert.ThrowsAsync<MotorCaidoException>(() => _cliente.PedirAsync("info"));
    }

    [Fact]
    public async Task Las_respuestas_se_asocian_por_id_aunque_lleguen_desordenadas()
    {
        var a = _cliente.PedirAsync("info");
        var idA = (string?)(await LeerPeticionAsync())["id"];
        var b = _cliente.PedirAsync("info");
        var idB = (string?)(await LeerPeticionAsync())["id"];

        await _motorEscribe.WriteLineAsync($$$"""{"id": "{{{idB}}}", "tipo": "resultado", "datos": {"n": 2}}""");
        await _motorEscribe.WriteLineAsync($$$"""{"id": "{{{idA}}}", "tipo": "resultado", "datos": {"n": 1}}""");

        Assert.Equal(1, (await a).GetProperty("n").GetInt32());
        Assert.Equal(2, (await b).GetProperty("n").GetInt32());
    }
}

/// <summary>
/// El protocolo de punta a punta contra el motor Python real en modo falso (sin torch).
/// Si no hay Python 3.11 en el equipo, la prueba no hace nada.
/// </summary>
public sealed class MotorPythonTests
{
    private static string? Python()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("py", "-3.11 -c \"import sys; print(sys.executable)\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null)
                return null;
            var ruta = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && File.Exists(ruta) ? ruta : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string CarpetaMotores()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "motores", "constantino_motor")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "motores");
    }

    [Fact]
    public async Task Sintetiza_con_el_motor_falso_y_sigue_vivo_tras_un_error()
    {
        if (Python() is not { } python)
        {
            // Con CONSTANTINO_EXIGIR_PYTHON=1 la falta de Python es un fallo, no un «no aplica».
            Assert.True(Environment.GetEnvironmentVariable("CONSTANTINO_EXIGIR_PYTHON") != "1", "No se encuentra Python 3.11 (py -3.11).");
            return;
        }
        var temporal = Path.Combine(Path.GetTempPath(), "constantino-motor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporal);
        var referencia = Path.Combine(temporal, "ref.wav");
        await File.WriteAllBytesAsync(referencia, [1, 2, 3]);

        await using var cliente = await ClienteMotor.ArrancarAsync(python, CarpetaMotores(),
            new Dictionary<string, string> { ["CONSTANTINO_MOTOR_FALSO"] = "1", ["PYTHONUTF8"] = "1" },
            TimeSpan.FromSeconds(30), CancellationToken.None);
        var motor = new MotorQwen(cliente);
        Assert.Equal("falso", (string?)(await cliente.Listo)["motor"]);

        var sinModelo = await Assert.ThrowsAsync<MotorException>(() =>
            motor.SintetizarAsync(new ParametrosSintesis("hola", "Spanish", referencia, "t", Path.Combine(temporal, "a.wav"))));
        Assert.Equal("sin_modelo", sinModelo.Codigo);

        await motor.CargarAsync("Qwen/x", "abc");
        var r = await motor.SintetizarAsync(new ParametrosSintesis("Sábado por la noche. Diluvia.", "Spanish", referencia, "t",
            Path.Combine(temporal, "salida.wav")));
        Assert.True(File.Exists(r.Salida));
        Assert.True(r.Duracion > 1);

        var fallo = await Assert.ThrowsAsync<MotorException>(() =>
            motor.SintetizarAsync(new ParametrosSintesis("falla", "Spanish", referencia, "t", Path.Combine(temporal, "b.wav"))));
        Assert.Equal("desconocido", fallo.Codigo);
        Assert.Contains("fallo provocado", fallo.Message);

        var disenada = await motor.DisenarAsync(new ParametrosDiseno("Hola, ¿qué tal?", "Spanish", "A deep warm voice.",
            Path.Combine(temporal, "disenada.wav"), Semilla: 42));
        Assert.True(File.Exists(disenada.Salida));
        var sinDescripcion = await Assert.ThrowsAsync<MotorException>(() =>
            motor.DisenarAsync(new ParametrosDiseno("Hola", "Spanish", " ", Path.Combine(temporal, "c.wav"))));
        Assert.Equal("sin_descripcion", sinDescripcion.Codigo);

        Assert.Equal("cpu", (await motor.InfoAsync()).Dispositivo);
        Assert.Contains("no debe romper el protocolo", cliente.UltimoDiagnostico);
        Directory.Delete(temporal, recursive: true);
    }
}
