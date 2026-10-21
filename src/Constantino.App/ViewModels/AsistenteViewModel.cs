using System.Collections.ObjectModel;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Equipo;
using Constantino.Core.Instalaciones;
using Constantino.Core.Recomendaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Constantino.App.ViewModels;

/// <summary>Un componente en el paso de selección del asistente.</summary>
public sealed partial class OpcionComponente(
    string id, string nombre, string resumen, string motivo, NivelRecomendacion nivel, double tamanoGb,
    string? grupoExclusivo, bool bloqueado, bool disponible, string? disponibleEn, bool yaInstalado,
    string? consejo = null) : ObservableObject
{
    public string? Consejo { get; } = yaInstalado ? null : consejo;
    public string Id { get; } = id;
    public string Nombre { get; } = nombre;
    public string Resumen { get; } = resumen;
    public string Motivo { get; } = yaInstalado ? "Ya está instalado." : disponible ? motivo : disponibleEn ?? "Todavía no disponible.";
    public string Etiqueta { get; } = yaInstalado ? "Instalado" : Presentacion.Etiqueta(nivel);
    public ControlAppearance Apariencia { get; } = yaInstalado ? ControlAppearance.Success : Presentacion.Apariencia(nivel);
    public double TamanoGb { get; } = tamanoGb;
    public string Tamano { get; } = Formato.Gb(tamanoGb);
    public string? GrupoExclusivo { get; } = grupoExclusivo;
    public bool YaInstalado { get; } = yaInstalado;
    public bool Editable { get; } = !bloqueado && disponible && !yaInstalado;

    [ObservableProperty] public partial bool Seleccionado { get; set; }

    public Action<OpcionComponente>? AlCambiar { get; set; }

    partial void OnSeleccionadoChanged(bool value) => AlCambiar?.Invoke(this);
}

public sealed partial class IndicadorPaso(int numero, string nombre) : ObservableObject
{
    public int Numero { get; } = numero;
    public string Nombre { get; } = nombre;
    [ObservableProperty] public partial string Estado { get; set; } = "Pendiente";
}

