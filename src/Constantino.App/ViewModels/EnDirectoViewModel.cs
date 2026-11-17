using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Motores;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>Un modo de la voz en directo, con su explicación.</summary>
public sealed record ModoEnDirecto(string Id, string Nombre, string Descripcion);

/// <summary>
/// Voz en directo (beta): hablas por el micrófono y sales con otra voz de la biblioteca. Te puedes oír en los
/// auriculares y, a la vez, mandar la voz a Teams o Zoom por la «Salida de Calíope» (VB-CABLE renombrado).
/// También se puede probar con una grabación, sin micrófono.
/// </summary>
public sealed partial class EnDirectoViewModel : ObservableObject
{
    private readonly ServicioEnDirecto _directo;
    private readonly ServicioVoces _voces;
    private readonly ServicioInstalacion _instalacion;
    private readonly INavigationService _navegacion;
    private readonly DispatcherTimer _reloj = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _cargado;

    public EnDirectoViewModel(ServicioEnDirecto directo, ServicioVoces voces, ServicioInstalacion instalacion, INavigationService navegacion)
    {
        _directo = directo;
        _voces = voces;
        _instalacion = instalacion;
        _navegacion = navegacion;
        _reloj.Tick += async (_, _) => await ActualizarEstadoAsync();
        Modo = Modos[0];
    }

    public ObservableCollection<OpcionVozMezcla> Voces { get; } = [];
    public ObservableCollection<DispositivoAudio> Microfonos { get; } = [];
    public ObservableCollection<DispositivoAudio> Auriculares { get; } = [];

    public IReadOnlyList<ModoEnDirecto> Modos { get; } =
    [
        new("calidad", T("Calidad"), T("Más natural. Necesita tarjeta gráfica NVIDIA. Retraso de algo más de medio segundo.")),
        new("ligero", T("Ligero"), T("Funciona con el procesador, con menos retraso (unos 0,3 s), y conserva mejor la zeta de España. Algo menos natural.")),
    ];

