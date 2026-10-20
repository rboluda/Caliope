using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaInicio : Page, INavigableView<InicioViewModel>
{
    public InicioViewModel ViewModel { get; }

    public PaginaInicio(InicioViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.CargarSiHaceFaltaAsync();
    }
}
