using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Constantino.Core.Catalogo;
using Constantino.Core.Instalaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using static Constantino.Core.Idioma;

namespace Constantino.App.Servicios;

public enum EstadoFila
{
    Pendiente,
    Instalando,
    Instalado,
    Error,
}

/// <summary>Una línea de la lista de progreso: un componente del plan en curso.</summary>
public sealed partial class FilaInstalacion(string id, string nombre) : ObservableObject
{
    public string Id { get; } = id;
    public string Nombre { get; } = nombre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarBarra))]
    public partial EstadoFila Estado { get; set; }

    [ObservableProperty] public partial string Detalle { get; set; } = T("Pendiente");

    /// <summary>Avance de su descarga en segundo plano (de 0 a 1), o null si no hay ninguna.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarBarra))]
    public partial double? Fraccion { get; set; }

    public bool MostrarBarra => Estado == EstadoFila.Instalando && Fraccion is not null;
}

/// <summary>
/// Puente entre el instalador del núcleo y la interfaz: una instalación a la vez, con su progreso
/// observable desde cualquier página (asistente o Componentes).
/// </summary>
public sealed partial class ServicioInstalacion(ServicioEquipo equipo, ServicioAjustes ajustes) : ObservableObject
{
    private const int MaxLineas = 400;
    private static readonly HttpClient Http = CrearHttp();
    private CancellationTokenSource? _cancelacion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarBarra), nameof(MostrarRueda))]
    public partial bool Ocupado { get; set; }

    [ObservableProperty] public partial string Titulo { get; set; } = "";
    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial double Fraccion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarBarra), nameof(MostrarRueda))]
    public partial bool Indeterminado { get; set; }

    /// <summary>
    /// Barra con porcentaje cuando se puede medir; rueda giratoria cuando no.
    /// (La barra indeterminada de WPF-UI no llegaba a verse.)
    /// </summary>
    public bool MostrarBarra => Ocupado && !Indeterminado;
    public bool MostrarRueda => Ocupado && Indeterminado;
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? DetalleError { get; set; }

    public ObservableCollection<FilaInstalacion> Filas { get; } = [];
    public ObservableCollection<string> Lineas { get; } = [];

    /// <summary>Se lanza al terminar una instalación o desinstalación, para refrescar estados.</summary>
    public event EventHandler? EstadosCambiados;

    public CatalogoComponentes Catalogo => equipo.Catalogo;

    private readonly HashSet<string> _carpetasRevisadas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Si la carpeta de datos se ha reorganizado al arrancar (versiones anteriores lo guardaban todo suelto).</summary>
    public ResultadoMigracion? Migracion { get; private set; }

    /// <summary>El instalador se crea con la carpeta y el dispositivo actuales (pueden cambiar en el asistente).</summary>
    public async Task<Instalador> InstaladorAsync()
    {
        var (_, rec) = await equipo.ObtenerAnalisisAsync();
        var carpeta = ajustes.CarpetaDatosEfectiva;
        // La primera vez que se usa una carpeta en esta sesión se pasa a la estructura clara (y se deja su LÉEME).
        if (_carpetasRevisadas.Add(carpeta))
            Migracion = await Task.Run(() => EstructuraDatos.Migrar(carpeta));
        return new Instalador(Catalogo, new RutasDatos(carpeta, rec.UsaGpu), new EjecutorProcesos(), new Descargador(Http));
    }

    public async Task<EstadoComponente> EstadoDeAsync(string id) => (await InstaladorAsync()).EstadoDe(id);

    public async Task<IReadOnlyList<Componente>> PlanAsync(IEnumerable<string> ids, bool forzar = false) =>
        (await InstaladorAsync()).Plan(ids, forzar);

    /// <summary>Instala; devuelve true si todo fue bien. Los errores quedan en <see cref="Error"/>.</summary>
    public async Task<bool> InstalarAsync(IEnumerable<string> ids, bool forzar = false)
    {
        if (Ocupado)
            return false;
        var instalador = await InstaladorAsync();
        var plan = instalador.Plan(ids, forzar);

        Ocupado = true;
        Error = null;
        DetalleError = null;
        Lineas.Clear();
        Filas.Clear();
        foreach (var c in plan)
            Filas.Add(new FilaInstalacion(c.Id, c.Nombre));
        Titulo = plan.Count == 0 ? T("Todo lo elegido ya está instalado") : T("Instalando {0} componente(s)", plan.Count);
        _cancelacion = new CancellationTokenSource();

        var despachador = Application.Current.Dispatcher;
        var progreso = new ProgresoSincrono<EventoInstalacion>(e => despachador.BeginInvoke(() => Aplicar(e)));
        Exception? fallo = null;
        try
        {
            await Task.Run(() => instalador.InstalarAsync(plan.Select(c => c.Id), progreso, _cancelacion.Token, forzar));
        }
        catch (Exception e)
        {
            fallo = e;
        }
        // Los avisos de progreso llegan encolados: se aplican todos antes de mostrar el resultado final.
        await despachador.InvokeAsync(() => { }, DispatcherPriority.Background);

        Ocupado = false;
        Indeterminado = false;
        _cancelacion.Dispose();
        _cancelacion = null;
        EstadosCambiados?.Invoke(this, EventArgs.Empty);

        switch (fallo)
        {
            case null:
                Titulo = T("Instalación terminada");
                Mensaje = T("Todo se ha instalado correctamente.");
                Fraccion = 1;
                return true;
            case OperationCanceledException:
                MarcarFilaActual(T("Cancelado"));
                Titulo = T("Instalación cancelada");
                Error = T("Has cancelado la instalación. Lo descargado se aprovechará si vuelves a intentarlo.");
                return false;
            case InstalacionFallidaException e:
                MarcarFilaActual(T("Error"), e.ComponenteId);
                Titulo = T("La instalación no se ha completado");
                Error = e.Message;
                DetalleError = e.Detalle;
                return false;
            case HttpRequestException or IOException or UnauthorizedAccessException:
                MarcarFilaActual(T("Error"));
                Titulo = T("La instalación no se ha completado");
                Error = fallo is HttpRequestException
                    ? T("No se ha podido descargar: {0}. Comprueba la conexión a internet y vuelve a intentarlo.", fallo.Message)
                    : fallo.Message;
                return false;
            default:
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fallo);
                return false;
        }
    }

    public void Cancelar() => _cancelacion?.Cancel();

    /// <summary>Desinstala; devuelve el mensaje de error o null si fue bien.</summary>
    public async Task<string?> DesinstalarAsync(string id)
    {
        try
        {
            var instalador = await InstaladorAsync();
            await Task.Run(() => instalador.Desinstalar(id));
            return null;
        }
        catch (ComponenteEnUsoException e)
        {
            return e.Message;
        }
        finally
        {
            EstadosCambiados?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task AbrirRegistroAsync()
    {
        var ruta = (await InstaladorAsync()).ArchivoRegistro;
        if (File.Exists(ruta))
            Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });
    }

    private void Aplicar(EventoInstalacion e)
    {
        var fila = Filas.FirstOrDefault(f => f.Id == e.ComponenteId);
        switch (e.Tipo)
        {
            case TipoEvento.InicioComponente:
                if (fila is not null)
                {
                    fila.Estado = EstadoFila.Instalando;
                    fila.Detalle = T("Instalando…");
                }
                break;
            // Lo que va en segundo plano (descargas de modelos) solo se muestra en su fila, con su propia barra; el
            // mensaje y la barra de abajo son para lo que se está instalando en primer plano.
            case TipoEvento.Paso:
                if (!e.SegundoPlano)
                {
                    Mensaje = e.TotalPasos > 1 ? T("{0} (paso {1} de {2})", e.Mensaje, e.Paso, e.TotalPasos) : e.Mensaje;
                    Indeterminado = true;
                }
                if (fila is not null)
                {
                    fila.Detalle = e.Mensaje;
                    fila.Fraccion = e.SegundoPlano ? 0 : null;
                }
                break;
            case TipoEvento.Progreso:
                if (!e.SegundoPlano)
                {
                    Mensaje = e.Mensaje;
                    Indeterminado = e.Fraccion is null;
                    Fraccion = e.Fraccion ?? 0;
                }
                else if (fila is not null)
                {
                    fila.Detalle = e.Mensaje;
                    fila.Fraccion = e.Fraccion ?? 0;
                }
                break;
            case TipoEvento.Linea:
                Lineas.Add(e.Mensaje);
                while (Lineas.Count > MaxLineas)
                    Lineas.RemoveAt(0);
                break;
            case TipoEvento.ComponenteCompletado:
                if (fila is not null)
                {
                    fila.Estado = EstadoFila.Instalado;
                    fila.Detalle = T("Instalado");
                    fila.Fraccion = null;
                }
                break;
        }
    }

    /// <summary>
    /// Marca la fila que ha fallado (o la primera en curso) y deja en espera las descargas que seguían en segundo
    /// plano: se pararon y continuarán al reintentar.
    /// </summary>
    private void MarcarFilaActual(string detalle, string? componenteId = null)
    {
        var fallida = Filas.FirstOrDefault(f => f.Id == componenteId) ?? Filas.FirstOrDefault(f => f.Estado == EstadoFila.Instalando);
        foreach (var fila in Filas.Where(f => f.Estado == EstadoFila.Instalando || f == fallida))
        {
            fila.Fraccion = null;
            if (fila == fallida)
            {
                fila.Estado = EstadoFila.Error;
                fila.Detalle = detalle;
            }
            else
            {
                fila.Estado = EstadoFila.Pendiente;
                fila.Detalle = T("Detenido: seguirá al reintentar");
            }
        }
    }

    private static HttpClient CrearHttp()
    {
        // Sin límite de tiempo global: hay descargas de varios GB. La cancelación la controla el usuario.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Constantino/0.1");
        return http;
    }
}