    [ObservableProperty] public partial bool Instalado { get; set; } = true;
    [ObservableProperty] public partial bool SinVoces { get; set; }
    [ObservableProperty] public partial bool SinMicrofono { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar))]
    public partial OpcionVozMezcla? Voz { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar))]
    public partial ModoEnDirecto Modo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar))]
    public partial DispositivoAudio? Microfono { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar))]
    public partial DispositivoAudio? Oido { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar))]
    public partial bool Oirme { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar))]
    public partial bool ATeams { get; set; }

    /// <summary>La salida de Calíope que ve el motor (el extremo donde se reproduce), si está instalada.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar), nameof(HayCable))]
    public partial DispositivoAudio? Cable { get; set; }

    public bool HayCable => Cable is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SalidaSinInstalar), nameof(SalidaSinRenombrar), nameof(SalidaLista))]
    public partial SalidaCaliope.Estado EstadoSalida { get; set; }

    public bool SalidaSinInstalar => EstadoSalida == SalidaCaliope.Estado.SinInstalar;
    public bool SalidaSinRenombrar => EstadoSalida == SalidaCaliope.Estado.SinRenombrar;
    public bool SalidaLista => EstadoSalida == SalidaCaliope.Estado.Lista;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar), nameof(Libre))]
    public partial bool Trabajando { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEmpezar), nameof(Libre))]
    public partial bool Encendido { get; set; }

    public bool Libre => !Trabajando && !Encendido;

    public bool PuedeEmpezar => Libre && Voz is not null && Modo is not null && (Oirme && Oido is not null || ATeams && Cable is not null);

    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string Detalles { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial double NivelEntrada { get; set; }
    [ObservableProperty] public partial double NivelSalida { get; set; }

    public async Task CargarAsync()
    {
        Instalado = await _directo.InstaladoAsync();
        var biblioteca = await _voces.BibliotecaAsync();
        var elegida = Voz?.Voz.Id;
        Voces.Clear();
        foreach (var v in biblioteca.Listar())
            Voces.Add(new OpcionVozMezcla(v, biblioteca.ReferenciaDe(v.Id)));
        Voz = Voces.FirstOrDefault(v => v.Voz.Id == elegida) ?? Voces.FirstOrDefault();
        SinVoces = Voces.Count == 0;
        EstadoSalida = SalidaCaliope.Comprobar();
        if (Instalado && !_cargado && !Encendido)
        {
            _cargado = true;
            await ActualizarDispositivosAsync();
        }
    }

    [RelayCommand]
    private async Task ActualizarDispositivosAsync()
    {
        if (Encendido)
            return;
        Error = null;
        Trabajando = true;
        Mensaje = T("Buscando micrófonos y salidas… (la primera vez tarda unos segundos)");
        try
        {
            var lista = (await _directo.DispositivosAsync()).Dispositivos;
            Rellenar(Microfonos, lista.Where(d => d.Entrada && !d.Cable), Microfono, v => Microfono = v);
            Rellenar(Auriculares, lista.Where(d => d.Salida && !d.Cable), Oido, v => Oido = v);
            Cable = lista.FirstOrDefault(d => d.Salida && d.Cable);
            SinMicrofono = Microfonos.Count == 0;
            EstadoSalida = SalidaCaliope.Comprobar();
            if (Cable is null)
                ATeams = false;
            // Sin tarjeta gráfica, el modo «calidad» no funciona: se propone el ligero.
            if (_directo.Dispositivo == "cpu" && Modo.Id == "calidad")
                Modo = Modos[1];
            Mensaje = "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
            Mensaje = "";
        }
        finally
        {
            Trabajando = false;
        }
    }

    private static void Rellenar(ObservableCollection<DispositivoAudio> coleccion, IEnumerable<DispositivoAudio> nuevos,
        DispositivoAudio? elegido, Action<DispositivoAudio?> elegir)
    {
        coleccion.Clear();
        foreach (var d in nuevos)
            coleccion.Add(d);
        elegir(coleccion.FirstOrDefault(d => d.Nombre == elegido?.Nombre)
               ?? coleccion.FirstOrDefault(d => d.PorDefecto) ?? coleccion.FirstOrDefault());
    }

    [RelayCommand]
    private Task EmpezarAsync() => ArrancarAsync(null);

    [RelayCommand]
    private async Task ProbarConArchivoAsync()
    {
        var dialogo = new OpenFileDialog
        {
            Title = T("Grabación para probar la voz en directo"),
            Filter = "Audio (*.wav;*.mp3;*.flac;*.ogg;*.m4a)|*.wav;*.mp3;*.flac;*.ogg;*.m4a|" + T("Todos los archivos") + "|*.*",
        };
        if (dialogo.ShowDialog() == true)
            await ArrancarAsync(dialogo.FileName);
    }

    private async Task ArrancarAsync(string? archivo)
    {
        if (Voz is null || (archivo is null && Microfono is null))
        {
            Error = archivo is null && Microfono is null ? T("No hay ningún micrófono. Conecta uno (mejor unos auriculares con micrófono) y pulsa «Buscar de nuevo».") : null;
            return;
        }
        var salidas = new List<DispositivoAudio>();
        if (ATeams && Cable is not null)
            salidas.Add(Cable);
        if (Oirme && Oido is not null)
            salidas.Add(Oido);
        if (salidas.Count == 0)
            return;
        Error = null;
        Trabajando = true;
        try
        {
            Mensaje = T("Preparando la voz en directo (la primera vez, medio minuto)…");
            var progreso = new Progress<EventoMotor>(e => Mensaje = e.Mensaje + "…");
            await _directo.CargarAsync(Modo.Id, Voz.Referencia, progreso);
            await _directo.IniciarAsync(archivo is null ? Microfono : null, archivo, salidas);
            Encendido = true;
            Mensaje = archivo is null
                ? ATeams ? T("En marcha: habla. En Teams o Zoom, elige «{0}» como micrófono.", SalidaCaliope.NombreMicrofono) : T("En marcha: habla.")
                : T("Convirtiendo «{0}» como si hablaras en directo…", Path.GetFileName(archivo));
            _reloj.Start();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
            Mensaje = "";
        }
        finally
        {
            Trabajando = false;
        }
    }

    [RelayCommand]
    private async Task PararAsync()
    {
        _reloj.Stop();
        try
        {
            var estado = await _directo.PararAsync();
            Mensaje = estado.Bloques > 0 ? T("Parado.") : "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Encendido = false;
            NivelEntrada = NivelSalida = 0;
        }
    }

    private async Task ActualizarEstadoAsync()
    {
        if (!Encendido)
            return;
        try
        {
            var e = await _directo.EstadoAsync();
            NivelEntrada = Nivel(e.NivelEntradaDb);
            NivelSalida = Nivel(e.NivelSalidaDb);
            Detalles = T("Retraso aproximado: {0:0.0} s · cada bloque de {1:0} ms se convierte en {2:0} ms (máx. {3:0}) · bloques lentos: {4} · cortes: {5}",
                e.RetrasoMs / 1000, e.BloqueMs, e.ProcesoMs, e.ProcesoMaxMs, e.Lentos, e.Cortes);
            if (e.Error is not null)
            {
                Error = T("La voz en directo se ha parado: {0}", e.Error);
                await PararAsync();
            }
            else if (!e.Activo)
            {
                await PararAsync();
                Mensaje = e.FinArchivo ? T("Terminada la grabación de prueba.") : T("Parado.");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _reloj.Stop();
            Encendido = false;
            Error = ServicioMotor.Explicar(e);
        }
    }

    /// <summary>De −60 dB (nada) a 0 dB (lleno), para la barra de nivel.</summary>
    private static double Nivel(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    [RelayCommand]
    private async Task PrepararSalidaAsync()
    {
        Error = null;
        Trabajando = true;
        Mensaje = T("Descargando VB-CABLE de la web de VB-Audio… Después Windows te pedirá permiso: en la ventana de VB-Audio pulsa «Install Driver».");
        try
        {
            var rutas = (await _instalacion.InstaladorAsync()).Rutas;
            EstadoSalida = await SalidaCaliope.PrepararAsync(rutas, ServicioInstalacion.Descargador, CancellationToken.None);
            Mensaje = EstadoSalida switch
            {
                SalidaCaliope.Estado.Lista => T("Listo: en Teams o Zoom ya aparece «{0}» como micrófono.", SalidaCaliope.NombreMicrofono),
                SalidaCaliope.Estado.SinRenombrar => T("VB-CABLE está instalado, pero Windows aún no lo muestra. Reinicia el equipo y pulsa «Ponerle el nombre de Calíope»."),
                _ => T("VB-CABLE no se ha instalado. Si cerraste su ventana sin pulsar «Install Driver», vuelve a intentarlo. Si ya lo hiciste, reinicia el equipo."),
            };
            _cargado = false;
            await ActualizarDispositivosAsync();
        }
        catch (OperationCanceledException e)
        {
            Mensaje = e.Message;
        }
        catch (Exception e)
        {
            Error = T("No se ha podido preparar la salida: {0}", e.Message);
            Mensaje = "";
        }
        finally
        {
            Trabajando = false;
        }
    }

    [RelayCommand]
    private async Task RenombrarSalidaAsync()
    {
        Error = null;
        Trabajando = true;
        try
        {
            EstadoSalida = await SalidaCaliope.RenombrarAsync(CancellationToken.None);
            Mensaje = EstadoSalida == SalidaCaliope.Estado.Lista
                ? T("Listo: en Teams o Zoom ya aparece «{0}» como micrófono.", SalidaCaliope.NombreMicrofono)
                : T("No se ha podido cambiar el nombre. Reinicia el equipo y vuelve a intentarlo.");
            await ActualizarDispositivosAsync();
        }
        catch (OperationCanceledException e)
        {
            Mensaje = e.Message;
        }
        finally
        {
            Trabajando = false;
        }
    }

    [RelayCommand]
    private void AbrirWebVbCable() => Process.Start(new ProcessStartInfo(SalidaCaliope.WebVbCable) { UseShellExecute = true });

    [RelayCommand]
    private void IrAComponentes() => _navegacion.Navigate(typeof(PaginaComponentes));
}
