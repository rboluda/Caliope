using Constantino.Core;
using Constantino.Core.Recomendaciones;
using Wpf.Ui.Controls;

namespace Constantino.App.ViewModels;

/// <summary>Textos y colores compartidos para mostrar recomendaciones y avisos.</summary>
public static class Presentacion
{
    public static string Gb(double valor) => Formato.Gb(valor);

    public static string Etiqueta(NivelRecomendacion nivel) => nivel switch
    {
        NivelRecomendacion.Imprescindible => "Imprescindible",
        NivelRecomendacion.Recomendado => "Recomendado",
        NivelRecomendacion.Opcional => "Opcional",
        _ => "No recomendado",
    };

    public static ControlAppearance Apariencia(NivelRecomendacion nivel) => nivel switch
    {
        NivelRecomendacion.Imprescindible => ControlAppearance.Primary,
        NivelRecomendacion.Recomendado => ControlAppearance.Success,
        NivelRecomendacion.Opcional => ControlAppearance.Info,
        _ => ControlAppearance.Caution,
    };

    public static InfoBarSeverity Severidad(SeveridadAviso severidad) => severidad switch
    {
        SeveridadAviso.Problema => InfoBarSeverity.Error,
        SeveridadAviso.Atencion => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };
}

public sealed record FilaAviso(string Titulo, string Mensaje, InfoBarSeverity Severidad)
{
    public static FilaAviso De(Aviso a) => new(a.Titulo, a.Mensaje, Presentacion.Severidad(a.Severidad));
}
