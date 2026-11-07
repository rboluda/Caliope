using System.Collections.ObjectModel;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Catalogo;
using Constantino.Core.Instalaciones;
using Constantino.Core.Motores;
using Constantino.Core.Recomendaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>Un componente: lo que es, su estado y lo que se puede hacer con él (instalar, reparar, desinstalar).</summary>
public sealed partial class TarjetaComponente(Componente c, Recomendacion r, double gb, string necesita) : ObservableObject
{
    public string Id { get; } = c.Id;
    public string Nombre { get; } = c.Nombre;
    public string Resumen { get; } = c.Resumen;
    public string Detalle { get; } = c.Detalle;
    public string? Consejo { get; } = c.Consejo;
    public string? Tecnico { get; } = c.Tecnico;
    public double Gb { get; } = gb;
    public string Tamano { get; } = Formato.Gb(gb);
    public string Habilita { get; } = string.Join(" · ", c.Habilita);
    public string Necesita { get; } = necesita;
    public IReadOnlyList<Licencia> Licencias { get; } = c.Licencias;
    public NivelRecomendacion Nivel { get; } = r.Nivel;
    public string? DisponibleEn { get; } = c.SePuedeInstalar ? null : c.DisponibleEn ?? T("Llegará en una próxima versión.");

    // Antes de instalarlo, la etiqueta y el motivo son la recomendación; una vez instalado, dicen su estado
    // (si no, «Recomendado» parece pedir que se instale algo que ya está).
    [ObservableProperty] public partial string Etiqueta { get; set; } = Presentacion.Etiqueta(r.Nivel);
    [ObservableProperty] public partial ControlAppearance Apariencia { get; set; } = Presentacion.Apariencia(r.Nivel);
    [ObservableProperty] public partial string Motivo { get; set; } = r.Motivo;
    [ObservableProperty] public partial string Estado { get; set; } = "";
    [ObservableProperty] public partial EstadoComponente EstadoActual { get; set; }
    [ObservableProperty] public partial bool PuedeInstalar { get; set; }
    [ObservableProperty] public partial string TextoInstalar { get; set; } = T("Instalar");
    [ObservableProperty] public partial bool PuedeReparar { get; set; }
    [ObservableProperty] public partial bool PuedeDesinstalar { get; set; }

    /// <summary>Instalado y utilizable (aunque haya una versión nueva).</summary>
    public bool Instalado => EstadoActual is EstadoComponente.Instalado or EstadoComponente.Actualizable;

    public void Aplicar(EstadoComponente estado)
    {
        EstadoActual = estado;
        OnPropertyChanged(nameof(Instalado));
        (Etiqueta, Apariencia, Motivo) = estado switch
        {
            EstadoComponente.Instalado => (T("Instalado"), ControlAppearance.Success, T("Instalado y listo para usar.")),
            EstadoComponente.Actualizable => (T("Actualizable"), ControlAppearance.Info, T("Instalado. Hay una versión nueva: pulsa «Actualizar».")),
            EstadoComponente.Danado => (T("Dañado"), ControlAppearance.Danger, T("Faltan archivos: pulsa «Reparar» para volver a instalarlo.")),
            _ => (Presentacion.Etiqueta(r.Nivel), Presentacion.Apariencia(r.Nivel), r.Motivo),
        };
        Estado = estado switch
        {
            EstadoComponente.NoDisponible => T("Próximamente"),
            EstadoComponente.NoInstalado => T("No instalado"),
            EstadoComponente.Instalado => T("Instalado"),
            EstadoComponente.Danado => T("Dañado: faltan archivos"),
            EstadoComponente.Actualizable => T("Hay una versión nueva"),
            _ => "",
        };
        PuedeInstalar = estado is EstadoComponente.NoInstalado or EstadoComponente.Actualizable;
        TextoInstalar = estado == EstadoComponente.Actualizable ? T("Actualizar") : T("Instalar");
        PuedeReparar = estado is EstadoComponente.Instalado or EstadoComponente.Danado;
        PuedeDesinstalar = estado is EstadoComponente.Instalado or EstadoComponente.Danado or EstadoComponente.Actualizable;
    }
}

