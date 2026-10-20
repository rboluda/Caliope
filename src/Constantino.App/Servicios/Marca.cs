using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Constantino.App.Servicios;

/// <summary>Nombre y colores de la aplicación, sacados del logotipo.</summary>
public static class Marca
{
    public const string Nombre = "Calíope";

    /// <summary>Azul marino del perfil del logotipo.</summary>
    public static readonly Color AzulMarino = Color.FromRgb(0x01, 0x21, 0x5E);

    /// <summary>Amarillo de las ondas de voz del logotipo.</summary>
    public static readonly Color Amarillo = Color.FromRgb(0xFE, 0xBB, 0x01);

    /// <summary>
    /// Sustituye el color de acento de Windows por el de la marca: azul marino con el tema claro y amarillo con
    /// el oscuro (el azul marino apenas se distingue sobre fondo oscuro).
    /// </summary>
    public static void AplicarAcento(ApplicationTheme tema) =>
        ApplicationAccentColorManager.Apply(tema == ApplicationTheme.Dark ? Amarillo : AzulMarino, tema);

    /// <summary>Aplica el acento ahora y cada vez que cambie el tema (también cuando lo cambia Windows).</summary>
    public static void Activar()
    {
        AplicarAcento(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += (tema, _) => AplicarAcento(tema);
    }
}
