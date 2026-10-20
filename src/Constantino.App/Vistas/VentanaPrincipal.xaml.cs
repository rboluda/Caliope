using System.Windows;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Constantino.App.Vistas;

public partial class VentanaPrincipal : FluentWindow
{
    public VentanaPrincipal(INavigationService navegacion, INavigationViewPageProvider paginas, ServicioAjustes ajustes)
    {
        InitializeComponent();
        Navegacion.SetPageProviderService(paginas);
        navegacion.SetNavigationControl(Navegacion);
        Loaded += async (_, _) =>
        {
            // Con el tema «Sistema», sigue los cambios de claro/oscuro de Windows mientras la app está abierta.
            if (ajustes.Actual.Tema == TemaApp.Sistema)
                SystemThemeWatcher.Watch(this);

            var captura = OpcionesCaptura.Leer(Environment.GetCommandLineArgs());
            var inicial = typeof(PaginaInicio);
            if (captura?.Pagina is { } nombre)
                inicial = typeof(PaginaInicio).Assembly.GetType($"{typeof(PaginaInicio).Namespace}.{nombre}") ?? inicial;
            navegacion.Navigate(inicial);

            if (captura is not null)
            {
                // Margen para que termine el análisis del equipo y se asiente la maquetación.
                await Task.Delay(TimeSpan.FromSeconds(4));
                OpcionesCaptura.Guardar(this, captura.Ruta);
                Application.Current.Shutdown();
            }
        };
    }
}
