using System.IO;
using Constantino.Core;
using Constantino.Core.Catalogo;
using Constantino.Core.Motores;

namespace Constantino.App.Servicios;

/// <summary>
/// Voz en directo (beta): su propio motor (motores/constantino_motor/endirecto.py), aparte de <see cref="ServicioMotor"/>,
/// porque el audio corre sin parar mientras se habla y no puede esperar turno ni apagarse por reposo. Usa poca memoria
/// de la tarjeta (unos 0,9 GB en el modo «calidad»; nada en el «ligero»), así que convive con los demás motores.
/// </summary>
public sealed class ServicioEnDirecto(ServicioInstalacion instalacion) : IAsyncDisposable
{
    public const string Componente = "voz-en-directo";

    private readonly SemaphoreSlim _turno = new(1, 1);
    private MotorEnDirecto? _motor;
    private string? _dispositivo;

    public async Task<bool> InstaladoAsync() => (await instalacion.InstaladorAsync()).EstaInstalado(Componente);

    /// <summary>«cuda» o «cpu», una vez arrancado el motor.</summary>
    public string? Dispositivo => _dispositivo;

    public Task<ResultadoDispositivos> DispositivosAsync(CancellationToken ct = default) =>
        ConMotorAsync(m => m.DispositivosAsync(ct), ct);

    public Task<ResultadoCarga> CargarAsync(string modo, string voz, IProgress<EventoMotor>? progreso, CancellationToken ct = default) =>
        ConMotorAsync(m => m.CargarAsync(new ParametrosEnDirecto(modo, voz, Modelos()), progreso, ct), ct);

    public Task<EstadoEnDirecto> IniciarAsync(DispositivoAudio? entrada, string? archivo, IReadOnlyList<DispositivoAudio> salidas,
        CancellationToken ct = default) =>
        ConMotorAsync(m => m.IniciarAsync(new ParametrosIniciarEnDirecto(entrada?.Indice, archivo, [.. salidas.Select(s => s.Indice)],
            entrada?.Nombre, [.. salidas.Select(s => s.Nombre)]), ct), ct);

    public async Task<EstadoEnDirecto> EstadoAsync(CancellationToken ct = default) =>
        _motor is { Cliente.Vivo: true } ? await ConMotorAsync(m => m.EstadoAsync(ct), ct) : new EstadoEnDirecto(false);

    public async Task<EstadoEnDirecto> PararAsync(CancellationToken ct = default) =>
        _motor is { Cliente.Vivo: true } ? await ConMotorAsync(m => m.PararAsync(ct), ct) : new EstadoEnDirecto(false);

    /// <summary>Cierra el motor y libera lo que tuviera cargado.</summary>
    public async Task ApagarAsync()
    {
        await _turno.WaitAsync();
        try
        {
            await CerrarAsync();
        }
        finally
        {
            _turno.Release();
        }
    }

    /// <summary>Repositorio → revisión de cada modelo, tal como los fija el catálogo.</summary>
    private Dictionary<string, string> Modelos() =>
        instalacion.Catalogo[Componente].Instalacion!.Pasos.OfType<PasoModelo>().ToDictionary(p => p.Repositorio, p => p.Revision);

    private async Task<T> ConMotorAsync<T>(Func<MotorEnDirecto, Task<T>> trabajo, CancellationToken ct)
    {
        await _turno.WaitAsync(ct);
        try
        {
            var motor = await ArrancarAsync(ct);
            return await trabajo(motor);
        }
        finally
        {
            _turno.Release();
        }
    }

    private async Task<MotorEnDirecto> ArrancarAsync(CancellationToken ct)
    {
        if (_motor is { Cliente.Vivo: true })
            return _motor;
        await CerrarAsync();
        var instalador = await instalacion.InstaladorAsync();
        if (!instalador.EstaInstalado(Componente))
            throw new MotorNoDisponibleException(Idioma.T("Falta la voz en directo: instálala desde Componentes."));
        var rutas = instalador.Rutas;
        var entorno = new Dictionary<string, string>(rutas.EntornoProcesos)
        {
            ["HF_HUB_OFFLINE"] = "1",
            ["CONSTANTINO_IDIOMA"] = Idioma.Actual,
            ["CONSTANTINO_MOTOR"] = "endirecto",
            ["TQDM_DISABLE"] = "1",
        };
        var cliente = await ClienteMotor.ArrancarAsync(Path.Combine(rutas.Entornos, "endirecto", "Scripts", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "motores"), entorno, TimeSpan.FromMinutes(2), ct);
        _motor = new MotorEnDirecto(cliente);
        try
        {
            _dispositivo = (await new MotorQwen(cliente).InfoAsync(ct)).Dispositivo;
        }
        catch (MotorException)
        {
            _dispositivo = null;
        }
        return _motor;
    }

    private async Task CerrarAsync()
    {
        if (_motor is not null)
        {
            await _motor.Cliente.DisposeAsync();
            _motor = null;
            _dispositivo = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CerrarAsync();
        _turno.Dispose();
    }
}
