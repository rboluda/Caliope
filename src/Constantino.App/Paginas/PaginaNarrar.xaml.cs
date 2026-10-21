using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;
using static Constantino.Core.Idioma;

namespace Constantino.App.Paginas;

public partial class PaginaNarrar : Page, INavigableView<NarrarViewModel>
{
    public NarrarViewModel ViewModel { get; }

    public PaginaNarrar(NarrarViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.CargarAsync();
    }

    /// <summary>Inserta la etiqueta del botón donde está el cursor.</summary>
    private void AlInsertar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string etiqueta })
            Insertar(etiqueta);
    }

    private void AlInsertarBloque(object sender, RoutedEventArgs e) => Insertar("\n\n" + T("# nuevo bloque") + "\n");

    // --- Menú contextual de cada bloque (hereda la fila como DataContext) ---------------------

    private void AlGuardarBloque(object sender, RoutedEventArgs e) => ConFila(sender, ViewModel.GuardarBloqueComoCommand.Execute);
    private void AlCopiarBloque(object sender, RoutedEventArgs e) => ConFila(sender, ViewModel.CopiarBloqueCommand.Execute);
    private void AlMostrarBloque(object sender, RoutedEventArgs e) => ConFila(sender, ViewModel.MostrarBloqueCommand.Execute);

    private static void ConFila(object sender, Action<FilaBloque> accion)
    {
        if (sender is FrameworkElement { DataContext: FilaBloque fila })
            accion(fila);
    }

    // --- Arrastrar un audio fuera de la app (al editor de vídeo, a una carpeta…) ----------------

    private Point? _inicioArrastre;

    private void AlPulsarAudio(object sender, MouseButtonEventArgs e) =>
        _inicioArrastre = EsBoton(e.OriginalSource as DependencyObject) ? null : e.GetPosition(this);

    private void AlArrastrarAudio(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _inicioArrastre is not { } inicio ||
            sender is not FrameworkElement { Tag: string ruta } elemento || !File.Exists(ruta))
            return;
        var d = e.GetPosition(this) - inicio;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _inicioArrastre = null;
        DragDrop.DoDragDrop(elemento, new DataObject(DataFormats.FileDrop, new[] { ruta }), DragDropEffects.Copy);
    }

    /// <summary>Pulsar un botón de la fila no debe empezar a arrastrar.</summary>
    private static bool EsBoton(DependencyObject? origen)
    {
        for (var d = origen; d is not null; d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase)
                return true;
        return false;
    }

    private void Insertar(string texto)
    {
        var posicion = Editor.SelectionStart;
        Editor.SelectedText = texto;
        Editor.CaretIndex = posicion + texto.Length;
        Editor.Focus();
    }
}
