using System.Windows;
using System.Windows.Controls;
using Constantino.App.Servicios;
using static Constantino.Core.Idioma;

namespace Constantino.App.Controles;

/// <summary>
/// Clic derecho en cualquier aviso (InfoBar): «Copiar el mensaje» y, si el motor ha fallado, «Copiar el detalle
/// técnico» con la traza entera. El texto de un InfoBar no se puede seleccionar.
/// </summary>
public static class CopiarAviso
{
    public static readonly DependencyProperty ActivoProperty = DependencyProperty.RegisterAttached(
        "Activo", typeof(bool), typeof(CopiarAviso), new PropertyMetadata(false, AlCambiar));

    public static bool GetActivo(DependencyObject d) => (bool)d.GetValue(ActivoProperty);
    public static void SetActivo(DependencyObject d, bool valor) => d.SetValue(ActivoProperty, valor);

    private static void AlCambiar(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Wpf.Ui.Controls.InfoBar aviso || e.NewValue is not true) return;
        aviso.ContextMenuOpening += (_, _) => aviso.ContextMenu = Menu(aviso);
        aviso.ContextMenu = Menu(aviso);
    }

    private static ContextMenu Menu(Wpf.Ui.Controls.InfoBar aviso)
    {
        var menu = new ContextMenu();
        var mensaje = new MenuItem { Header = T("Copiar el mensaje") };
        mensaje.Click += (_, _) => Copiar(string.Join("\n", new[] { aviso.Title, aviso.Message }
            .Where(t => !string.IsNullOrWhiteSpace(t))));
        menu.Items.Add(mensaje);
        if (ServicioMotor.UltimoDetalle is { } detalle)
        {
            var tecnico = new MenuItem { Header = T("Copiar el detalle técnico") };
            tecnico.Click += (_, _) => Copiar(detalle);
            menu.Items.Add(tecnico);
        }
        return menu;
    }

    private static void Copiar(string texto)
    {
        try { Clipboard.SetText(texto); }
        catch (System.Runtime.InteropServices.ExternalException) { } // portapapeles ocupado por otro programa
    }
}
