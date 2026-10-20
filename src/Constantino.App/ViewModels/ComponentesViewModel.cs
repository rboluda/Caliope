using System.Collections.ObjectModel;
using Constantino.App.Servicios;
using Constantino.Core.Catalogo;
using Constantino.Core.Recomendaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Constantino.App.ViewModels;

public sealed record TarjetaComponente(
    string Nombre,
    string Resumen,
    string Detalle,
    string Etiqueta,
    ControlAppearance Apariencia,
    string Motivo,
    string Tamano,
    string Habilita,
    string Necesita,
    IReadOnlyList<Licencia> Licencias,
    string Estado);

public sealed record GrupoComponentes(string Nombre, IReadOnlyList<TarjetaComponente> Tarjetas);

public sealed partial class ComponentesViewModel(ServicioEquipo servicio) : ObservableObject
{
    [ObservableProperty] public partial bool Cargando { get; set; } = true;
    [ObservableProperty] public partial string ResumenDispositivo { get; set; } = "";

    public ObservableCollection<GrupoComponentes> Grupos { get; } = [];

    private bool _suscrito;

    public async Task CargarAsync()
    {
        if (!_suscrito)
        {
            // Si se vuelve a analizar el equipo desde Inicio, las recomendaciones cambian.
            servicio.AnalisisActualizado += async (_, _) => await MostrarAsync();
            _suscrito = true;
        }
        await MostrarAsync();
    }

    private async Task MostrarAsync()
    {
        Cargando = true;
        var (_, rec) = await servicio.ObtenerAnalisisAsync();
        var catalogo = servicio.Catalogo;
        ResumenDispositivo = rec.ResumenDispositivo;

        Grupos.Clear();
        foreach (var grupo in catalogo.Grupos)
        {
            var tarjetas = catalogo.Componentes
                .Where(c => c.Grupo == grupo)
                .Select(c => Tarjeta(catalogo, c, rec))
                .ToList();
            Grupos.Add(new GrupoComponentes(grupo, tarjetas));
        }
        Cargando = false;
    }

    private static TarjetaComponente Tarjeta(CatalogoComponentes catalogo, Componente c, InformeRecomendacion rec)
    {
        var r = rec.Para(c.Id);
        var necesita = c.DependeDe.Count == 0
            ? "Nada más."
            : string.Join(", ", c.DependeDe.Select(id => catalogo[id].Nombre)) + ".";
        if (c.GrupoExclusivo is not null)
        {
            var otras = catalogo.Componentes.Where(o => o.GrupoExclusivo == c.GrupoExclusivo && o.Id != c.Id);
            necesita += $" Es alternativa a: {string.Join(", ", otras.Select(o => o.Nombre))}.";
        }
        return new TarjetaComponente(
            c.Nombre,
            c.Resumen,
            c.Detalle,
            Presentacion.Etiqueta(r.Nivel),
            Presentacion.Apariencia(r.Nivel),
            r.Motivo,
            Presentacion.Gb(c.Tamano.Para(rec.UsaGpu)),
            string.Join(" · ", c.Habilita),
            necesita,
            c.Licencias,
            "No instalado");
    }
}
