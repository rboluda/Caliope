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

/// <param name="PausaMinima">Silencio mínimo, en segundos, que separa dos frases.</param>
/// <param name="Maximo">Las frases más largas (en segundos) se parten por su punto más silencioso.</param>
/// <param name="Cortes">Instantes (s) en los que se corta aunque no haya pausa: donde cambia la persona que habla.</param>
/// <param name="UnirCortos">Actas: los trozos de menos de 1 s se unen al vecino y se quitan las frases que Whisper inventa.</param>
public sealed record ParametrosTramos(string Audio, string Idioma, double? PausaMinima = null, double? Maximo = null,
    IReadOnlyList<double>? Cortes = null, bool UnirCortos = false);

/// <param name="Personas">Cuántas personas hablan, si se sabe; null: lo decide el motor.</param>
public sealed record ParametrosHablantesTramos(string Audio, IReadOnlyList<TramoTranscrito> Tramos, int? Personas = null);

/// <param name="Hablantes">La persona de cada tramo, en el mismo orden (numeradas desde 1 por orden de aparición).</param>
public sealed record ResultadoHablantesTramos(int Personas, IReadOnlyList<int> Hablantes, double Segundos);

/// <param name="MaxHablantes">Como mucho, cuántas personas se buscan.</param>
/// <param name="Umbral">Dos grupos de voz cuyas huellas se parecen más que esto son la misma persona.</param>
public sealed record ParametrosHablantes(string Audio, int MaxHablantes = 4, double Umbral = 0.87);

/// <summary>Un rato en que habla una misma persona (numeradas desde 1 por orden de aparición).</summary>
public sealed record TurnoHablante(double Inicio, double Fin, int Hablante);

public sealed record ResultadoHablantes(int Hablantes, IReadOnlyList<TurnoHablante> Turnos, double Segundos);

public sealed record TramoTranscrito(double Inicio, double Fin, string Texto);

public sealed record ResultadoTramos(IReadOnlyList<TramoTranscrito> Frases, double Duracion, double Segundos);

/// <param name="ReferenciaPronunciacion">Voz nativa del idioma de destino: de ella sale la pronunciación.</param>
/// <param name="ReferenciaTimbre">Voz original: de ella solo se toma el timbre.</param>
public sealed record ParametrosDoblaje(string Texto, string ReferenciaPronunciacion, string ReferenciaTimbre, string Salida,
    int Semilla = 1234, double? Intensidad = null);

public sealed record ParametrosSeparacion(string Entrada, string Voces, string Fondo);

public sealed record ResultadoSeparacion(string Voces, string Fondo, int Frecuencia, double Duracion, double Segundos);

public sealed record ResultadoSintesis(string Salida, double Duracion, double Segundos, int Frecuencia, double? VramPicoGib = null);

/// <summary>Convertir la grabación <paramref name="Entrada"/> de una voz en susurro (WORLD) y escribirlo en <paramref name="Salida"/>.</summary>
public sealed record ParametrosSusurro(string Entrada, string Salida);

public sealed record ResultadoRetoque(string Salida, double Duracion);

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

    /// <summary>Necesita Whisper cargado: corta la voz por sus pausas y transcribe cada trozo.</summary>
    public Task<ResultadoTramos> TranscribirTramosAsync(ParametrosTramos parametros, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoTramos>("transcribir_tramos", parametros, progreso, ct);

    /// <summary>Solo el motor castellano: pronunciación de una voz nativa y timbre de otra (doblaje).</summary>
    public Task<ResultadoSintesis> SintetizarDoblajeAsync(ParametrosDoblaje parametros, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoSintesis>("sintetizar_doblaje", parametros, progreso, ct);

    /// <summary>Solo el motor castellano: quién habla en cada momento, por la huella de voz de su codificador.</summary>
    public Task<ResultadoHablantes> DetectarHablantesAsync(ParametrosHablantes parametros, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoHablantes>("hablantes", parametros, progreso, ct);

    /// <summary>Necesita un modelo Base cargado: quién dice cada tramo de una reunión, por su huella de voz.</summary>
    public Task<ResultadoHablantesTramos> HablantesTramosAsync(ParametrosHablantesTramos parametros, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoHablantesTramos>("hablantes_tramos", parametros, progreso, ct);

    /// <summary>Solo el separador de voz: divide un audio en la voz y el fondo.</summary>
    public Task<ResultadoSeparacion> SepararAsync(ParametrosSeparacion parametros, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoSeparacion>("separar", parametros, progreso, ct);

    /// <summary>Solo el motor principal; no necesita modelo cargado.</summary>
    public Task<ResultadoRetoque> SusurrarAsync(ParametrosSusurro parametros, IProgress<EventoMotor>? progreso = null,
        CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoRetoque>("susurrar", parametros, progreso, ct);

    /// <summary>Necesita el modelo VoiceDesign cargado.</summary>
    public Task<ResultadoSintesis> DisenarAsync(ParametrosDiseno parametros, IProgress<EventoMotor>? progreso = null, CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoSintesis>("disenar", parametros, progreso, ct);
}
