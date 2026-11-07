using System.Windows.Controls;
using System.Windows.Threading;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaComponentes : Page, INavigableView<ComponentesViewModel>
{
    public ComponentesViewModel ViewModel { get; }

    public PaginaComponentes(ComponentesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.CargarAsync();
            // Al rellenarse las cajas, la página se desplazaba hacia abajo: siempre empieza por el título y el resumen.
            await Dispatcher.InvokeAsync(() => Desplazamiento.ScrollToTop(), DispatcherPriority.ContextIdle);
        };
    }
}
