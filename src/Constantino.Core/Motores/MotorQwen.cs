namespace Constantino.Core.Motores;

public sealed record InfoMotor(string? Torch, string Dispositivo, string? Gpu = null, double? VramTotalGib = null, double? VramLibreGib = null);

public sealed record ParametrosCarga(string Repositorio, string Revision);

public sealed record ResultadoCarga(double Segundos, bool YaCargado, double? VramUsadaGib = null);

/// <summary>Leer <paramref name="Texto"/> con la voz de la grabación de referencia.</summary>
/// <param name="ReferenciaTexto">Transcripción de la referencia; null o vacía para imitar solo el timbre.</param>
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
    int? MaxTokens = null);

public sealed record ResultadoSintesis(string Salida, double Duracion, double Segundos, int Frecuencia, double? VramPicoGib = null);

/// <summary>Operaciones del motor Qwen3-TTS con tipos, sobre el protocolo de <see cref="ClienteMotor"/>.</summary>
public sealed class MotorQwen(ClienteMotor cliente)
{
    public ClienteMotor Cliente => cliente;

    public Task<InfoMotor> InfoAsync(CancellationToken ct = default) =>
        cliente.PedirAsync<InfoMotor>("info", null, null, ct);

    public Task<ResultadoCarga> CargarAsync(string repositorio, string revision, IProgress<EventoMotor>? progreso = null, CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoCarga>("cargar", new ParametrosCarga(repositorio, revision), progreso, ct);

    public Task DescargarAsync(CancellationToken ct = default) => cliente.PedirAsync("descargar", null, null, ct);

    public Task<ResultadoSintesis> SintetizarAsync(ParametrosSintesis parametros, IProgress<EventoMotor>? progreso = null, CancellationToken ct = default) =>
        cliente.PedirAsync<ResultadoSintesis>("sintetizar", parametros, progreso, ct);
}