/// <summary>
/// Una fila de una caja: el componente y una nota («También para: Doblar vídeos», «La tuya»…). Al pulsarla se
/// despliega su detalle; el mismo componente puede salir en varias cajas, cada una con su fila.
/// </summary>
public sealed partial class PiezaVista(TarjetaComponente tarjeta, string? nota = null, bool opcional = false) : ObservableObject
{
    public TarjetaComponente Tarjeta { get; } = tarjeta;
    public bool Opcional { get; } = opcional;

    [ObservableProperty] public partial string? Nota { get; set; } = nota;
    [ObservableProperty] public partial bool Abierta { get; set; }

    [RelayCommand]
    private void Alternar() => Abierta = !Abierta;
}

/// <summary>Una caja del bento: una función (o la base, o los motores alternativos) con sus piezas y si está lista.</summary>
public sealed partial class CajaComponentes(string id, string titulo, string descripcion, SymbolRegular icono) : ObservableObject
{
    public string Id { get; } = id;
    public string Titulo { get; } = titulo;
    public string Descripcion { get; } = descripcion;
    public SymbolRegular Icono { get; } = icono;
    public ObservableCollection<PiezaVista> Piezas { get; } = [];

    /// <summary>Alternativas entre sí (la base: las voces; basta con una).</summary>
    public ObservableCollection<PiezaVista> Alternativas { get; } = [];

    /// <summary>Lo que hay que instalar para tenerla lista (con lo recomendado si se elige entre alternativas).</summary>
    public List<string> Faltan { get; } = [];

    [ObservableProperty] public partial string Estado { get; set; } = "";
    [ObservableProperty] public partial ControlAppearance Apariencia { get; set; } = ControlAppearance.Secondary;
    [ObservableProperty] public partial bool Lista { get; set; }
    [ObservableProperty] public partial bool PuedeCompletar { get; set; }
    [ObservableProperty] public partial string TextoCompletar { get; set; } = "";

    public bool HayAlternativas => Alternativas.Count > 0;
}

public sealed record GuiaVista(string Consejo, IReadOnlyList<FichaMotor> Fichas);

public sealed partial class ComponentesViewModel : ObservableObject
{
    private readonly ServicioEquipo _equipo;
    private readonly INavigationService _navegacion;
    private readonly List<TarjetaComponente> _tarjetas = [];
    private bool _usaGpu;

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

    [ObservableProperty] public partial string Instalados { get; set; } = "";
    [ObservableProperty] public partial string Ocupa { get; set; } = "";
    [ObservableProperty] public partial string FuncionesListas { get; set; } = "";

    [ObservableProperty] public partial CajaComponentes? Base { get; set; }

    /// <summary>La primera función va junto a la base; el resto, debajo.</summary>
    [ObservableProperty] public partial CajaComponentes? Primera { get; set; }

    public ObservableCollection<CajaComponentes> Funciones { get; } = [];
    [ObservableProperty] public partial CajaComponentes? Motores { get; set; }
    [ObservableProperty] public partial GuiaVista? Guia { get; set; }

    public Task CargarAsync() => MostrarAsync();

