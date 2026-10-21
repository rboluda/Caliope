using System.Collections.ObjectModel;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Equipo;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Constantino.App.ViewModels;

public sealed record FilaGpu(string Nombre, string Detalle, double PorcentajeUso, bool TieneMemoria, bool Utilizable);

public sealed record FilaPropuesta(string Nombre, string Etiqueta, ControlAppearance Apariencia, string Tamano);

public sealed partial class InicioViewModel(ServicioEquipo servicio, INavigationService navegacion) : ObservableObject
{
    [ObservableProperty] public partial bool Cargando { get; set; } = true;
    [ObservableProperty] public partial string SistemaOperativo { get; set; } = "";
    [ObservableProperty] public partial string Procesador { get; set; } = "";
    [ObservableProperty] public partial string Memoria { get; set; } = "";
    [ObservableProperty] public partial string CarpetaDatos { get; set; } = "";
    [ObservableProperty] public partial string EspacioDisco { get; set; } = "";
    [ObservableProperty] public partial string ResumenDispositivo { get; set; } = "";
    [ObservableProperty] public partial string DescargaPropuesta { get; set; } = "";
    [ObservableProperty] public partial bool SinGpus { get; set; }

    public ObservableCollection<FilaGpu> Gpus { get; } = [];
    public ObservableCollection<FilaAviso> Avisos { get; } = [];
    public ObservableCollection<FilaPropuesta> Propuesta { get; } = [];

    private bool _cargado;

    public async Task CargarSiHaceFaltaAsync()
    {
        if (_cargado)
            return;
        _cargado = true;
        await MostrarAsync(servicio.ObtenerAnalisisAsync());
    }

    [RelayCommand]
    private Task ReanalizarAsync() => MostrarAsync(servicio.ReanalizarAsync());

    [RelayCommand]
    private void IrAComponentes() => navegacion.Navigate(typeof(PaginaComponentes));

    private async Task MostrarAsync(Task<AnalisisEquipo> tarea)
    {
        Cargando = true;
        var (equipo, rec) = await tarea;

        SistemaOperativo = equipo.SistemaOperativo;
        Procesador = $"{equipo.Procesador} · {equipo.Nucleos} hilos";
        Memoria = $"{Presentacion.Gb(equipo.RamGb)} de RAM";

        Gpus.Clear();
        foreach (var g in equipo.Gpus)
            Gpus.Add(FilaDe(g));
        SinGpus = Gpus.Count == 0;

        var disco = equipo.Disco;
        CarpetaDatos = disco?.Ruta ?? "";
        EspacioDisco = disco is null
            ? "No se pudo consultar el disco."
            : $"{Presentacion.Gb(disco.LibreGb)} libres de {Presentacion.Gb(disco.TotalGb)} en {disco.Unidad}";

        ResumenDispositivo = rec.ResumenDispositivo;
        Avisos.Clear();
        foreach (var a in rec.Avisos)
            Avisos.Add(FilaAviso.De(a));

        Propuesta.Clear();
        foreach (var c in servicio.Catalogo.Componentes.Where(c => rec.Propuestos.Contains(c.Id)))
        {
            var nivel = rec.Para(c.Id).Nivel;
            Propuesta.Add(new FilaPropuesta(c.Nombre, Presentacion.Etiqueta(nivel), Presentacion.Apariencia(nivel),
                Presentacion.Gb(c.Tamano.Para(rec.UsaGpu))));
        }
        DescargaPropuesta = $"En total, unos {Presentacion.Gb(rec.DescargaPropuestaGb)} de descarga.";
        Cargando = false;
    }

    private static FilaGpu FilaDe(Gpu g)
    {
        if (g.VramTotalGb is not { } total)
            return new FilaGpu(g.Nombre, g.CompatibleCuda ? "Memoria desconocida" : "No sirve para generar voz: se usará el procesador", 0, false, false);
        var usada = g.VramUsadaGb ?? 0;
        var detalle = $"{Presentacion.Gb(total)} de memoria · {Presentacion.Gb(usada)} en uso ahora · driver {g.Driver}";
        return new FilaGpu(g.Nombre, detalle, total > 0 ? usada / total * 100 : 0, true, true);
    }
}
