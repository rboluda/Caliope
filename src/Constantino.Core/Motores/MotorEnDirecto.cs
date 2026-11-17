namespace Constantino.Core.Motores;

/// <summary>Un micrófono o una salida de sonido, tal como los ve el motor de voz en directo (MME).</summary>
/// <param name="Cable">Es el cable virtual (VB-CABLE), con su nombre de fábrica o el que le pone Calíope.</param>
public sealed record DispositivoAudio(int Indice, string Nombre, bool Entrada, bool Salida, bool PorDefecto, bool Cable);

public sealed record ResultadoDispositivos(IReadOnlyList<DispositivoAudio> Dispositivos);

/// <param name="Modo">«calidad» (Seed-VC, tarjeta gráfica) o «ligero» (MeanVC2, procesador).</param>
/// <param name="Voz">Grabación de referencia de la voz con la que se quiere sonar.</param>
/// <param name="Modelos">Repositorio → revisión de cada modelo, tal como los fija el catálogo.</param>
public sealed record ParametrosEnDirecto(string Modo, string Voz, IReadOnlyDictionary<string, string> Modelos);

/// <param name="Entrada">Índice del micrófono; null para usar <paramref name="Archivo"/>.</param>
/// <param name="Archivo">Grabación que se convierte como si se hablara en directo (para probar sin micrófono).</param>
/// <param name="Salidas">Índices de los dispositivos por los que sale la voz convertida (uno o dos).</param>
/// <param name="EntradaNombre">Nombre del micrófono: el motor lo busca por nombre, porque los números de MME cambian
/// cuando Windows cambia el dispositivo predeterminado.</param>
/// <param name="SalidasNombres">Nombres de las salidas, en el mismo orden que <paramref name="Salidas"/>.</param>
public sealed record ParametrosIniciarEnDirecto(int? Entrada, string? Archivo, IReadOnlyList<int> Salidas,
    string? EntradaNombre = null, IReadOnlyList<string>? SalidasNombres = null);

/// <summary>Cómo va la voz en directo.</summary>
/// <param name="Lentos">Bloques que tardaron más en convertirse de lo que duran (se notan como cortes).</param>
/// <param name="Cortes">Veces que una salida se quedó sin sonido que reproducir.</param>
/// <param name="RetrasoMs">Retraso estimado entre lo que dices y lo que se oye.</param>
public sealed record EstadoEnDirecto(bool Activo, string? Error = null, bool FinArchivo = false, int Bloques = 0, int Lentos = 0,
    int Cortes = 0, double ProcesoMs = 0, double ProcesoMaxMs = 0, double BloqueMs = 0, double RetrasoMs = 0,
    double NivelEntradaDb = -90, double NivelSalidaDb = -90, double? VramUsadaGib = null);

/// <summary>Las operaciones del motor de voz en directo (motores/constantino_motor/endirecto.py).</summary>
public sealed class MotorEnDirecto(ClienteMotor cliente)
{
    public ClienteMotor Cliente { get; } = cliente;

    public Task<ResultadoDispositivos> DispositivosAsync(CancellationToken ct = default) =>
        Cliente.PedirAsync<ResultadoDispositivos>("dispositivos", null, null, ct);

    public Task<ResultadoCarga> CargarAsync(ParametrosEnDirecto parametros, IProgress<EventoMotor>? progreso = null, CancellationToken ct = default) =>
        Cliente.PedirAsync<ResultadoCarga>("cargar", parametros, progreso, ct);

    public Task<EstadoEnDirecto> IniciarAsync(ParametrosIniciarEnDirecto parametros, CancellationToken ct = default) =>
        Cliente.PedirAsync<EstadoEnDirecto>("iniciar", parametros, null, ct);

    public Task<EstadoEnDirecto> EstadoAsync(CancellationToken ct = default) =>
        Cliente.PedirAsync<EstadoEnDirecto>("estado", null, null, ct);

    public Task<EstadoEnDirecto> PararAsync(CancellationToken ct = default) =>
        Cliente.PedirAsync<EstadoEnDirecto>("parar", null, null, ct);
}
