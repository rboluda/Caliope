using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaDoblar : Page, INavigableView<DoblarViewModel>
{
    public DoblarViewModel ViewModel { get; }

    public PaginaDoblar(DoblarViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.CargarAsync();
    }
}
