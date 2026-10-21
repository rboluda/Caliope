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
        IServiceProvider servicios, AvisosViewModel avisos)
    {
        InitializeComponent();
        Aviso.DataContext = avisos;
        Navegacion.SetPageProviderService(paginas);
        navegacion.SetNavigationControl(Navegacion);
        Loaded += async (_, _) =>
        {
            // Con el tema «Sistema», sigue los cambios de claro/oscuro de Windows mientras la app está abierta.
            if (ajustes.Actual.Tema == TemaApp.Sistema)
                SystemThemeWatcher.Watch(this, updateAccents: false);

            var captura = OpcionesCaptura.Leer(Environment.GetCommandLineArgs());
            // Hasta completar la configuración inicial, se arranca en el asistente.
            var inicial = ajustes.Actual.AsistenteCompletado ? typeof(PaginaInicio) : typeof(PaginaAsistente);
            if (captura?.Pagina is { } nombre)
                inicial = typeof(PaginaInicio).Assembly.GetType($"{typeof(PaginaInicio).Namespace}.{nombre}") ?? inicial;
            navegacion.Navigate(inicial);

            // Versión nueva o mejoras para componentes (sin estorbar al arrancar ni en las capturas).
            if (captura is null && ajustes.Actual.AsistenteCompletado)
            {
                // Los sonidos incluidos se crean en segundo plano, para que estén listos al narrar.
                _ = servicios.GetRequiredService<ServicioNarracion>().PrepararSonidosAsync();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await Dispatcher.InvokeAsync(() => avisos.ComprobarAsync()).Task.Unwrap();
                });
            }

            if (captura is not null)
            {
                if (captura.Alto is { } alto)
                    Height = alto;
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
