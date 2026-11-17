using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaEnDirecto : Page, INavigableView<EnDirectoViewModel>
{
    public EnDirectoViewModel ViewModel { get; }

    public PaginaEnDirecto(EnDirectoViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.CargarAsync();
    }
}
