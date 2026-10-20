using System.Windows;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Constantino.App.Vistas;

public partial class VentanaPrincipal : FluentWindow
{
    public VentanaPrincipal(INavigationService navegacion, INavigationViewPageProvider paginas, ServicioAjustes ajustes,
        IServiceProvider servicios)
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
            // Hasta completar la configuración inicial, se arranca en el asistente.
            var inicial = ajustes.Actual.AsistenteCompletado ? typeof(PaginaInicio) : typeof(PaginaAsistente);
            if (captura?.Pagina is { } nombre)
                inicial = typeof(PaginaInicio).Assembly.GetType($"{typeof(PaginaInicio).Namespace}.{nombre}") ?? inicial;
            navegacion.Navigate(inicial);

            if (captura is not null)
            {
                // Margen para que termine el análisis del equipo y se asiente la maquetación.
                await Task.Delay(TimeSpan.FromSeconds(4));
                if (captura.Paso is { } paso && inicial == typeof(PaginaAsistente))
                {
                    servicios.GetRequiredService<AsistenteViewModel>().Paso = paso;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                OpcionesCaptura.Guardar(this, captura.Ruta);
                Application.Current.Shutdown();
            }
        };
    }
}
