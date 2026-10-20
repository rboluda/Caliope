using Constantino.Core.Catalogo;
using Constantino.Core.Equipo;
using Constantino.Core.Recomendaciones;

namespace Constantino.App.Servicios;

public sealed record AnalisisEquipo(InformeEquipo Equipo, InformeRecomendacion Recomendacion);

/// <summary>Catálogo de componentes y análisis del equipo, compartidos por todas las páginas.</summary>
public sealed class ServicioEquipo(ServicioAjustes ajustes)
{
    private Task<AnalisisEquipo>? _analisis;

    public CatalogoComponentes Catalogo { get; } = CatalogoComponentes.CargarPredeterminado();

    public event EventHandler? AnalisisActualizado;

    /// <summary>El análisis se hace una vez y se reutiliza hasta que se pide otro.</summary>
    public Task<AnalisisEquipo> ObtenerAnalisisAsync() => _analisis ??= AnalizarAsync();

    public Task<AnalisisEquipo> ReanalizarAsync()
    {
        _analisis = AnalizarAsync();
        return _analisis;
    }

    private async Task<AnalisisEquipo> AnalizarAsync()
    {
        var equipo = await Task.Run(() => DetectorEquipo.DetectarAsync(ajustes.CarpetaDatosEfectiva));
        var analisis = new AnalisisEquipo(equipo, Recomendador.Recomendar(Catalogo, equipo));
        AnalisisActualizado?.Invoke(this, EventArgs.Empty);
        return analisis;
    }
}
