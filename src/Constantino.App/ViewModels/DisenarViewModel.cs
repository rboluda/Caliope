using System.Collections.ObjectModel;
using System.Globalization;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Voces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

public sealed record OpcionSexo(SexoVoz Valor, string Nombre);

public sealed record EjemploDiseno(string Nombre, DescripcionGuiada Descripcion);

/// <summary>Una voz candidata: el mismo texto leído con otra semilla.</summary>
public sealed partial class VarianteVoz(int numero, int semilla, string ruta, double duracion, string instruccion, string idioma, string texto)
    : ObservableObject
{
    public int Numero { get; } = numero;
    public int Semilla { get; } = semilla;
    public string Ruta { get; } = ruta;
    public string Instruccion { get; } = instruccion;
    public string Idioma { get; } = idioma;
    public string Texto { get; } = texto;
    public string Titulo => T("Variante {0}", Numero);
    public string Detalle => T("Semilla {0} · {1:0.0} s", Semilla, duracion);

    [ObservableProperty] public partial bool Guardando { get; set; }
    [ObservableProperty] public partial string Nombre { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Guardada { get; set; }
}

public sealed partial class DisenarViewModel : ObservableObject
{
    private readonly ServicioMotor _motor;
    private readonly ServicioVoces _voces;
    private readonly ServicioInstalacion _instalacion;
    private readonly INavigationService _navegacion;
    private string _ultimaCompuesta = "";
    private CancellationTokenSource? _cancelacion;

    public DisenarViewModel(ServicioMotor motor, ServicioVoces voces, ServicioReproductor reproductor, ServicioInstalacion instalacion,
        INavigationService navegacion)
    {
        _motor = motor;
        _voces = voces;
        _instalacion = instalacion;
        _navegacion = navegacion;
        Reproductor = reproductor;
        Ejemplo = Ejemplos[0]; // aplica sus opciones y compone la descripción
        TextoMuestra = TextosDeReferencia.Para(Idioma);
    }

    public ServicioReproductor Reproductor { get; }
    public ServicioMotor Motor => _motor;

    public IReadOnlyList<EjemploDiseno> Ejemplos { get; } = CatalogoDiseno.Ejemplos.Select(e => new EjemploDiseno(e.Nombre, e.Descripcion)).ToList();
    public IReadOnlyList<OpcionSexo> Sexos { get; } = [new(SexoVoz.Hombre, T("Hombre")), new(SexoVoz.Mujer, T("Mujer")), new(SexoVoz.Indiferente, T("Indiferente"))];
    public IReadOnlyList<OpcionDiseno> Timbres => CatalogoDiseno.Timbres;
    public IReadOnlyList<OpcionDiseno> Texturas => CatalogoDiseno.Texturas;
    public IReadOnlyList<OpcionDiseno> Ritmos => CatalogoDiseno.Ritmos;
    public IReadOnlyList<OpcionDiseno> Tonos => CatalogoDiseno.Tonos;
    public IReadOnlyList<OpcionDiseno> Acentos => CatalogoDiseno.Acentos;
    public IReadOnlyList<OpcionIdioma> IdiomasDisponibles { get; } = Idiomas.Todos.Select(i => new OpcionIdioma(i.Clave, i.Nombre)).ToList();
    public ObservableCollection<VarianteVoz> Variantes { get; } = [];

    [ObservableProperty] public partial bool VoiceDesignInstalado { get; set; } = true;

    [ObservableProperty] public partial EjemploDiseno? Ejemplo { get; set; }
    [ObservableProperty] public partial SexoVoz Sexo { get; set; }
    [ObservableProperty] public partial double Edad { get; set; }
    [ObservableProperty] public partial string Timbre { get; set; } = "";
    [ObservableProperty] public partial string Textura { get; set; } = "";
    [ObservableProperty] public partial string Ritmo { get; set; } = "";
    [ObservableProperty] public partial string Tono { get; set; } = "";
    [ObservableProperty] public partial string? Acento { get; set; }
    [ObservableProperty] public partial string Extra { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsEspanol))]
    public partial string Idioma { get; set; } = "Spanish";

    public bool EsEspanol => Idioma == "Spanish";

    [ObservableProperty] public partial string Instruccion { get; set; } = "";
    [ObservableProperty] public partial string TextoMuestra { get; set; } = "";
    [ObservableProperty] public partial double NumeroVariantes { get; set; } = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Generando { get; set; }

    public bool Libre => !Generando;

    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }

    public async Task CargarAsync()
    {
        VoiceDesignInstalado = (await _instalacion.InstaladorAsync()).EstaInstalado(ServicioMotor.ModeloVoiceDesign);
    }

    // --- Descripción guiada ------------------------------------------------------------------

    partial void OnEjemploChanged(EjemploDiseno? value)
    {
        if (value is not null)
            Aplicar(value);
    }

    private void Aplicar(EjemploDiseno ejemplo)
    {
        var d = ejemplo.Descripcion;
        Sexo = d.Sexo;
        Edad = d.Edad;
        Timbre = d.Timbre;
        Textura = d.Textura;
        Ritmo = d.Ritmo;
        Tono = d.Tono;
        Acento = d.Acento;
        Extra = d.Extra ?? "";
        Componer(forzar: true);
    }

    partial void OnSexoChanged(SexoVoz value) => Componer();
    partial void OnEdadChanged(double value) => Componer();
    partial void OnTimbreChanged(string value) => Componer();
    partial void OnTexturaChanged(string value) => Componer();
    partial void OnRitmoChanged(string value) => Componer();
    partial void OnTonoChanged(string value) => Componer();
    partial void OnAcentoChanged(string? value) => Componer();
    partial void OnExtraChanged(string value) => Componer();

    partial void OnIdiomaChanged(string oldValue, string newValue)
    {
        // Si el texto de muestra era el de ejemplo, se cambia por el del nuevo idioma.
        if (TextoMuestra == TextosDeReferencia.Para(oldValue))
            TextoMuestra = TextosDeReferencia.Para(newValue);
        Componer();
    }

    /// <summary>Rehace la descripción desde las opciones, salvo que el usuario la haya retocado a mano.</summary>
    private void Componer(bool forzar = false)
    {
        if (!forzar && Instruccion != _ultimaCompuesta)
            return;
        _ultimaCompuesta = new DescripcionGuiada(Sexo, (int)Edad, Timbre, Textura, Ritmo, Tono, Acento, Extra).Componer(Idioma);
        Instruccion = _ultimaCompuesta;
    }

    [RelayCommand]
    private void RehacerDescripcion() => Componer(forzar: true);

    [RelayCommand]
    private void RestaurarTexto() => TextoMuestra = TextosDeReferencia.Para(Idioma);

    // --- Generar variantes -------------------------------------------------------------------

    [RelayCommand]
    private async Task GenerarAsync()
    {
        if (string.IsNullOrWhiteSpace(Instruccion) || string.IsNullOrWhiteSpace(TextoMuestra))
        {
            Error = T("Hace falta una descripción y un texto de muestra.");
            return;
        }
        Error = null;
        Reproductor.Detener();
        Variantes.Clear();
        Generando = true;
        _cancelacion = new CancellationTokenSource();
        var total = (int)Math.Clamp(NumeroVariantes, 1, 6);
        var aleatorio = new Random();
        try
        {
            for (var i = 1; i <= total; i++)
            {
                Mensaje = T("Creando la variante {0} de {1}…", i, total);
                var semilla = aleatorio.Next(1, 1_000_000);
                var salida = await _voces.ArchivoTemporalAsync($"diseno-{i}");
                var r = await _motor.DisenarAsync(Instruccion, Idioma, TextoMuestra.Trim(), salida, semilla, ct: _cancelacion.Token);
                var variante = new VarianteVoz(i, semilla, r.Salida, r.Duracion, Instruccion, Idioma, TextoMuestra.Trim())
                {
                    Nombre = Ejemplo?.Nombre ?? T("Voz diseñada"),
                };
                Variantes.Add(variante);
                if (i == 1)
                    Reproductor.Reproducir(r.Salida);
            }
            Mensaje = T("Escucha las variantes y guarda la que más te guste.");
        }
        catch (OperationCanceledException)
        {
            Mensaje = T("Cancelado.");
        }
        catch (Exception e)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Generando = false;
            _cancelacion.Dispose();
            _cancelacion = null;
        }
    }

    [RelayCommand]
    private void Cancelar() => _cancelacion?.Cancel();

    [RelayCommand]
    private void Escuchar(VarianteVoz v)
    {
        if (Reproductor.Sonando == v.Ruta)
            Reproductor.Detener();
        else
            Reproductor.Reproducir(v.Ruta);
    }

    /// <summary>La grabación de la variante pasa a ser la referencia de la voz; el texto de muestra, su transcripción exacta.</summary>
    [RelayCommand]
    private async Task GuardarAsync(VarianteVoz v)
    {
        v.Error = null;
        v.Guardando = true;
        try
        {
            var biblioteca = await _voces.BibliotecaAsync();
            var r = await biblioteca.ImportarAsync(new SolicitudImportacion(
                v.Ruta, v.Nombre, v.Idioma, v.Texto, Consentimiento: true, OrigenVoz.Disenada, v.Instruccion, v.Semilla));
            if (_voces.Predeterminada is null)
                _voces.MarcarPredeterminada(r.Voz.Id);
            else
                _voces.Notificar();
            v.Guardada = T("Guardada como «{0}».", r.Voz.Nombre) + (r.Aviso is null ? "" : " " + r.Aviso);
        }
        catch (VozInvalidaException e)
        {
            v.Error = e.Message;
        }
        finally
        {
            v.Guardando = false;
        }
    }

    [RelayCommand]
    private void IrAVoces() => _navegacion.Navigate(typeof(PaginaVoces));

    [RelayCommand]
    private void IrAComponentes() => _navegacion.Navigate(typeof(PaginaComponentes));
}
