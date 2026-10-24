using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>
/// La barra de una generación larga. El narrador avisa al empezar cada frase (con lo que ocupará en la barra y sus
/// letras); mientras el motor la lee, la barra avanza sola según lo que han tardado las anteriores, sin pasarse nunca
/// del final de la frase. Debajo, el porcentaje, el tiempo que lleva y lo que falta.
/// </summary>
public sealed partial class AvanceSuave : ObservableObject
{
    /// <summary>Segundos que tarda el motor por letra. Se aprende frase a frase y se recuerda durante la sesión.</summary>
    private static double _segundosPorLetra = 0.12;

    private readonly DispatcherTimer _reloj = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _total = new();
    private readonly Stopwatch _frase = new();
    private double _desde;
    private double _hasta;
    private int _letras;
    // Para estimar lo que queda: cuánto de la barra se ha generado de verdad y en cuánto tiempo (los bloques sin
    // cambios saltan de golpe y no cuentan).
    private double _fraccionGenerada;
    private double _segundosGenerando;

    public AvanceSuave() => _reloj.Tick += (_, _) => Actualizar();

    [ObservableProperty] public partial double Fraccion { get; private set; }

    /// <summary>«37 % · 1:12 · quedan unos 3 min».</summary>
    [ObservableProperty] public partial string Estado { get; private set; } = "";

    public void Empezar()
    {
        _total.Restart();
        _frase.Reset();
        _desde = _hasta = 0;
        _letras = 0;
        _fraccionGenerada = _segundosGenerando = 0;
        Fraccion = 0;
        Actualizar();
        _reloj.Start();
    }

    /// <param name="fraccion">Avance global ahora.</param>
    /// <param name="hasta">Si empieza una frase: el avance cuando termine.</param>
    /// <param name="letras">Si empieza una frase: sus letras, para estimar lo que tardará.</param>
    public void Informar(double fraccion, double? hasta = null, int letras = 0)
    {
        if (_letras > 0)
        {
            // La frase anterior ha terminado: se aprende cuánto tarda el motor.
            var segundos = _frase.Elapsed.TotalSeconds;
            _segundosPorLetra = _fraccionGenerada == 0
                ? segundos / _letras
                : 0.7 * _segundosPorLetra + 0.3 * (segundos / _letras);
            _fraccionGenerada += _hasta - _desde;
            _segundosGenerando += segundos;
        }
        if (fraccion < _desde - 0.01)
        {
            // Empieza otra fase con su propia barra (p. ej. repetir un bloque mal leído): lo medido no vale para ella.
            _fraccionGenerada = _segundosGenerando = 0;
        }
        _desde = fraccion;
        _hasta = hasta is { } h && h > fraccion ? h : fraccion;
        _letras = _hasta > _desde ? letras : 0;
        _frase.Restart();
        Actualizar();
    }

    public void Terminar()
    {
        _reloj.Stop();
        _total.Stop();
        _frase.Reset();
        _letras = 0;
    }

    private void Actualizar()
    {
        var f = _desde;
        if (_letras > 0)
        {
            // Avanza al ritmo previsto hasta el 80 % de la frase y después cada vez más despacio: si tarda más de lo
            // previsto, se acerca al final sin llegar.
            var x = _frase.Elapsed.TotalSeconds / Math.Max(0.5, _letras * _segundosPorLetra);
            var parte = x <= 0.8 ? x : 0.8 + 0.15 * (1 - Math.Exp(-(x - 0.8) / 0.5));
            f += (_hasta - _desde) * Math.Min(parte, 0.95);
        }
        Fraccion = Math.Clamp(f, 0, 1);

        var texto = T("{0} % · {1}", Math.Floor(Fraccion * 100), Reloj(_total.Elapsed));
        if (Fraccion < 0.999)
        {
            texto += " · " + (_fraccionGenerada > 0.005
                ? Queda(TimeSpan.FromSeconds((1 - Fraccion) * _segundosGenerando / _fraccionGenerada))
                : T("calculando cuánto queda…"));
        }
        Estado = texto;
    }

    private static string Reloj(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string Queda(TimeSpan t) => t.TotalMinutes switch
    {
        < 1 => T("queda menos de un minuto"),
        < 90 => T("quedan unos {0} min", (int)Math.Ceiling(t.TotalMinutes)),
        _ => T("quedan unas {0} h", Math.Round(t.TotalHours, 1)),
    };
}
