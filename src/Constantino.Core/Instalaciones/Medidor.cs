using System.Diagnostics;
using System.Globalization;

namespace Constantino.Core.Instalaciones;

/// <summary>
/// Calcula velocidad y tiempo restante a partir de muestras de bytes, con una ventana deslizante
/// para que la cifra no salte con cada pico de la conexión.
/// </summary>
public sealed class Medidor
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private static readonly TimeSpan Ventana = TimeSpan.FromSeconds(10);

    private readonly Queue<(TimeSpan Momento, long Bytes)> _muestras = new();
    private readonly Stopwatch _reloj = Stopwatch.StartNew();

    public double? BytesPorSegundo { get; private set; }
    public TimeSpan? Restante { get; private set; }

    public void Anotar(long bytes, long? total) => Anotar(bytes, total, _reloj.Elapsed);

    internal void Anotar(long bytes, long? total, TimeSpan ahora)
    {
        _muestras.Enqueue((ahora, bytes));
        while (_muestras.Count > 2 && ahora - _muestras.Peek().Momento > Ventana)
            _muestras.Dequeue();

        var (inicio, bytesInicio) = _muestras.Peek();
        var segundos = (ahora - inicio).TotalSeconds;
        // Hace falta un mínimo de tiempo medido para que la velocidad tenga sentido.
        BytesPorSegundo = segundos >= 1.5 && bytes > bytesInicio ? (bytes - bytesInicio) / segundos : null;
        Restante = BytesPorSegundo is { } v && total is { } t && t > bytes ? TimeSpan.FromSeconds((t - bytes) / v) : null;
    }

    /// <summary>«Descargando X · 1,2 GB de 4,2 GB · 12 MB/s · quedan ~3 min».</summary>
    /// <param name="aproximado">El total es una estimación: se muestra con «~».</param>
    public string Texto(string descripcion, long bytes, long? total, bool aproximado = false)
    {
        var partes = new List<string> { descripcion };
        partes.Add(total is { } t and > 0 ? $"{Tamano(bytes)} de {(aproximado ? "~" : "")}{Tamano(t)}" : Tamano(bytes));
        if (BytesPorSegundo is { } v)
            partes.Add($"{Tamano((long)v)}/s");
        if (Restante is { } r)
            partes.Add(TiempoRestante(r));
        return string.Join(" · ", partes);
    }

    public static string Tamano(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("0.0", Es)} GB",
        >= 1L << 20 => $"{(bytes / (double)(1L << 20)).ToString(bytes < 10L << 20 ? "0.0" : "0", Es)} MB",
        _ => $"{bytes / 1024} KB",
    };

    public static string TiempoRestante(TimeSpan r) => r.TotalSeconds switch
    {
        < 10 => "casi listo",
        < 60 => $"quedan ~{Math.Ceiling(r.TotalSeconds / 10) * 10:0} s",
        < 3600 => $"quedan ~{Math.Ceiling(r.TotalMinutes):0} min",
        _ => $"quedan ~{(int)r.TotalHours} h {r.Minutes} min",
    };
}
