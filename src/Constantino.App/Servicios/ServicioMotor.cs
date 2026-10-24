using System.IO;
using Constantino.Core;
using Constantino.Core.Catalogo;
using Constantino.Core.Motores;
using Constantino.Core.Voces;
using CommunityToolkit.Mvvm.ComponentModel;
using static Constantino.Core.Idioma;

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
/// Los motores de voz como servicio: se arranca el que hace falta la primera vez, carga el modelo una sola vez
/// y lo mantiene en memoria hasta que se apaga (o se cierra la aplicación). Solo hay un motor vivo a la vez
/// (Qwen3-TTS, Chatterbox o VoxCPM2, cada uno con el Python de su entorno): cambiar de uno a otro cierra el anterior para
/// liberar la tarjeta. Atiende una petición a la vez.
/// </summary>
public sealed partial class ServicioMotor(ServicioInstalacion instalacion) : ObservableObject, IAsyncDisposable
{
    private readonly SemaphoreSlim _turno = new(1, 1);
    private MotorQwen? _motor;
    private string? _motorVivo;
    private string? _modeloCargado;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Encendido))]
    public partial EstadoMotor Estado { get; set; } = EstadoMotor.Apagado;

    [ObservableProperty] public partial string Descripcion { get; set; } = T("Apagado. Se encenderá solo cuando haga falta.");

    public bool Encendido => Estado is not (EstadoMotor.Apagado or EstadoMotor.Error);

    public const string ModeloVoiceDesign = "modelo-qwen-voicedesign";
    public const string ModeloWhisper = "whisper";
    private static readonly string[] ModelosBase = ["modelo-qwen-base-1.7b", "modelo-qwen-base-0.6b"];

    /// <summary>Lee <paramref name="texto"/> con la voz indicada y deja el WAV en <paramref name="salida"/>.</summary>
    /// <param name="motor">Uno de <see cref="MotoresDeVoz"/>.</param>
    /// <param name="intensidad">Solo Chatterbox: intensidad emocional.</param>
    public async Task<ResultadoSintesis> SintetizarAsync(Voz voz, string referenciaWav, string texto, string salida,
        int semilla = 1234, double? temperatura = null, CancellationToken ct = default,
        string motor = MotoresDeVoz.Qwen, double? intensidad = null)
    {
        var modelo = await ModeloDeVozAsync(motor);
        return await EjecutarAsync(modelo, (m, progreso) => m.SintetizarAsync(
            new ParametrosSintesis(texto, voz.Idioma, referenciaWav, voz.Transcripcion, salida, semilla, Temperatura: temperatura,
                Intensidad: MotoresDeVoz.TieneIntensidad(motor) ? intensidad : null),
            progreso, ct), ct);
    }

    /// <summary>El componente con el modelo que lee voces con <paramref name="motor"/>, o una explicación de qué falta.</summary>
    private async Task<string> ModeloDeVozAsync(string motor)
    {
        if (motor != MotoresDeVoz.Qwen)
            return await InstaladoAsync(motor)
                ? MotoresDeVoz.Componente(motor)
                : throw new MotorNoDisponibleException(
                    T("Falta el motor «{0}»: instálalo desde Componentes o elige el principal.", MotoresDeVoz.NombreVisible(motor)));
        return (await ModeloBaseAsync())?.Componente
               ?? throw new MotorNoDisponibleException(T("Falta una voz: instala la «Voz de alta calidad» (o la ligera) desde Componentes."));
    }

    /// <summary>Si está instalado <paramref name="motor"/> (el principal cuenta solo con una voz instalada).</summary>
    public async Task<bool> InstaladoAsync(string motor) => motor == MotoresDeVoz.Qwen
        ? await ModeloBaseAsync() is not null
        : (await instalacion.InstaladorAsync()).EstaInstalado(MotoresDeVoz.Componente(motor));

    /// <summary>Los motores que se pueden usar ya, del principal a los opcionales.</summary>
    public async Task<IReadOnlyList<string>> MotoresInstaladosAsync()
    {
        var instalador = await instalacion.InstaladorAsync();
        return MotoresDeVoz.Todos.Where(m => m == MotoresDeVoz.Qwen || instalador.EstaInstalado(MotoresDeVoz.Componente(m))).ToList();
    }

    /// <summary>Lo que identifica al modelo de <paramref name="motor"/> en las huellas, o null si no está instalado.</summary>
    public async Task<string?> FirmaModeloAsync(string motor)
    {
        if (motor != MotoresDeVoz.Qwen)
        {
            if (!await InstaladoAsync(motor))
                return null;
            // Todas las revisiones: si cambia cualquier pieza (Chatterbox v3 añade el paquete de castellano), cambia la voz.
            var revisiones = instalacion.Catalogo[MotoresDeVoz.Componente(motor)].Instalacion!.Pasos.OfType<PasoModelo>()
                .Select(p => p.Revision);
            return $"{motor}@{string.Join('+', revisiones)}";
        }
        return await ModeloBaseAsync() is { } m ? $"{m.Componente}@{m.Revision}" : null;
    }

    /// <summary>Inventa una voz a partir de una descripción y lee <paramref name="texto"/> con ella (modelo VoiceDesign).</summary>
    public async Task<ResultadoSintesis> DisenarAsync(string instruccion, string idioma, string texto, string salida,
        int semilla, double? temperatura = null, CancellationToken ct = default)
    {
        if (!(await instalacion.InstaladorAsync()).EstaInstalado(ModeloVoiceDesign))
            throw new MotorNoDisponibleException(T("Falta el diseñador de voces: instálalo desde Componentes."));
        return await EjecutarAsync(ModeloVoiceDesign, (motor, progreso) => motor.DisenarAsync(
            new ParametrosDiseno(texto, idioma, instruccion, salida, semilla, temperatura), progreso, ct), ct);
    }

    /// <summary>Escribe lo que se dice en <paramref name="audio"/> (modelo Whisper).</summary>
    public async Task<ResultadoTranscripcion> TranscribirAsync(string audio, string idioma, CancellationToken ct = default)
    {
        if (!(await instalacion.InstaladorAsync()).EstaInstalado(ModeloWhisper))
            throw new MotorNoDisponibleException(T("Falta la transcripción automática: instálala desde Componentes, o escribe el texto a mano."));
        return await EjecutarAsync(ModeloWhisper, (motor, progreso) =>
            motor.TranscribirAsync(new ParametrosTranscripcion(audio, idioma), progreso, ct), ct);
    }

    // --- Doblaje ---------------------------------------------------------------------------------

    public const string ComponenteSeparador = "separador-voz";

    /// <summary>Divide <paramref name="audio"/> en la voz y el fondo (separador de voz).</summary>
    public Task<ResultadoSeparacion> SepararAsync(string audio, string voces, string fondo, CancellationToken ct = default) =>
        EjecutarAsync(ComponenteSeparador, (motor, progreso) =>
            motor.SepararAsync(new ParametrosSeparacion(audio, voces, fondo), progreso, ct), ct);

    /// <summary>Frases con su inicio y fin: corta la voz por sus pausas y transcribe cada trozo (Whisper).</summary>
    public Task<ResultadoTramos> TranscribirTramosAsync(string audio, string idioma, CancellationToken ct = default) =>
        EjecutarAsync(ModeloWhisper, (motor, progreso) =>
            motor.TranscribirTramosAsync(new ParametrosTramos(audio, idioma), progreso, ct), ct);

    /// <summary>Una frase doblada: pronunciación de la voz nativa y timbre de la original (motor castellano).</summary>
    public Task<ResultadoSintesis> SintetizarDoblajeAsync(string texto, string pronunciacion, string timbre, string salida, int semilla,
        CancellationToken ct = default) =>
        EjecutarAsync(MotoresDeVoz.Componente(MotoresDeVoz.Chatterbox), (motor, progreso) =>
            motor.SintetizarDoblajeAsync(new ParametrosDoblaje(texto, pronunciacion, timbre, salida, semilla), progreso, ct), ct);

    /// <summary>Una operación a la vez, con el modelo que necesite ya cargado.</summary>
    private async Task<TResultado> EjecutarAsync<TResultado>(string modelo, Func<MotorQwen, IProgress<EventoMotor>, Task<TResultado>> trabajo, CancellationToken ct)
    {
        await _turno.WaitAsync(ct);
        try
        {
            var motor = await PrepararAsync(modelo, ct);
            Estado = EstadoMotor.Trabajando;
            Descripcion = T("Generando la voz…");
            var progreso = new Progress<EventoMotor>(e => Descripcion = e.Mensaje + "…");
            var r = await trabajo(motor, progreso);
            await MarcarListoAsync(motor);
            return r;
        }
        catch (OperationCanceledException)
        {
            // Cancelar a mitad detiene el proceso del motor: se deja apagado y la próxima petición lo arranca.
            await CerrarAsync();
            Estado = EstadoMotor.Apagado;
            Descripcion = T("Detenido. Se encenderá solo cuando haga falta.");
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
        var id = ModelosBase.FirstOrDefault(instalador.EstaInstalado);
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
            Descripcion = T("Apagado. Se encenderá solo cuando haga falta.");
        }
        finally
        {
            _turno.Release();
        }
    }

    /// <summary>
    /// Arranca el motor si hace falta y deja cargado <paramref name="modelo"/>. Solo cabe un modelo a la vez en la
    /// tarjeta: cambiar de Base a VoiceDesign (o al revés) descarga el anterior.
    /// </summary>
    private async Task<MotorQwen> PrepararAsync(string modelo, CancellationToken ct)
    {
        var instalador = await instalacion.InstaladorAsync();
        // El separador de voz es un motor más (su propio proceso y entorno), aunque no lea textos.
        var separador = modelo == ComponenteSeparador;
        var motorId = separador ? "separador" : MotoresDeVoz.DeComponente(modelo) ?? MotoresDeVoz.Qwen;
        var nombre = separador ? T("separador de voz") : MotoresDeVoz.Nombre(motorId);
        if (!instalador.EstaInstalado(separador ? modelo : MotoresDeVoz.Componente(motorId)))
            throw new MotorNoDisponibleException(T("Falta el motor {0}: instálalo desde Componentes.", nombre));

        if (_motor is null || !_motor.Cliente.Vivo || _motorVivo != motorId)
        {
            // Dos motores no caben a la vez en la tarjeta: se cierra el que hubiera.
            await CerrarAsync();
            Estado = EstadoMotor.Arrancando;
            Descripcion = T("Arrancando el motor {0}…", nombre);
            var rutas = instalador.Rutas;
            var entorno = new Dictionary<string, string>(rutas.EntornoProcesos) { ["HF_HUB_OFFLINE"] = "1" };
            // Los mensajes de error y de progreso del motor, en el idioma de la interfaz.
            entorno["CONSTANTINO_IDIOMA"] = Idioma.Actual;
            if (motorId != MotoresDeVoz.Qwen)
            {
                entorno["CONSTANTINO_MOTOR"] = motorId;
                entorno["TQDM_DISABLE"] = "1"; // sin barras de progreso en el registro de diagnóstico
            }
            var cliente = await ClienteMotor.ArrancarAsync(
                Path.Combine(rutas.Entornos, separador ? "demucs" : MotoresDeVoz.Entorno(motorId), "Scripts", "python.exe"),
                Path.Combine(AppContext.BaseDirectory, "motores"),
                entorno, TimeSpan.FromMinutes(2), ct);
            _motor = new MotorQwen(cliente);
            _motorVivo = motorId;
            _modeloCargado = null;
        }

        if (_modeloCargado != modelo)
        {
            Estado = EstadoMotor.CargandoModelo;
            Descripcion = modelo switch
            {
                ModeloVoiceDesign => T("Preparando el diseñador de voces (unos segundos)…"),
                ModeloWhisper => T("Preparando la transcripción (unos segundos)…"),
                "chatterbox" => T("Preparando el motor castellano (unos segundos)…"),
                "voxcpm2" => T("Preparando el motor de máximo parecido (medio minuto)…"),
                ComponenteSeparador => T("Preparando el separador de voz (unos segundos)…"),
                _ => T("Preparando la voz (unos segundos la primera vez)…"),
            };
            var paso = instalacion.Catalogo[modelo].Instalacion!.Pasos.OfType<PasoModelo>().First();
            await _motor.CargarAsync(paso.Repositorio, paso.Revision, null, ct, modelo == ModeloWhisper ? "asr" : "tts");
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
                ? T("Listo (generando con el procesador).")
                : T("Listo en {0} · {1} de {2} ocupados.", info.Gpu, Formato.Gb((info.VramTotalGib ?? 0) - (info.VramLibreGib ?? 0)), Formato.Gb(info.VramTotalGib ?? 0));
        }
        catch (Exception e) when (e is MotorException or MotorCaidoException)
        {
            Descripcion = T("Listo.");
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
            T("No hay memoria suficiente en la tarjeta gráfica. Cierra navegadores y otros programas que la usen y vuelve a intentarlo."),
        MotorException { Codigo: "modelo_no_encontrado" } => T("Falta algo por instalar o se ha estropeado: pulsa «Reparar» en Componentes."),
        MotorException { Codigo: "sin_descripcion" } => T("Describe la voz que quieres crear."),
        MotorException { Codigo: "idioma_no_soportado" or "no_soportado" or "instalacion_incompleta" } m => m.Message,
        MotorException { Codigo: "archivo_no_encontrado" } => T("No se encuentra la grabación de referencia de la voz."),
        MotorException m => T("El motor ha fallado: {0}", m.Message),
        MotorCaidoException c => T("El motor de voz se ha cerrado inesperadamente.") +
                                 (string.IsNullOrWhiteSpace(c.Diagnostico) ? "" : " " + T("Últimos mensajes:\n{0}", UltimasLineas(c.Diagnostico, 6))),
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
            _motorVivo = null;
            _modeloCargado = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CerrarAsync();
        _turno.Dispose();
    }
}
