using System.IO;
using Constantino.Core;
using Constantino.Core.Catalogo;
using Constantino.Core.Motores;
using Constantino.Core.Voces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Constantino.App.Servicios;

public enum EstadoMotor
{
    Apagado,
    Arrancando,
    CargandoModelo,
    Listo,
    Trabajando,
    Error,
}

/// <summary>Falta algo por instalar para poder generar voz.</summary>
public sealed class MotorNoDisponibleException(string mensaje) : Exception(mensaje);

/// <summary>
/// El motor Qwen3-TTS como servicio: se arranca la primera vez que hace falta, carga el modelo Base
/// una sola vez y lo mantiene en memoria hasta que se apaga (o se cierra la aplicación).
/// Atiende una petición a la vez.
/// </summary>
public sealed partial class ServicioMotor(ServicioInstalacion instalacion) : ObservableObject, IAsyncDisposable
{
    private readonly SemaphoreSlim _turno = new(1, 1);
    private MotorQwen? _motor;
    private string? _modeloCargado;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Encendido))]
    public partial EstadoMotor Estado { get; set; } = EstadoMotor.Apagado;

    [ObservableProperty] public partial string Descripcion { get; set; } = "Apagado. Se encenderá solo cuando haga falta.";

    public bool Encendido => Estado is not (EstadoMotor.Apagado or EstadoMotor.Error);

    /// <summary>Lee <paramref name="texto"/> con la voz indicada y deja el WAV en <paramref name="salida"/>.</summary>
    public async Task<ResultadoSintesis> SintetizarAsync(Voz voz, string referenciaWav, string texto, string salida,
        int semilla = 1234, double? temperatura = null, CancellationToken ct = default)
    {
        await _turno.WaitAsync(ct);
        try
        {
            var motor = await PrepararAsync(ct);
            Estado = EstadoMotor.Trabajando;
            Descripcion = "Generando la voz…";
            var progreso = new Progress<EventoMotor>(e => Descripcion = e.Mensaje + "…");
            var r = await motor.SintetizarAsync(
                new ParametrosSintesis(texto, voz.Idioma, referenciaWav, voz.Transcripcion, salida, semilla, Temperatura: temperatura),
                progreso, ct);
            await MarcarListoAsync(motor);
            return r;
        }
        catch (OperationCanceledException)
        {
            // Cancelar a mitad detiene el proceso del motor: se deja apagado y la próxima petición lo arranca.
            await CerrarAsync();
            Estado = EstadoMotor.Apagado;
            Descripcion = "Detenido. Se encenderá solo cuando haga falta.";
            throw;
        }
        catch (Exception e)
        {
            await GestionarFalloAsync(e);
            throw;
        }
        finally
        {
            _turno.Release();
        }
    }

    /// <summary>El mejor modelo Base instalado (componente y revisión), o null si no hay ninguno.</summary>
    public async Task<(string Componente, string Revision)?> ModeloBaseAsync()
    {
        var instalador = await instalacion.InstaladorAsync();
        var id = new[] { "modelo-qwen-base-1.7b", "modelo-qwen-base-0.6b" }.FirstOrDefault(instalador.EstaInstalado);
        if (id is null)
            return null;
        return (id, instalacion.Catalogo[id].Instalacion!.Pasos.OfType<PasoModelo>().First().Revision);
    }

    /// <summary>Libera la memoria de la tarjeta cerrando el motor.</summary>
    public async Task ApagarAsync()
    {
        await _turno.WaitAsync();
        try
        {
            await CerrarAsync();
            Estado = EstadoMotor.Apagado;
            Descripcion = "Apagado. Se encenderá solo cuando haga falta.";
        }
        finally
        {
            _turno.Release();
        }
    }

    private async Task<MotorQwen> PrepararAsync(CancellationToken ct)
    {
        var instalador = await instalacion.InstaladorAsync();
        if (!instalador.EstaInstalado("motor-qwen"))
            throw new MotorNoDisponibleException("Falta el motor de voz Qwen3-TTS: instálalo desde Componentes.");
        // Se usa el mejor modelo Base instalado.
        var modelo = new[] { "modelo-qwen-base-1.7b", "modelo-qwen-base-0.6b" }.FirstOrDefault(instalador.EstaInstalado)
                     ?? throw new MotorNoDisponibleException("Falta un modelo Base: instala uno desde Componentes.");

        if (_motor is null || !_motor.Cliente.Vivo)
        {
            await CerrarAsync();
            Estado = EstadoMotor.Arrancando;
            Descripcion = "Arrancando el motor de voz…";
            var rutas = instalador.Rutas;
            var entorno = new Dictionary<string, string>(rutas.EntornoProcesos) { ["HF_HUB_OFFLINE"] = "1" };
            var cliente = await ClienteMotor.ArrancarAsync(
                Path.Combine(rutas.Entornos, "qwen3tts", "Scripts", "python.exe"),
                Path.Combine(AppContext.BaseDirectory, "motores"),
                entorno, TimeSpan.FromMinutes(2), ct);
            _motor = new MotorQwen(cliente);
            _modeloCargado = null;
        }

        if (_modeloCargado != modelo)
        {
            Estado = EstadoMotor.CargandoModelo;
            Descripcion = "Cargando el modelo de voz (unos segundos la primera vez)…";
            var paso = instalacion.Catalogo[modelo].Instalacion!.Pasos.OfType<PasoModelo>().First();
            await _motor.CargarAsync(paso.Repositorio, paso.Revision, null, ct);
            _modeloCargado = modelo;
        }
        return _motor;
    }

    private async Task MarcarListoAsync(MotorQwen motor)
    {
        Estado = EstadoMotor.Listo;
        try
        {
            var info = await motor.InfoAsync();
            Descripcion = info.Gpu is null
                ? "Listo (generando con el procesador)."
                : $"Listo en {info.Gpu} · {Formato.Gb((info.VramTotalGib ?? 0) - (info.VramLibreGib ?? 0))} de {Formato.Gb(info.VramTotalGib ?? 0)} ocupados.";
        }
        catch (Exception e) when (e is MotorException or MotorCaidoException)
        {
            Descripcion = "Listo.";
        }
    }

    private async Task GestionarFalloAsync(Exception e)
    {
        Estado = EstadoMotor.Error;
        Descripcion = Explicar(e);
        // Si el proceso murió, se cierra del todo: la próxima petición lo arranca de nuevo.
        if (_motor is { Cliente.Vivo: false })
            await CerrarAsync();
    }

    /// <summary>Texto para el usuario a partir de un fallo del motor.</summary>
    public static string Explicar(Exception e) => e switch
    {
        MotorNoDisponibleException => e.Message,
        MotorException { Codigo: "sin_memoria" } =>
            "No hay memoria suficiente en la tarjeta gráfica. Cierra navegadores y otros programas que la usen y vuelve a intentarlo.",
        MotorException { Codigo: "modelo_no_encontrado" } => "El modelo no está bien instalado: repáralo desde Componentes.",
        MotorException { Codigo: "archivo_no_encontrado" } => "No se encuentra la grabación de referencia de la voz.",
        MotorException m => $"El motor ha fallado: {m.Message}",
        MotorCaidoException c => "El motor de voz se ha cerrado inesperadamente." +
                                 (string.IsNullOrWhiteSpace(c.Diagnostico) ? "" : $" Últimos mensajes:\n{UltimasLineas(c.Diagnostico, 6)}"),
        _ => e.Message,
    };

    private static string UltimasLineas(string texto, int n) =>
        string.Join("\n", texto.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(n));

    private async Task CerrarAsync()
    {
        if (_motor is not null)
        {
            await _motor.Cliente.DisposeAsync();
            _motor = null;
            _modeloCargado = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CerrarAsync();
        _turno.Dispose();
    }
}
