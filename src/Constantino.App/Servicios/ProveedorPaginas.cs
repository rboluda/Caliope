using Wpf.Ui.Abstractions;

namespace Constantino.App.Servicios;

/// <summary>Entrega a la navegación de WPF-UI las páginas registradas en el contenedor.</summary>
public sealed class ProveedorPaginas(IServiceProvider servicios) : INavigationViewPageProvider
{
    public object? GetPage(Type pageType) => servicios.GetService(pageType);
}
