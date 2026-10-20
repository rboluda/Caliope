using System.Collections.ObjectModel;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Catalogo;
using Constantino.Core.Instalaciones;
using Constantino.Core.Recomendaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Constantino.App.ViewModels;

public sealed partial class TarjetaComponente(Componente c, Recomendacion r, string tamano, string necesita) : ObservableObject
{
    public string Id { get; } = c.Id;
    public string Nombre { get; } = c.Nombre;
    public string Resumen { get; } = c.Resumen;
    public string Detalle { get; } = c.Detalle;
    public string Etiqueta { get; } = Presentacion.Etiqueta(r.Nivel);
    public ControlAppearance Apariencia { get; } = Presentacion.Apariencia(r.Nivel);
    public string Motivo { get; } = r.Motivo;
    public string Tamano { get; } = tamano;
    public string Habilita { get; } = string.Join(" · ", c.Habilita);
    public string Necesita { get; } = necesita;
    public IReadOnlyList<Licencia> Licencias { get; } = c.Licencias;
    public string? DisponibleEn { get; } = c.SePuedeInstalar ? null : c.DisponibleEn ?? "Llegará en una próxima versión.";

    [ObservableProperty] public partial string Estado { get; set; } = "";
    [ObservableProperty] public partial bool PuedeInstalar { get; set; }
    [ObservableProperty] public partial string TextoInstalar { get; set; } = "Instalar";
    [ObservableProperty] public partial bool PuedeReparar { get; set; }
    [ObservableProperty] public partial bool PuedeDesinstalar { get; set; }

    public void Aplicar(EstadoComponente estado)
    {
        Estado = estado switch
        {
            EstadoComponente.NoDisponible => "Próximamente",
            EstadoComponente.NoInstalado => "No instalado",
            EstadoComponente.Instalado => "Instalado",
            EstadoComponente.Danado => "Dañado: faltan archivos",
            EstadoComponente.Actualizable => "Hay una versión nueva",
            _ => "",
        };
        PuedeInstalar = estado is EstadoComponente.NoInstalado or EstadoComponente.Actualizable;
        TextoInstalar = estado == EstadoComponente.Actualizable ? "Actualizar" : "Instalar";
        PuedeReparar = estado is EstadoComponente.Instalado or EstadoComponente.Danado;
        PuedeDesinstalar = estado is EstadoComponente.Instalado or EstadoComponente.Danado or EstadoComponente.Actualizable;
    }
}

public sealed record GrupoComponentes(string Nombre, IReadOnlyList<TarjetaComponente> Tarjetas);

public sealed partial class ComponentesViewModel : ObservableObject
{
    private readonly ServicioEquipo _equipo;
    private readonly INavigationService _navegacion;

    public ComponentesViewModel(ServicioEquipo equipo, ServicioInstalacion instalacion, INavigationService navegacion)
    {
        _equipo = equipo;
        _navegacion = navegacion;
        Instalacion = instalacion;
        // Si se vuelve a analizar el equipo, cambian las recomendaciones; si se instala algo, los estados.
        equipo.AnalisisActualizado += async (_, _) => await MostrarAsync();
        instalacion.EstadosCambiados += async (_, _) => await RefrescarEstadosAsync();
    }

    public ServicioInstalacion Instalacion { get; }

    [ObservableProperty] public partial bool Cargando { get; set; } = true;
    [ObservableProperty] public partial string ResumenDispositivo { get; set; } = "";
    [ObservableProperty] public partial bool MostrarProgreso { get; set; }
    [ObservableProperty] public partial string? Aviso { get; set; }

    public ObservableCollection<GrupoComponentes> Grupos { get; } = [];

    public Task CargarAsync() => MostrarAsync();

    private async Task MostrarAsync()
    {
        Cargando = true;
        var (_, rec) = await _equipo.ObtenerAnalisisAsync();
        var catalogo = _equipo.Catalogo;
        ResumenDispositivo = rec.ResumenDispositivo;

        Grupos.Clear();
        foreach (var grupo in catalogo.Grupos)
        {
            var tarjetas = catalogo.Componentes
                .Where(c => c.Grupo == grupo)
                .Select(c => new TarjetaComponente(c, rec.Para(c.Id), Formato.Gb(c.Tamano.Para(rec.UsaGpu)), Necesita(catalogo, c)))
                .ToList();
            Grupos.Add(new GrupoComponentes(grupo, tarjetas));
        }
        await RefrescarEstadosAsync();
        Cargando = false;
    }

    private async Task RefrescarEstadosAsync()
    {
        var instalador = await Instalacion.InstaladorAsync();
        foreach (var t in Grupos.SelectMany(g => g.Tarjetas))
            t.Aplicar(instalador.EstadoDe(t.Id));
    }

    private static string Necesita(CatalogoComponentes catalogo, Componente c)
    {
        var texto = c.DependeDe.Count == 0
            ? "Nada más."
            : string.Join(", ", c.DependeDe.Select(id => catalogo[id].Nombre)) + ".";
        if (c.GrupoExclusivo is not null)
        {
            var otras = catalogo.Componentes.Where(o => o.GrupoExclusivo == c.GrupoExclusivo && o.Id != c.Id);
            texto += $" Es alternativa a: {string.Join(", ", otras.Select(o => o.Nombre))}.";
        }
        return texto;
    }

    [RelayCommand]
    private async Task InstalarAsync(TarjetaComponente tarjeta)
    {
        var plan = await Instalacion.PlanAsync([tarjeta.Id]);
        var (_, rec) = await _equipo.ObtenerAnalisisAsync();
        var lista = string.Join("\n", plan.Select(c => $"• {c.Nombre} ({Formato.Gb(c.Tamano.Para(rec.UsaGpu))})"));
        var total = Formato.Gb(plan.Sum(c => c.Tamano.Para(rec.UsaGpu)));
        if (!await ConfirmarAsync($"{tarjeta.TextoInstalar} {tarjeta.Nombre}",
                $"Se descargará e instalará:\n\n{lista}\n\nEn total, unos {total}.", tarjeta.TextoInstalar))
            return;
        await EjecutarAsync([tarjeta.Id], forzar: false);
    }

    [RelayCommand]
    private async Task RepararAsync(TarjetaComponente tarjeta)
    {
        if (!await ConfirmarAsync($"Reparar {tarjeta.Nombre}",
                "Se volverá a instalar este componente. Lo ya descargado se aprovecha cuando es posible.", "Reparar"))
            return;
        await EjecutarAsync([tarjeta.Id], forzar: true);
    }

    [RelayCommand]
    private async Task DesinstalarAsync(TarjetaComponente tarjeta)
    {
        if (!await ConfirmarAsync($"Desinstalar {tarjeta.Nombre}",
                $"Se borrará de tu equipo y se liberarán unos {tarjeta.Tamano}. Podrás volver a instalarlo cuando quieras.", "Desinstalar"))
            return;
        Aviso = await Instalacion.DesinstalarAsync(tarjeta.Id);
    }

    [RelayCommand]
    private void AbrirAsistente() => _navegacion.Navigate(typeof(PaginaAsistente));

    private async Task EjecutarAsync(IReadOnlyList<string> ids, bool forzar)
    {
        Aviso = null;
        MostrarProgreso = true;
        await Instalacion.InstalarAsync(ids, forzar);
    }

    private static async Task<bool> ConfirmarAsync(string titulo, string mensaje, string accion)
    {
        var caja = new MessageBox
        {
            Title = titulo,
            Content = mensaje,
            PrimaryButtonText = accion,
            CloseButtonText = "Cancelar",
        };
        return await caja.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
