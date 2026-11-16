using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaConvertir : Page, INavigableView<ConvertirViewModel>
{
    public ConvertirViewModel ViewModel { get; }

    public PaginaConvertir(ConvertirViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.CargarAsync();
    }
}
