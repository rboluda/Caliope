using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Doblaje;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>Una frase del doblaje en la lista: el original, la traducción (editable) y si cabe en su tiempo.</summary>
public sealed partial class FilaFrase(FraseDoblaje f, string archivo, Action<FilaFrase> alCambiar) : ObservableObject
{
    public int Id { get; } = f.Id;
    public string Tiempo { get; } = TimeSpan.FromSeconds(f.Inicio).ToString(@"m\:ss");
    public string Original { get; } = f.Original;
    public string Archivo { get; } = archivo;

    [ObservableProperty] public partial string Traduccion { get; set; } = f.Traduccion;
    [ObservableProperty] public partial string Estado { get; set; } = TextoEncaje(f);
    [ObservableProperty] public partial ControlAppearance Apariencia { get; set; } = AparienciaDe(f.Encaje);
    [ObservableProperty] public partial bool Generada { get; set; } = f.Encaje != EncajeFrase.SinGenerar && File.Exists(archivo);

    partial void OnTraduccionChanged(string value) => alCambiar(this);

    public void Actualizar(FraseDoblaje f)
    {
        Estado = TextoEncaje(f);
        Apariencia = AparienciaDe(f.Encaje);
        Generada = f.Encaje != EncajeFrase.SinGenerar && File.Exists(Archivo);
    }

    private static string TextoEncaje(FraseDoblaje f) => f.Encaje switch
    {
        EncajeFrase.Cabe => T("Cabe"),
        EncajeFrase.Justa => T("Justa ({0:0.00}×)", f.Tempo),
        EncajeFrase.NoCabe => T("No cabe"),
        _ => T("Sin doblar"),
    };

    private static ControlAppearance AparienciaDe(EncajeFrase e) => e switch
    {
        EncajeFrase.Cabe => ControlAppearance.Success,
        EncajeFrase.Justa => ControlAppearance.Caution,
        EncajeFrase.NoCabe => ControlAppearance.Danger,
        _ => ControlAppearance.Secondary,
    };
}

public sealed record OpcionDoblaje(string Id, string Nombre);