public sealed partial class AsistenteViewModel(
    ServicioEquipo equipo,
    ServicioAjustes ajustes,
    ServicioInstalacion instalacion,
    INavigationService navegacion) : ObservableObject
{
    public const int PasoBienvenida = 0, PasoEquipo = 1, PasoCarpeta = 2, PasoComponentes = 3, PasoInstalacion = 4, PasoFin = 5;

    private static readonly string[] NombresPasos = ["Bienvenida", "Tu equipo", "Carpeta", "Componentes", "Instalación", "Listo"];

    /// <summary>La fila de pasos de arriba: hechos, actual y pendientes.</summary>
    public IReadOnlyList<IndicadorPaso> Indicadores { get; } =
        NombresPasos.Select((n, i) => new IndicadorPaso(i + 1, n) { Estado = i == 0 ? "Actual" : "Pendiente" }).ToList();

    partial void OnPasoChanged(int value)
    {
        foreach (var ind in Indicadores)
            ind.Estado = ind.Numero - 1 < value ? "Hecho" : ind.Numero - 1 == value ? "Actual" : "Pendiente";
    }

    public ServicioInstalacion Instalacion => instalacion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoSiguiente), nameof(PuedeVolver), nameof(MuestraSiguiente))]
    public partial int Paso { get; set; }

    [ObservableProperty] public partial bool Cargando { get; set; } = true;
    [ObservableProperty] public partial string ResumenDispositivo { get; set; } = "";
    [ObservableProperty] public partial string Especificaciones { get; set; } = "";
    [ObservableProperty] public partial string Carpeta { get; set; } = "";
    [ObservableProperty] public partial string InfoCarpeta { get; set; } = "";
    [ObservableProperty] public partial string? ErrorCarpeta { get; set; }
    [ObservableProperty] public partial bool CarpetaBloqueada { get; set; }
    [ObservableProperty] public partial string TotalSeleccion { get; set; } = "";
    [ObservableProperty] public partial string? AvisoSeleccion { get; set; }
    [ObservableProperty] public partial bool InstalacionFallida { get; set; }

    public ObservableCollection<FilaAviso> Avisos { get; } = [];
    public ObservableCollection<OpcionComponente> Opciones { get; } = [];

    public bool PuedeVolver => Paso is > PasoBienvenida and < PasoInstalacion;
    public bool MuestraSiguiente => Paso != PasoInstalacion;

    public string TextoSiguiente => Paso switch
    {
        PasoBienvenida => "Empezar",
        PasoComponentes => "Instalar",
        PasoFin => "Empezar a usar Constantino",
        _ => "Siguiente",
    };

    private InformeRecomendacion? _recomendacion;

    public async Task IniciarAsync()
    {
        Cargando = true;
        var (eq, rec) = await equipo.ObtenerAnalisisAsync();
        _recomendacion = rec;
        ResumenDispositivo = rec.ResumenDispositivo;
        var gpu = eq.GpuPrincipal;
        Especificaciones = $"{eq.SistemaOperativo}\n{eq.Procesador} · {Formato.Gb(eq.RamGb)} de RAM\n" +
                           (gpu is null ? "Sin tarjeta NVIDIA" : $"{gpu.Nombre} · {Formato.Gb(gpu.VramTotalGb ?? 0)} de memoria");
        Avisos.Clear();
        // El aviso de OneDrive y de espacio se trata en el paso de la carpeta, con la carpeta elegida.
        foreach (var a in rec.Avisos.Where(a => a.Titulo is not ("La carpeta está en OneDrive" or "Poco espacio en disco")))
            Avisos.Add(FilaAviso.De(a));

        Carpeta = ajustes.CarpetaDatosEfectiva;
        CarpetaBloqueada = (await instalacion.InstaladorAsync()).Estado.Leer().Count > 0;
        ActualizarCarpeta();
        await CargarOpcionesAsync();
        Cargando = false;
    }

    partial void OnCarpetaChanged(string value) => ActualizarCarpeta();

    private void ActualizarCarpeta()
    {
        if (string.IsNullOrWhiteSpace(Carpeta))
        {
            ErrorCarpeta = "Elige una carpeta.";
            InfoCarpeta = "";
            return;
        }
        var disco = DetectorEquipo.Disco(Carpeta);
        InfoCarpeta = disco is null ? "No se puede usar esa ruta." : $"Quedan {Formato.Gb(disco.LibreGb)} libres en {disco.Unidad}";
        ErrorCarpeta = disco switch
        {
            null => "La ruta no es válida.",
            { EnOneDrive: true } => "Esa carpeta está dentro de OneDrive: se intentarían subir a la nube varios gigas. Elige otra.",
            _ => null,
        };
        ActualizarTotal();
    }

    [RelayCommand]
    private void Examinar()
    {
        var dialogo = new OpenFolderDialog { Title = "Carpeta de datos de Constantino", InitialDirectory = Directory.Exists(Carpeta) ? Carpeta : null };
        if (dialogo.ShowDialog() == true)
            Carpeta = Path.Combine(dialogo.FolderName, "Constantino");
    }

    private async Task CargarOpcionesAsync()
    {
        var instalador = await instalacion.InstaladorAsync();
        var rec = _recomendacion!;
        Opciones.Clear();
        foreach (var c in equipo.Catalogo.Componentes)
        {
            var r = rec.Para(c.Id);
            var instalado = c.SePuedeInstalar && instalador.EstaInstalado(c.Id);
            var bloqueado = c.GrupoExclusivo is null && r.Nivel == NivelRecomendacion.Imprescindible;
            var opcion = new OpcionComponente(c.Id, c.Nombre, c.Resumen, r.Motivo, r.Nivel, c.Tamano.Para(rec.UsaGpu),
                c.GrupoExclusivo, bloqueado, c.SePuedeInstalar, c.DisponibleEn, instalado, c.Consejo)
            {
                Seleccionado = instalado || (c.SePuedeInstalar && rec.Propuestos.Contains(c.Id)),
            };
            opcion.AlCambiar = AlCambiarSeleccion;
            Opciones.Add(opcion);
        }
        ActualizarTotal();
    }

    /// <summary>De cada grupo de alternativas (los modelos Base) tiene que quedar al menos uno.</summary>
    private void AlCambiarSeleccion(OpcionComponente opcion)
    {
        if (!opcion.Seleccionado && opcion.GrupoExclusivo is { } grupo
            && !Opciones.Any(o => o.GrupoExclusivo == grupo && o.Seleccionado))
        {
            opcion.Seleccionado = true;
            AvisoSeleccion = "Hace falta al menos uno de los modelos Base: sin él no se puede narrar ni clonar.";
            return;
        }
        AvisoSeleccion = null;
        ActualizarTotal();
    }

    private void ActualizarTotal()
    {
        var gb = Opciones.Where(o => o.Seleccionado && !o.YaInstalado).Sum(o => o.TamanoGb);
        var disco = string.IsNullOrWhiteSpace(Carpeta) ? null : DetectorEquipo.Disco(Carpeta);
        TotalSeleccion = gb == 0
            ? "No hay nada nuevo que instalar."
            : $"Se descargarán e instalarán unos {Formato.Gb(gb)}" + (disco is null ? "." : $" (quedan {Formato.Gb(disco.LibreGb)} libres en {disco.Unidad}).");
        if (disco is not null && disco.LibreGb < gb + 5)
            AvisoSeleccion ??= "No hay espacio suficiente en el disco para esta selección. Libera espacio o elige otra carpeta.";
    }

    [RelayCommand]
    private void Atras()
    {
        if (PuedeVolver)
            Paso--;
    }

    [RelayCommand]
    private async Task SiguienteAsync()
    {
        switch (Paso)
        {
            case PasoCarpeta:
                if (ErrorCarpeta is not null)
                    return;
                Directory.CreateDirectory(Carpeta);
                ajustes.Guardar(ajustes.Actual with { CarpetaDatos = Path.GetFullPath(Carpeta) });
                Paso++;
                break;
            case PasoComponentes:
                Paso = PasoInstalacion;
                await InstalarAsync();
                break;
            case PasoFin:
                Terminar();
                break;
            default:
                Paso++;
                break;
        }
    }

    [RelayCommand]
    private Task ReintentarAsync() => InstalarAsync();

    [RelayCommand]
    private void VolverASeleccion()
    {
        InstalacionFallida = false;
        Paso = PasoComponentes;
    }

    /// <summary>Sale sin instalar: el asistente volverá a aparecer en el próximo arranque.</summary>
    [RelayCommand]
    private void MasTarde() => navegacion.Navigate(typeof(PaginaInicio));

    private async Task InstalarAsync()
    {
        InstalacionFallida = false;
        var ids = Opciones.Where(o => o.Seleccionado && !o.YaInstalado).Select(o => o.Id).ToList();
        var ok = await instalacion.InstalarAsync(ids);
        if (ok)
        {
            Paso = PasoFin;
            await CargarOpcionesAsync();
        }
        else
        {
            InstalacionFallida = true;
        }
    }

    private void Terminar()
    {
        ajustes.Guardar(ajustes.Actual with { AsistenteCompletado = true });
        navegacion.Navigate(typeof(PaginaInicio));
    }
}
