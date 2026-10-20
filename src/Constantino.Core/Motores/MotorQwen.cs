namespace Constantino.Core.Motores;

public sealed record InfoMotor(string? Torch, string Dispositivo, string? Gpu = null, double? VramTotalGib = null, double? VramLibreGib = null);

/// <param name="Tipo">«tts» para los modelos de voz, «asr» para Whisper.</param>
public sealed record ParametrosCarga(string Repositorio, string Revision, string Tipo = "tts");

public sealed record ParametrosTranscripcion(string Audio, string Idioma);

public sealed record ResultadoTranscripcion(string Texto, double Segundos);

public sealed record ResultadoCarga(double Segundos, bool YaCargado, double? VramUsadaGib = null);

/// <summary>Leer <paramref name="Texto"/> con la voz de la grabación de referencia.</summary>
/// <param name="ReferenciaTexto">Transcripción de la referencia; null o vacía para imitar solo el timbre (Chatterbox no la usa).</param>
/// <param name="Intensidad">Solo Chatterbox: intensidad emocional (exaggeration).</param>
/// <param name="Cfg">Solo Chatterbox: cuánto se ciñe a la referencia (cfg_weight).</param>
public sealed record ParametrosSintesis(
    string Texto,
    string Idioma,
    string ReferenciaWav,
    string? ReferenciaTexto,
    string Salida,
    int Semilla = 1234,
    double? Temperatura = null,
    double? TopP = null,
    int? TopK = null,
    double? Repeticion = null,
    int? MaxTokens = null,
    double? Intensidad = null,
    double? Cfg = null);

/// <summary>Inventar una voz a partir de <paramref name="Instruccion"/> (VoiceDesign) y leer <paramref name="Texto"/> con ella.</summary>
public sealed record ParametrosDiseno(
    string Texto,
    string Idioma,
    string Instruccion,
    string Salida,
    int Semilla = 1234,
    double? Temperatura = null);

public sealed record ResultadoSintesis(string Salida, double Duracion, double Segundos, int Frecuencia, double? VramPicoGib = null);

/// <summary>Operaciones del motor Qwen3-TTS con tipos, sobre el protocolo de <see cref="ClienteMotor"/>.</summary>
public sealed class MotorQwen(ClienteMotor cliente)
{
    public ClienteMotor Cliente => cliente;

    public Task<InfoMotor> InfoAsync(CancellationToken ct = default) =>
        cliente.PedirAsync<InfoMotor>("info", null, null, ct);

    public Task<ResultadoCarga> CargarAsync(string repositorio, string revision, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default, string tipo = "tts") =>
        cliente.PedirAsync<ResultadoCarga>("cargar", new ParametrosCarga(repositorio, revision, tipo), progreso, ct);

    public Task DescargarAsync(CancellationToken ct = default) => cliente.PedirAsync("descargar", null, null, ct);

    public Task<ResultadoSintesis> SintetizarAsync(ParametrosSintesis parametros, IProgress<EventoMotor>? progreso = null, CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoSintesis>("sintetizar", parametros, progreso, ct);

    /// <summary>Necesita Whisper cargado (tipo «asr»).</summary>
    public Task<ResultadoTranscripcion> TranscribirAsync(ParametrosTranscripcion parametros, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoTranscripcion>("transcribir", parametros, progreso, ct);

    /// <summary>Necesita el modelo VoiceDesign cargado.</summary>
    public Task<ResultadoSintesis> DisenarAsync(ParametrosDiseno parametros, IProgress<EventoMotor>? progreso = null, CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoSintesis>("disenar", parametros, progreso, ct);
}
