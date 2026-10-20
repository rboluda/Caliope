using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.App.ViewModels;
using Constantino.App.Vistas;
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
        s.AddSingleton<INavigationViewPageProvider, ProveedorPaginas>();
        s.AddSingleton<INavigationService, NavigationService>();

        s.AddSingleton<VentanaPrincipal>();
        s.AddSingleton<PaginaInicio>();
        s.AddSingleton<InicioViewModel>();
        s.AddSingleton<PaginaComponentes>();
        s.AddSingleton<ComponentesViewModel>();
        s.AddSingleton<PaginaAjustes>();
        s.AddSingleton<AjustesViewModel>();
        s.AddSingleton<PaginaVoces>();
        s.AddSingleton<PaginaDisenar>();
        s.AddSingleton<PaginaClonar>();
        s.AddSingleton<PaginaNarrar>();

        return builder.Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Fechas y números de los bindings en español.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.GetCultureInfo("es-ES").IetfLanguageTag)));

        await _host.StartAsync();
        _host.Services.GetRequiredService<ServicioAjustes>().AplicarTema();
        _host.Services.GetRequiredService<VentanaPrincipal>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    private void AlFallarSinControl(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Ha ocurrido un error inesperado:\n\n{e.Exception.Message}", "Constantino",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
