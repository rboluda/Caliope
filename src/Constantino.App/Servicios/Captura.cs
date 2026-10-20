using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Constantino.App.Servicios;

/// <summary>
/// Opción de desarrollo: <c>Constantino.exe --captura salida.png [--pagina PaginaComponentes]</c>
/// abre la página indicada, la dibuja en un PNG y cierra la aplicación. Sirve para revisar la interfaz
/// y documentarla sin depender de capturas de pantalla del sistema.
/// </summary>
/// <param name="Paso">Solo para el asistente: el paso que se muestra (0–5).</param>
public sealed record OpcionesCaptura(string Ruta, string? Pagina, int? Paso)
{
    public static OpcionesCaptura? Leer(string[] args)
    {
        var i = Array.IndexOf(args, "--captura");
        if (i < 0 || i + 1 >= args.Length)
            return null;
        return new OpcionesCaptura(Path.GetFullPath(args[i + 1]), Valor("--pagina"),
            int.TryParse(Valor("--paso"), out var paso) ? paso : null);

        string? Valor(string nombre) =>
            Array.IndexOf(args, nombre) is var j and >= 0 && j + 1 < args.Length ? args[j + 1] : null;
    }

    public static void Guardar(Window ventana, string ruta)
    {
        var dpi = VisualTreeHelper.GetDpi(ventana);
        var ancho = ventana.ActualWidth;
        var alto = ventana.ActualHeight;

        // El fondo Mica lo pinta Windows, no WPF: se sustituye por el color de fondo del tema.
        var dibujo = new DrawingVisual();
        using (var dc = dibujo.RenderOpen())
        {
            var fondo = ventana.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(fondo, null, new Rect(0, 0, ancho, alto));
            dc.DrawRectangle(new VisualBrush((Visual)ventana.Content), null, new Rect(0, 0, ancho, alto));
        }

        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(ancho * dpi.DpiScaleX), (int)Math.Ceiling(alto * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dibujo);

        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var archivo = File.Create(ruta);
        png.Save(archivo);
    }
}
