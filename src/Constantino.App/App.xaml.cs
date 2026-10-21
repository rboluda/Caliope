using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.App.ViewModels;
using Constantino.App.Vistas;
using Constantino.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace Constantino.App;

public partial class App : Application
{
    private readonly IHost _host = CrearHost();

    private static IHost CrearHost()
    {
        var builder = Host.CreateApplicationBuilder();
        var s = builder.Services;

        s.AddSingleton<ServicioAjustes>();
        s.AddSingleton<ServicioEquipo>();
        s.AddSingleton<ServicioInstalacion>();
        s.AddSingleton<ServicioMotor>();
        s.AddSingleton<ServicioVoces>();
        s.AddSingleton<ServicioReproductor>();
        s.AddSingleton<ServicioNarracion>();
        s.AddSingleton<ServicioGrabacion>();
        s.AddSingleton<ServicioActualizaciones>();
        s.AddSingleton<AvisosViewModel>();
        s.AddSingleton<INavigationViewPageProvider, ProveedorPaginas>();
        s.AddSingleton<INavigationService, NavigationService>();

        s.AddSingleton<VentanaPrincipal>();
        s.AddSingleton<PaginaInicio>();
        s.AddSingleton<InicioViewModel>();
        s.AddSingleton<PaginaComponentes>();
        s.AddSingleton<ComponentesViewModel>();
        s.AddSingleton<PaginaAsistente>();
        s.AddSingleton<AsistenteViewModel>();
        s.AddSingleton<PaginaAyuda>();
        s.AddSingleton<PaginaAjustes>();
        s.AddSingleton<AjustesViewModel>();
        s.AddSingleton<PaginaVoces>();
        s.AddSingleton<VocesViewModel>();
        s.AddSingleton<PaginaDisenar>();
        s.AddSingleton<DisenarViewModel>();
        s.AddSingleton<PaginaClonar>();
        s.AddSingleton<ClonarViewModel>();
        s.AddSingleton<PaginaNarrar>();
        s.AddSingleton<NarrarViewModel>();

        return builder.Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // La ventana se dibuja con el procesador: la tarjeta gráfica queda entera para los motores de voz. Si la
        // llenan (o la llena otro programa), WPF no se queda sin memoria para dibujar y no se cierra con
        // UCEERR_RENDERTHREADFAILURE. La interfaz es sencilla y no lo nota.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        // El idioma va lo primero: todos los textos (también los del catálogo y los estáticos) se traducen al crearse.
        Idioma.Activar(_host.Services.GetRequiredService<ServicioAjustes>().Actual.Idioma);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Idioma.Cultura;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Idioma.Cultura;
        // Fechas y números de los bindings en el idioma elegido.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Idioma.Cultura.IetfLanguageTag)));

        await _host.StartAsync();
        _host.Services.GetRequiredService<ServicioAjustes>().AplicarTema();
        Marca.Activar();
        _host.Services.GetRequiredService<VentanaPrincipal>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // El motor de voz se cierra con la app para liberar la memoria de la tarjeta.
        await _host.Services.GetRequiredService<ServicioMotor>().DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    private void AlFallarSinControl(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Idioma.T("Ha ocurrido un error inesperado:\n\n{0}", e.Exception.Message), Marca.Nombre,
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
