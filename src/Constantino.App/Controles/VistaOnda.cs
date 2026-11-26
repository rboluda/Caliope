using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Constantino.Core.Audio;

namespace Constantino.App.Controles;

/// <summary>
/// Forma de onda con un tramo seleccionable: arrastrar sobre la onda marca un tramo nuevo; arrastrar cerca de un
/// borde lo mueve. <see cref="Inicio"/> y <see cref="Fin"/> van en segundos y se enlazan en ambos sentidos.
/// Además dibuja los tramos ya añadidos (<see cref="Tramos"/>); con Ctrl pulsado, empezar a arrastrar añade antes el
/// tramo actual (<see cref="ComandoAnadir"/>), para usar varios trozos separados.
/// </summary>
public sealed class VistaOnda : FrameworkElement
{
    private const double Asa = 8;

    public static readonly DependencyProperty AudioProperty = DependencyProperty.Register(
        nameof(Audio), typeof(Pcm), typeof(VistaOnda),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((VistaOnda)d)._picos = null));

    public static readonly DependencyProperty InicioProperty = DependencyProperty.Register(
        nameof(Inicio), typeof(double), typeof(VistaOnda),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty FinProperty = DependencyProperty.Register(
        nameof(Fin), typeof(double), typeof(VistaOnda),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TramosProperty = DependencyProperty.Register(
        nameof(Tramos), typeof(IReadOnlyList<Tramo>), typeof(VistaOnda),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ComandoAnadirProperty = DependencyProperty.Register(
        nameof(ComandoAnadir), typeof(ICommand), typeof(VistaOnda));

    /// <summary>Tramos ya añadidos (además del que se está marcando).</summary>
    public IReadOnlyList<Tramo>? Tramos
    {
        get => (IReadOnlyList<Tramo>?)GetValue(TramosProperty);
        set => SetValue(TramosProperty, value);
    }

    /// <summary>Se ejecuta con Ctrl + arrastrar: añade el tramo actual antes de empezar otro.</summary>
    public ICommand? ComandoAnadir
    {
        get => (ICommand?)GetValue(ComandoAnadirProperty);
        set => SetValue(ComandoAnadirProperty, value);
    }

    public Pcm? Audio
    {
        get => (Pcm?)GetValue(AudioProperty);
        set => SetValue(AudioProperty, value);
    }

    public double Inicio
    {
        get => (double)GetValue(InicioProperty);
        set => SetValue(InicioProperty, value);
    }

    public double Fin
    {
        get => (double)GetValue(FinProperty);
        set => SetValue(FinProperty, value);
    }

    private (float Min, float Max)[]? _picos;
    private enum Arrastre { Nada, Inicio, Fin, Nuevo }
    private Arrastre _arrastre;
    private double _ancla;

    public VistaOnda()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var ancho = ActualWidth;
        var alto = ActualHeight;
        var fondo = TryFindResource("ControlFillColorSecondaryBrush") as Brush ?? Brushes.WhiteSmoke;
        var onda = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        var acento = TryFindResource("AccentFillColorDefaultBrush") as SolidColorBrush ?? Brushes.SteelBlue;
        dc.DrawRoundedRectangle(fondo, null, new Rect(0, 0, ancho, alto), 6, 6);
        if (Audio is not { } audio || audio.Muestras.Length == 0 || ancho < 2)
            return;

        // Tramos ya añadidos: sombreados y con bordes finos.
        if (Tramos is { Count: > 0 } tramos)
        {
            var sombra = new SolidColorBrush(Color.FromArgb(45, acento.Color.R, acento.Color.G, acento.Color.B));
            var fino = new Pen(new SolidColorBrush(Color.FromArgb(160, acento.Color.R, acento.Color.G, acento.Color.B)), 1);
            foreach (var t in tramos)
            {
                var (a, b) = (X(t.Inicio), X(t.Fin));
                dc.DrawRectangle(sombra, null, new Rect(a, 0, Math.Max(1, b - a), alto));
                dc.DrawLine(fino, new Point(a, 0), new Point(a, alto));
                dc.DrawLine(fino, new Point(b, 0), new Point(b, alto));
            }
        }

        // Tramo seleccionado.
        var x0 = X(Inicio);
        var x1 = X(Fin);
        if (x1 > x0)
        {
            var relleno = new SolidColorBrush(Color.FromArgb(60, acento.Color.R, acento.Color.G, acento.Color.B));
            dc.DrawRectangle(relleno, null, new Rect(x0, 0, x1 - x0, alto));
        }

        // Onda: mínimo y máximo de cada columna de píxeles.
        var columnas = (int)ancho;
        if (_picos is null || _picos.Length != columnas)
            _picos = CalcularPicos(audio, columnas);
        var lapiz = new Pen(onda, 1);
        lapiz.Freeze();
        var mitad = alto / 2;
        for (var c = 0; c < columnas; c++)
        {
            var (min, max) = _picos[c];
            dc.DrawLine(lapiz, new Point(c + 0.5, mitad - max * mitad * 0.95), new Point(c + 0.5, mitad - min * mitad * 0.95 + 0.5));
        }

        if (x1 > x0)
        {
            var borde = new Pen(acento, 2);
            dc.DrawLine(borde, new Point(x0, 0), new Point(x0, alto));
            dc.DrawLine(borde, new Point(x1, 0), new Point(x1, alto));
        }
    }

    private static (float, float)[] CalcularPicos(Pcm audio, int columnas)
    {
        var m = audio.Muestras;
        var picos = new (float, float)[columnas];
        var porColumna = (double)m.Length / columnas;
        for (var c = 0; c < columnas; c++)
        {
            var a = (int)(c * porColumna);
            var b = Math.Min(m.Length, Math.Max(a + 1, (int)((c + 1) * porColumna)));
            float min = 0, max = 0;
            for (var i = a; i < b; i++)
            {
                if (m[i] < min) min = m[i];
                if (m[i] > max) max = m[i];
            }
            picos[c] = (min, max);
        }
        return picos;
    }

    private double Duracion => Audio?.Segundos ?? 0;

    private double X(double segundos) => Duracion <= 0 ? 0 : segundos / Duracion * ActualWidth;

    private double T(double x) => Duracion <= 0 ? 0 : Math.Clamp(x / ActualWidth, 0, 1) * Duracion;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Audio is null)
            return;
        var x = e.GetPosition(this).X;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl: el tramo actual pasa a la lista y se empieza otro, aunque se pulse sobre un borde.
            if (Fin - Inicio >= TramosAudio.Minimo && ComandoAnadir is { } anadir && anadir.CanExecute(null))
                anadir.Execute(null);
            _arrastre = Arrastre.Nuevo;
            _ancla = T(x);
            Inicio = Fin = _ancla;
        }
        else if (Fin > Inicio && Math.Abs(x - X(Inicio)) <= Asa)
            _arrastre = Arrastre.Inicio;
        else if (Fin > Inicio && Math.Abs(x - X(Fin)) <= Asa)
            _arrastre = Arrastre.Fin;
        else
        {
            _arrastre = Arrastre.Nuevo;
            _ancla = T(x);
            Inicio = Fin = _ancla;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (_arrastre == Arrastre.Nada)
        {
            Cursor = Fin > Inicio && (Math.Abs(x - X(Inicio)) <= Asa || Math.Abs(x - X(Fin)) <= Asa) ? Cursors.SizeWE : Cursors.IBeam;
            return;
        }
        var t = T(x);
        switch (_arrastre)
        {
            case Arrastre.Inicio:
                Inicio = Math.Min(t, Fin);
                break;
            case Arrastre.Fin:
                Fin = Math.Max(t, Inicio);
                break;
            case Arrastre.Nuevo:
                Inicio = Math.Min(_ancla, t);
                Fin = Math.Max(_ancla, t);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _arrastre = Arrastre.Nada;
        ReleaseMouseCapture();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _picos = null;
    }
}
