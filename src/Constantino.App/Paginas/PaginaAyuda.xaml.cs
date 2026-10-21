using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Constantino.App.Servicios;
using Wpf.Ui;

namespace Constantino.App.Paginas;

/// <summary>Ayuda dentro de la aplicación: qué es, cómo empezar, el guion, los ajustes y qué hacer si algo falla.</summary>
public partial class PaginaAyuda : Page
{
    public const string UrlRepositorio = "https://github.com/rboluda/Caliope";

    private readonly ServicioInstalacion _instalacion;
    private readonly INavigationService _navegacion;

    public PaginaAyuda(ServicioInstalacion instalacion, INavigationService navegacion)
    {
        _instalacion = instalacion;
        _navegacion = navegacion;
        InitializeComponent();
    }

    private void AlIrAComponentes(object sender, RoutedEventArgs e) => _navegacion.Navigate(typeof(PaginaComponentes));

    private void AlIrANarrar(object sender, RoutedEventArgs e) => _navegacion.Navigate(typeof(PaginaNarrar));

    private async void AlAbrirDatos(object sender, RoutedEventArgs e) =>
        Abrir((await _instalacion.InstaladorAsync()).Rutas.Raiz);

    private async void AlAbrirRegistros(object sender, RoutedEventArgs e) =>
        Abrir((await _instalacion.InstaladorAsync()).Rutas.Registros);

    private void AlAbrirGitHub(object sender, RoutedEventArgs e) => Abrir(UrlRepositorio);

    private static void Abrir(string destino)
    {
        if (!destino.StartsWith("http", StringComparison.Ordinal))
            Directory.CreateDirectory(destino);
        Process.Start(new ProcessStartInfo(destino) { UseShellExecute = true });
    }
}
