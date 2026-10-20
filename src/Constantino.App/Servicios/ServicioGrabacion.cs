using System.Diagnostics;
using System.Windows;
using Constantino.Core.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;

namespace Constantino.App.Servicios;

/// <summary>Graba del micrófono (mono, 48 kHz) y da el nivel en directo para el medidor.</summary>
public sealed partial class ServicioGrabacion : ObservableObject, IDisposable
{
    public const int Frecuencia = 48000;
    public static readonly TimeSpan Maximo = TimeSpan.FromSeconds(90);

    private readonly object _cerrojo = new();
    private readonly Stopwatch _reloj = new();
    private WaveInEvent? _entrada;
    private List<float> _muestras = [];
    private TaskCompletionSource? _parada;

    [ObservableProperty] public partial bool Grabando { get; set; }

    /// <summary>Pico reciente, de 0 a 1.</summary>
    [ObservableProperty] public partial double Nivel { get; set; }

    [ObservableProperty] public partial double Segundos { get; set; }

    public static IReadOnlyList<string> Dispositivos() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName).ToList();

    public void Empezar(int dispositivo)
    {
        if (Grabando)
            return;
        lock (_cerrojo)
            _muestras = new List<float>(Frecuencia * 30);
        var despachador = Application.Current.Dispatcher;
        _parada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _entrada = new WaveInEvent
        {
            DeviceNumber = dispositivo,
            WaveFormat = new WaveFormat(Frecuencia, 16, 1),
            BufferMilliseconds = 50,
        };
        _entrada.DataAvailable += (_, e) =>
        {
            float pico = 0;
            lock (_cerrojo)
            {
                for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    var muestra = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                    _muestras.Add(muestra);
                    pico = Math.Max(pico, Math.Abs(muestra));
                }
            }
            var transcurrido = _reloj.Elapsed;
            despachador.BeginInvoke(() =>
            {
                // El medidor cae despacio para que se pueda leer.
                Nivel = Math.Max(pico, Nivel * 0.85);
                Segundos = transcurrido.TotalSeconds;
            });
            if (transcurrido > Maximo)
                _entrada?.StopRecording();
        };
        _entrada.RecordingStopped += (_, _) => _parada?.TrySetResult();
        _entrada.StartRecording();
        _reloj.Restart();
        Grabando = true;
    }

    /// <summary>Para y devuelve lo grabado.</summary>
    public async Task<Pcm> DetenerAsync()
    {
        if (_entrada is null)
            return new Pcm([], Frecuencia);
        _entrada.StopRecording();
        if (_parada is not null)
            await _parada.Task.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
        _entrada.Dispose();
        _entrada = null;
        _reloj.Stop();
        Grabando = false;
        Nivel = 0;
        lock (_cerrojo)
            return new Pcm(_muestras.ToArray(), Frecuencia);
    }

    public void Dispose() => _entrada?.Dispose();
}