    private async Task MostrarAsync()
    {
        Cargando = true;
        var (equipo, rec) = await _equipo.ObtenerAnalisisAsync();
        var catalogo = _equipo.Catalogo;
        _usaGpu = rec.UsaGpu;
        ResumenDispositivo = rec.ResumenDispositivo;
        Guia = new GuiaVista(GuiaMotores.Consejo(equipo), GuiaMotores.Fichas);

        _tarjetas.Clear();
        _tarjetas.AddRange(catalogo.Componentes.Select(c => new TarjetaComponente(c, rec.Para(c.Id), c.Tamano.Para(rec.UsaGpu), Necesita(catalogo, c))));
        TarjetaComponente Tarjeta(string id) => _tarjetas.First(t => t.Id == id);
        var funciones = FuncionesCaliope.Todas;

        // Dónde más se usa un componente: así se ve qué pasa si se quita.
        string? Compartida(string id, string? funcion) =>
            funciones.Where(f => f.Id != funcion && f.Necesita.Contains(id)).Select(f => f.Nombre).ToList() is { Count: > 0 } otras
                ? T("También para: {0}", string.Join(", ", otras))
                : null;

        var cajaBase = new CajaComponentes("base", T("La base"), T("Imprescindible: sin esto no funciona nada."), SymbolRegular.Cube24);
        foreach (var id in FuncionesCaliope.Base)
            cajaBase.Piezas.Add(new PiezaVista(Tarjeta(id)));
        foreach (var c in catalogo.Componentes.Where(c => c.GrupoExclusivo == FuncionesCaliope.GrupoVoces))
            cajaBase.Alternativas.Add(new PiezaVista(Tarjeta(c.Id)));
        Base = cajaBase;

        Funciones.Clear();
        foreach (var f in funciones)
        {
            var caja = new CajaComponentes(f.Id, f.Nombre, f.Descripcion, Icono(f.Id));
            foreach (var id in f.Necesita)
                caja.Piezas.Add(new PiezaVista(Tarjeta(id), Compartida(id, f.Id)));
            foreach (var id in f.Opcionales)
                caja.Piezas.Add(new PiezaVista(Tarjeta(id), T("Opcional"), opcional: true));
            Funciones.Add(caja);
        }
        Primera = Funciones.FirstOrDefault();
        if (Primera is not null)
            Funciones.Remove(Primera);

        var motores = new CajaComponentes("motores", T("Otros motores para narrar"),
            T("Opcionales: sustituyen al motor principal cuando los eliges en Narrar. No hace falta probarlos todos."), SymbolRegular.ArrowSwap24);
        foreach (var id in FuncionesCaliope.MotoresAlternativos)
            motores.Piezas.Add(new PiezaVista(Tarjeta(id), Compartida(id, null)));
        Motores = motores;

        await RefrescarEstadosAsync();
        Cargando = false;
    }

    private static SymbolRegular Icono(string funcion) => funcion switch
    {
        "narrar" => SymbolRegular.BookOpen24,
        "disenar" => SymbolRegular.Wand24,
        "doblar" => SymbolRegular.VideoClip24,
        "actas" => SymbolRegular.DocumentText24,
        _ => SymbolRegular.Apps24,
    };

    private IEnumerable<CajaComponentes> CajasDeFunciones() => Primera is null ? Funciones : Funciones.Prepend(Primera);

    private async Task RefrescarEstadosAsync()
    {
        var instalador = await Instalacion.InstaladorAsync();
        foreach (var t in _tarjetas)
            t.Aplicar(instalador.EstadoDe(t.Id));
        if (Base is null)
            return;

        // La base: lo fijo y una de las voces (si no hay ninguna, la recomendada para este equipo).
        var voces = Base.Alternativas.Select(p => p.Tarjeta).ToList();
        var vozInstalada = voces.FirstOrDefault(t => t.Instalado);
        var vozPropuesta = vozInstalada ?? voces.FirstOrDefault(t => t.Nivel == NivelRecomendacion.Imprescindible) ?? voces.FirstOrDefault();
        foreach (var p in Base.Alternativas)
        {
            p.Nota = p.Tarjeta == vozInstalada ? T("La tuya")
                : vozInstalada is null && p.Tarjeta == vozPropuesta ? T("Recomendada para tu equipo")
                : p.Tarjeta.Nivel == NivelRecomendacion.NoRecomendado ? T("No recomendada para tu equipo")
                : null;
        }
        var faltaBase = Base.Piezas.Where(p => !p.Tarjeta.Instalado).Select(p => p.Tarjeta).ToList();
        if (vozInstalada is null && vozPropuesta is not null)
            faltaBase.Add(vozPropuesta);
        Actualizar(Base, faltaBase, baseLista: true);

        var baseLista = faltaBase.Count == 0;
        foreach (var caja in CajasDeFunciones())
            Actualizar(caja, caja.Piezas.Where(p => !p.Opcional && !p.Tarjeta.Instalado).Select(p => p.Tarjeta).ToList(), baseLista);

        // Los motores alternativos no «faltan»: solo se dice cuántos hay.
        if (Motores is not null)
        {
            var n = Motores.Piezas.Count(p => p.Tarjeta.Instalado);
            Motores.Estado = n == 0 ? T("Ninguno instalado") : T("{0} de {1} instalados", n, Motores.Piezas.Count);
            Motores.Apariencia = n == 0 ? ControlAppearance.Secondary : ControlAppearance.Success;
        }

        var utiles = _tarjetas.Where(t => t.DisponibleEn is null).ToList();
        Instalados = T("{0} de {1}", utiles.Count(t => t.Instalado), utiles.Count);
        Ocupa = Formato.Gb(_tarjetas.Where(t => t.Instalado).Sum(t => t.Gb));
        var cajas = CajasDeFunciones().ToList();
        FuncionesListas = T("{0} de {1}", cajas.Count(c => c.Lista), cajas.Count);
    }