public sealed partial class DoblarViewModel(ServicioDoblaje doblaje, ServicioVoces voces, ServicioReproductor reproductor,
    INavigationService navegacion) : ObservableObject
{
    private ProyectoDoblaje? _proyecto;
    private AlmacenDoblajes? _almacen;
    private CancellationTokenSource? _cancelacion;
    private bool _cargando;

    public ServicioReproductor Reproductor { get; } = reproductor;
    public ObservableCollection<OpcionDoblaje> Proyectos { get; } = [];
    public ObservableCollection<OpcionVoz> Voces { get; } = [];
    public ObservableCollection<FilaFrase> Frases { get; } = [];

    [ObservableProperty] public partial string? Faltan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayProyecto))]
    public partial OpcionDoblaje? ProyectoSeleccionado { get; set; }

    public bool HayProyecto => ProyectoSeleccionado is not null;

    [ObservableProperty] public partial string? VozPronunciacion { get; set; }
    [ObservableProperty] public partial bool VozOriginalDeFondo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Ocupado { get; set; }

    public bool Libre => !Ocupado;

    [ObservableProperty] public partial string? Mensaje { get; set; }
    [ObservableProperty] public partial double Fraccion { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Resumen { get; set; }
    [ObservableProperty] public partial string? VideoExportado { get; set; }

    public async Task CargarAsync()
    {
        var faltan = await doblaje.FaltanAsync();
        Faltan = faltan.Count == 0 ? null : T("Para doblar vídeos falta instalar: {0}.", string.Join(", ", faltan));
        _almacen = await doblaje.AlmacenAsync();

        var biblioteca = await voces.BibliotecaAsync();
        Voces.Clear();
        foreach (var v in biblioteca.Listar().Where(v => v.Idioma == "Spanish"))
            Voces.Add(new OpcionVoz(v.Id, v.Nombre));

        var actual = ProyectoSeleccionado?.Id;
        Proyectos.Clear();
        foreach (var p in _almacen.Listar())
            Proyectos.Add(new OpcionDoblaje(p.Id, p.Nombre));
        ProyectoSeleccionado = Proyectos.FirstOrDefault(p => p.Id == actual) ?? Proyectos.FirstOrDefault();
    }

    partial void OnProyectoSeleccionadoChanged(OpcionDoblaje? value) => Mostrar(value is null ? null : _almacen?.Abrir(value.Id));

    private void Mostrar(ProyectoDoblaje? p)
    {
        _cargando = true;
        _proyecto = p;
        Frases.Clear();
        // El botón «Ver el vídeo doblado» aparece siempre que exista, también al volver a abrir el proyecto.
        VideoExportado = p is not null && _almacen is not null && File.Exists(_almacen.Exportado(p, "mp4")) ? _almacen.Exportado(p, "mp4") : null;
        if (p is not null && _almacen is not null)
        {
            foreach (var f in p.ListaFrases)
                Frases.Add(new FilaFrase(f, Path.Combine(_almacen.Frases(p.Id), $"{f.Id:000}.wav"), TraduccionCambiada));
            VozOriginalDeFondo = p.VozOriginalDeFondo;
            VozPronunciacion = p.ListaHablantes.FirstOrDefault()?.VozPronunciacion ?? voces.Predeterminada ?? Voces.FirstOrDefault()?.Id;
        }
        ActualizarResumen();
        _cargando = false;
    }

    private void ActualizarResumen()
    {
        if (_proyecto is not { } p)
        {
            Resumen = null;
            return;
        }
        if (p.ListaFrases.Count == 0)
        {
            Resumen = T("Vídeo de {0}. Pulsa «Doblar vídeo» para empezar.", TimeSpan.FromSeconds(p.Duracion).ToString(@"m\:ss"));
            return;
        }
        var f = p.ListaFrases;
        Resumen = T("{0} frases · {1} caben · {2} justas · {3} no caben · {4} sin doblar", f.Count,
            f.Count(x => x.Encaje == EncajeFrase.Cabe), f.Count(x => x.Encaje == EncajeFrase.Justa),
            f.Count(x => x.Encaje == EncajeFrase.NoCabe), f.Count(x => x.Encaje == EncajeFrase.SinGenerar));
    }

    private void TraduccionCambiada(FilaFrase fila)
    {
        if (_cargando || _proyecto is null || _almacen is null)
            return;
        var f = _proyecto.ListaFrases.First(x => x.Id == fila.Id);
        if (f.Traduccion == fila.Traduccion)
            return;
        // Cambiar el texto deja la frase pendiente de volver a doblar (su huella ya no coincide).
        _proyecto = _almacen.Guardar(_proyecto.ConFrase(f with { Traduccion = fila.Traduccion, Encaje = EncajeFrase.SinGenerar }));
        fila.Actualizar(_proyecto.ListaFrases.First(x => x.Id == fila.Id));
        ActualizarResumen();
    }

    partial void OnVozPronunciacionChanged(string? value)
    {
        if (_cargando || _proyecto is null || _almacen is null || _proyecto.ListaHablantes.Count == 0)
            return;
        _proyecto = _almacen.Guardar(_proyecto with { Hablantes = _proyecto.ListaHablantes.Select(h => h with { VozPronunciacion = value }).ToList() });
    }

    partial void OnVozOriginalDeFondoChanged(bool value)
    {
        if (!_cargando && _proyecto is not null && _almacen is not null)
            _proyecto = _almacen.Guardar(_proyecto with { VozOriginalDeFondo = value });
    }

    [RelayCommand]
    private void IrAComponentes() => navegacion.Navigate(typeof(PaginaComponentes));

    [RelayCommand]
    private async Task NuevoAsync()
    {
        var dialogo = new OpenFileDialog
        {
            Title = T("Elige el vídeo que quieres doblar"),
            Filter = T("Vídeos") + "|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v",
        };
        if (dialogo.ShowDialog() != true || _almacen is null)
            return;
        Error = null;
        try
        {
            var duracion = await doblaje.DuracionAsync(dialogo.FileName);
            var p = _almacen.Crear(dialogo.FileName, duracion);
            Proyectos.Insert(0, new OpcionDoblaje(p.Id, p.Nombre));
            ProyectoSeleccionado = Proyectos[0];
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Todo de una vez: guion, frases (solo las que falten o hayan cambiado) y exportación.</summary>
    [RelayCommand]
    private Task DoblarAsync() => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var p = await sesion.Doblador.PrepararAsync(_proyecto!, VozPronunciacion, progreso, ct);
        EnPantalla(() => Mostrar(p));
        p = await sesion.Doblador.GenerarAsync(p, RutaVoz, null, progreso, ct);
        EnPantalla(() => Mostrar(p));
        var r = await sesion.Doblador.ExportarAsync(p, progreso, ct);
        EnPantalla(() =>
        {
            VideoExportado = r.Video;
            Mensaje = T("Listo: el vídeo doblado está en la carpeta «Audio» del proyecto.");
        });
    });

    /// <summary>Solo hasta el guion traducido, para revisarlo antes de generar las voces.</summary>
    [RelayCommand]
    private Task PrepararAsync() => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var p = await sesion.Doblador.PrepararAsync(_proyecto!, VozPronunciacion, progreso, ct);
        EnPantalla(() =>
        {
            Mostrar(p);
            Mensaje = T("Guion listo: revisa la traducción y pulsa «Doblar vídeo».");
        });
    });

    [RelayCommand]
    private Task RegenerarAsync(FilaFrase fila) => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var p = await sesion.Doblador.GenerarAsync(_proyecto!, RutaVoz, [fila.Id], progreso, ct);
        EnPantalla(() =>
        {
            Mostrar(p);
            Mensaje = T("Frase rehecha. Pulsa «Doblar vídeo» para volver a exportar.");
        });
    });

    [RelayCommand]
    private void Escuchar(FilaFrase fila)
    {
        if (Reproductor.Sonando == fila.Archivo)
            Reproductor.Detener();
        else if (File.Exists(fila.Archivo))
            Reproductor.Reproducir(fila.Archivo);
    }

    [RelayCommand]
    private void Cancelar() => _cancelacion?.Cancel();

    [RelayCommand]
    private void AbrirCarpeta()
    {
        if (_proyecto is not null && _almacen is not null)
        {
            var carpeta = Directory.Exists(_almacen.Salida(_proyecto.Id)) ? _almacen.Salida(_proyecto.Id) : _almacen.CarpetaDe(_proyecto.Id);
            Process.Start(new ProcessStartInfo(carpeta) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void AbrirVideo()
    {
        if (VideoExportado is not null && File.Exists(VideoExportado))
            Process.Start(new ProcessStartInfo(VideoExportado) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task BorrarAsync()
    {
        if (_proyecto is null || _almacen is null)
            return;
        var caja = new Wpf.Ui.Controls.MessageBox
        {
            Title = T("Borrar el doblaje"),
            Content = T("Se borrará el proyecto «{0}» con sus frases dobladas y lo exportado. El vídeo original no se toca.", _proyecto.Nombre),
            PrimaryButtonText = T("Borrar"),
            CloseButtonText = T("Cancelar"),
        };
        if (await caja.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;
        Reproductor.Detener();
        _almacen.Borrar(_proyecto.Id);
        await CargarAsync();
    }

    /// <summary>El trabajo va en segundo plano; la lista de frases solo se toca desde la ventana.</summary>
    private static void EnPantalla(Action accion) => System.Windows.Application.Current.Dispatcher.Invoke(accion);

    private string RutaVoz(string? id) => doblaje.RutaVozAsync(id).GetAwaiter().GetResult();

    private async Task TrabajarAsync(Func<ServicioDoblaje.Sesion, IProgress<EventoDoblaje>, CancellationToken, Task> trabajo)
    {
        if (_proyecto is null || Ocupado)
            return;
        if (Faltan is not null)
        {
            Error = Faltan;
            return;
        }
        Reproductor.Detener();
        Error = null;
        Ocupado = true;
        Fraccion = 0;
        _cancelacion = new CancellationTokenSource();
        var progreso = new Progress<EventoDoblaje>(e =>
        {
            Mensaje = e.Mensaje;
            if (e.Fraccion is { } f)
                Fraccion = f;
        });
        try
        {
            await using var sesion = await doblaje.AbrirAsync();
            await Task.Run(() => trabajo(sesion, progreso, _cancelacion.Token));
        }
        catch (OperationCanceledException)
        {
            Mensaje = T("Cancelado. Lo hecho se conserva: puedes seguir cuando quieras.");
        }
        catch (Exception e)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Ocupado = false;
            _cancelacion.Dispose();
            _cancelacion = null;
            if (_proyecto is not null && _almacen?.Abrir(_proyecto.Id) is { } actual)
                Mostrar(actual);
        }
    }
}