    private void Actualizar(CajaComponentes caja, List<TarjetaComponente> faltan, bool baseLista)
    {
        caja.Faltan.Clear();
        caja.Faltan.AddRange(faltan.Select(t => t.Id));
        caja.Lista = faltan.Count == 0 && baseLista;
        var gb = Formato.Gb(faltan.Sum(t => t.Gb));
        (caja.Estado, caja.Apariencia) = faltan.Count == 0
            ? baseLista ? (T("Lista"), ControlAppearance.Success) : (T("Primero, la base"), ControlAppearance.Caution)
            : (T("Te falta: {0} · {1}", string.Join(", ", faltan.Select(t => t.Nombre)), gb), ControlAppearance.Caution);
        caja.PuedeCompletar = faltan.Count > 0 && faltan.All(t => t.DisponibleEn is null);
        caja.TextoCompletar = faltan.Count == 1 ? T("Instalar ({0})", gb) : T("Instalar lo que falta ({0})", gb);
    }

    private static string Necesita(CatalogoComponentes catalogo, Componente c)
    {
        var texto = c.DependeDe.Count == 0
            ? T("Nada más.")
            : string.Join(", ", c.DependeDe.Select(id => catalogo[id].Nombre)) + ".";
        if (c.GrupoExclusivo is not null)
        {
            var otras = catalogo.Componentes.Where(o => o.GrupoExclusivo == c.GrupoExclusivo && o.Id != c.Id);
            texto += " " + T("Es alternativa a: {0}.", string.Join(", ", otras.Select(o => o.Nombre)));
        }
        return texto;
    }

    /// <summary>Instala lo que le falta a una caja (con sus dependencias), en una sola tanda.</summary>
    [RelayCommand]
    private async Task CompletarAsync(CajaComponentes caja)
    {
        if (caja.Faltan.Count == 0)
            return;
        if (await ConfirmarPlanAsync(caja.Faltan, T("Completar «{0}»", caja.Titulo), T("Instalar")))
            await EjecutarAsync([.. caja.Faltan], forzar: false);
    }

    [RelayCommand]
    private async Task InstalarAsync(TarjetaComponente tarjeta)
    {
        if (await ConfirmarPlanAsync([tarjeta.Id], $"{tarjeta.TextoInstalar} {tarjeta.Nombre}", tarjeta.TextoInstalar))
            await EjecutarAsync([tarjeta.Id], forzar: false);
    }

    private async Task<bool> ConfirmarPlanAsync(IReadOnlyList<string> ids, string titulo, string accion)
    {
        var plan = await Instalacion.PlanAsync(ids);
        var lista = string.Join("\n", plan.Select(c => $"• {c.Nombre} ({Formato.Gb(c.Tamano.Para(_usaGpu))})"));
        var total = Formato.Gb(plan.Sum(c => c.Tamano.Para(_usaGpu)));
        return await ConfirmarAsync(titulo, T("Se descargará e instalará:\n\n{0}\n\nEn total, unos {1}.", lista, total), accion);
    }

    [RelayCommand]
    private async Task RepararAsync(TarjetaComponente tarjeta)
    {
        if (!await ConfirmarAsync(T("Reparar {0}", tarjeta.Nombre),
                T("Se volverá a instalar este componente. Lo ya descargado se aprovecha cuando es posible."), T("Reparar")))
            return;
        await EjecutarAsync([tarjeta.Id], forzar: true);
    }

    [RelayCommand]
    private async Task DesinstalarAsync(TarjetaComponente tarjeta)
    {
        if (!await ConfirmarAsync(T("Desinstalar {0}", tarjeta.Nombre),
                T("Se borrará de tu equipo y se liberarán unos {0}. Podrás volver a instalarlo cuando quieras.", tarjeta.Tamano), T("Desinstalar")))
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
            CloseButtonText = T("Cancelar"),
        };
        return await caja.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
