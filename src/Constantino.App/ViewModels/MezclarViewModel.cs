using System.Collections.ObjectModel;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Motores;
using Constantino.Core.Narracion;
using Constantino.Core.Voces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>Una voz de la biblioteca para el mezclador.</summary>
public sealed record OpcionVozMezcla(Voz Voz, string Referencia)
{
    public string Nombre => Voz.Nombre;
}

/// <summary>De dónde sale la gravedad o el ritmo de la mezcla: tal como salga, de la voz A o de la voz B.</summary>
public enum OrigenPropiedad { ComoSalga, DeA, DeB }

/// <summary>
/// Mezclador de voces: cada propiedad de una de las dos voces. La forma de hablar (acento, entonación, emoción) y el
/// timbre los separa el motor castellano (T3 y S3Gen). La gravedad y el ritmo se igualan después a los de la voz elegida
/// midiendo su tono y su velocidad de habla y ajustándolos con rubberband (medido: el motor principal los respeta al
/// narrar y conserva el acento). Sin ajustar, el ritmo sigue a la voz de la forma de hablar y la gravedad sale a medias;
/// el timbre sigue a su voz (parecido 0,98). «Acercar» mezcla la huella del timbre con la de la otra voz: un efecto suave.
/// </summary>
public sealed partial class MezclarViewModel : ObservableObject
{
    private readonly ServicioMotor _motor;
    private readonly ServicioVoces _voces;
    private readonly INavigationService _navegacion;

    /// <summary>Cuántas veces se repite una mezcla que se salta palabras antes de dejarla con su aviso.</summary>
    private const int Reintentos = 2;

    public MezclarViewModel(ServicioMotor motor, ServicioVoces voces, ServicioReproductor reproductor, INavigationService navegacion)
    {
        _motor = motor;
        _voces = voces;
        _navegacion = navegacion;
        Reproductor = reproductor;
    }

    public ServicioReproductor Reproductor { get; }
    public ServicioMotor Motor => _motor;
    public ObservableCollection<OpcionVozMezcla> Voces { get; } = [];

    [ObservableProperty] public partial bool MotorCastellanoInstalado { get; set; } = true;
    [ObservableProperty] public partial bool SinVoces { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Resumen), nameof(PuedeMezclar))]
    public partial OpcionVozMezcla? VozA { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Resumen), nameof(PuedeMezclar))]
    public partial OpcionVozMezcla? VozB { get; set; }

    /// <summary>La forma de hablar (acento, ritmo, entonación) sale de A; si no, de B.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HablaDeB), nameof(Resumen))]
    public partial bool HablaDeA { get; set; } = true;

    public bool HablaDeB
    {
        get => !HablaDeA;
        set => HablaDeA = !value;
    }

    /// <summary>El timbre (quién suena) sale de A; si no, de B.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimbreDeB), nameof(Resumen))]
    public partial bool TimbreDeA { get; set; }

    public bool TimbreDeB
    {
        get => !TimbreDeA;
        set => TimbreDeA = !value;
    }

    /// <summary>De dónde sale la gravedad (el tono medio): sin ajustar sale a medias entre las dos.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GravedadComoSalga), nameof(GravedadDeA), nameof(GravedadDeB), nameof(Resumen))]
    public partial OrigenPropiedad Gravedad { get; set; }

    public bool GravedadComoSalga { get => Gravedad == OrigenPropiedad.ComoSalga; set { if (value) Gravedad = OrigenPropiedad.ComoSalga; } }
    public bool GravedadDeA { get => Gravedad == OrigenPropiedad.DeA; set { if (value) Gravedad = OrigenPropiedad.DeA; } }
    public bool GravedadDeB { get => Gravedad == OrigenPropiedad.DeB; set { if (value) Gravedad = OrigenPropiedad.DeB; } }

    /// <summary>De dónde sale el ritmo (la velocidad de habla): sin ajustar, el de la voz de la forma de hablar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RitmoComoSalga), nameof(RitmoDeA), nameof(RitmoDeB), nameof(Resumen))]
    public partial OrigenPropiedad Ritmo { get; set; }

    public bool RitmoComoSalga { get => Ritmo == OrigenPropiedad.ComoSalga; set { if (value) Ritmo = OrigenPropiedad.ComoSalga; } }
    public bool RitmoDeA { get => Ritmo == OrigenPropiedad.DeA; set { if (value) Ritmo = OrigenPropiedad.DeA; } }
    public bool RitmoDeB { get => Ritmo == OrigenPropiedad.DeB; set { if (value) Ritmo = OrigenPropiedad.DeB; } }

    /// <summary>Lo que se ha ajustado de gravedad y ritmo en la última mezcla («Gravedad 139 → 181 Hz · ritmo ×0,87»).</summary>
    [ObservableProperty] public partial string? Ajustes { get; set; }

    /// <summary>Límites del ajuste: más allá suena artificial.</summary>
    private const double SemitonosMaximos = 5, TempoMinimo = 0.75, TempoMaximo = 1.33;

    /// <summary>Cuánto se acerca el timbre a la otra voz (0–50 %).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Resumen))]
    public partial double Acercar { get; set; }

    [ObservableProperty] public partial string TextoMuestra { get; set; } = TextosDeReferencia.Para("Spanish");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre), nameof(PuedeMezclar))]
    public partial bool Trabajando { get; set; }

    public bool Libre => !Trabajando;
    public bool PuedeMezclar => !Trabajando && VozA is not null && VozB is not null;

    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Aviso { get; set; }

    /// <summary>La grabación mezclada (null hasta que se crea).</summary>
    [ObservableProperty] public partial string? Resultado { get; set; }

    /// <summary>El párrafo de narración con la voz mezclada ya clonada.</summary>
    [ObservableProperty] public partial string? Narracion { get; set; }

    [ObservableProperty] public partial string NombreNueva { get; set; } = "";
    [ObservableProperty] public partial string? Guardada { get; set; }
    private double _duracion;

    /// <summary>Qué se toma de cada voz, en una frase («Acento, ritmo y entonación de Ana; timbre de Luis»).</summary>
    public string Resumen
    {
        get
        {
            if (VozA is null || VozB is null)
                return T("Elige las dos voces.");
            var (habla, timbre, otra) = Reparto();
            var partes = new List<string>
            {
                T("acento y entonación de «{0}»", habla.Nombre),
                Acercar >= 1
                    ? T("timbre de «{0}» (un {1:0} % más cerca de «{2}»)", timbre.Nombre, Acercar, otra.Nombre)
                    : T("timbre de «{0}»", timbre.Nombre),
            };
            if (Gravedad != OrigenPropiedad.ComoSalga)
                partes.Add(T("gravedad de «{0}»", (Gravedad == OrigenPropiedad.DeA ? VozA : VozB).Nombre));
            if (Ritmo != OrigenPropiedad.ComoSalga)
                partes.Add(T("ritmo de «{0}»", (Ritmo == OrigenPropiedad.DeA ? VozA : VozB).Nombre));
            var resumen = string.Join(", ", partes) + ".";
            return char.ToUpper(resumen[0]) + resumen[1..];
        }
    }

    private (OpcionVozMezcla Habla, OpcionVozMezcla Timbre, OpcionVozMezcla Otra) Reparto() =>
        (HablaDeA ? VozA! : VozB!, TimbreDeA ? VozA! : VozB!, TimbreDeA ? VozB! : VozA!);

    public async Task CargarAsync()
    {
        MotorCastellanoInstalado = await _motor.HayMotorCastellanoAsync();
        var biblioteca = await _voces.BibliotecaAsync();
        var (a, b) = (VozA?.Voz.Id, VozB?.Voz.Id);
        Voces.Clear();
        foreach (var v in biblioteca.Listar())
            Voces.Add(new OpcionVozMezcla(v, biblioteca.ReferenciaDe(v.Id)));
        VozA = Voces.FirstOrDefault(v => v.Voz.Id == a) ?? Voces.ElementAtOrDefault(0);
        VozB = Voces.FirstOrDefault(v => v.Voz.Id == b) ?? Voces.ElementAtOrDefault(1);
        SinVoces = Voces.Count < 2;
    }

    // Cambiar cualquier elección deja la mezcla anterior sin validez: hay que volver a crearla.
    partial void OnVozAChanged(OpcionVozMezcla? value) => Olvidar();
    partial void OnVozBChanged(OpcionVozMezcla? value) => Olvidar();
    partial void OnHablaDeAChanged(bool value) => Olvidar();
    partial void OnTimbreDeAChanged(bool value) => Olvidar();
    partial void OnAcercarChanged(double value) => Olvidar();
    partial void OnGravedadChanged(OrigenPropiedad value) => Olvidar();
    partial void OnRitmoChanged(OrigenPropiedad value) => Olvidar();
    partial void OnTextoMuestraChanged(string value) => Olvidar();

    private void Olvidar()
    {
        Resultado = Narracion = Guardada = Aviso = Ajustes = null;
    }

    [RelayCommand]
    private void EscucharVoz(OpcionVozMezcla? v)
    {
        if (v is not null)
            Escuchar(v.Referencia);
    }

    /// <summary>
    /// Crea la grabación mezclada leyendo el texto de muestra. Si la transcripción automática está instalada, comprueba que
    /// lo lee exacto (será su transcripción) y, si no, lo repite con otra semilla.
    /// </summary>
    [RelayCommand]
    private async Task MezclarAsync()
    {
        if (VozA is null || VozB is null)
            return;
        if (string.IsNullOrWhiteSpace(TextoMuestra))
        {
            Error = T("Hace falta un texto de muestra.");
            return;
        }
        Error = Aviso = Guardada = null;
        Trabajando = true;
        Reproductor.Detener();
        var (habla, timbre, otra) = Reparto();
        var texto = TextoMuestra.Trim();
        try
        {
            var whisper = await _motor.HayWhisperAsync();
            string? salida = null;
            var leeBien = true;
            for (var intento = 0; intento <= Reintentos; intento++)
            {
                Mensaje = intento == 0 ? T("Mezclando las voces…") : T("Repitiendo la mezcla, que se saltaba palabras…");
                salida = await _voces.ArchivoTemporalAsync($"mezcla-{intento}");
                var r = await _motor.MezclarVocesAsync(texto, habla.Referencia, timbre.Referencia, salida,
                    Acercar >= 1 ? otra.Referencia : null, Acercar / 100, 1234 + intento);
                _duracion = r.Duracion;
                if (!whisper)
                    break;
                Mensaje = T("Comprobando que la mezcla lee bien el texto…");
                leeBien = RevisionDiseno.LeeElTexto(texto, (await _motor.TranscribirAsync(salida, "Spanish")).Texto, "Spanish");
                if (leeBien)
                    break;
            }
            salida = await AjustarGravedadYRitmoAsync(salida!, texto);
            Resultado = salida;
            Narracion = null;
            if (!leeBien)
                Aviso = T("La mezcla se salta o cambia alguna palabra del texto de muestra: la voz clonada podría salir cortada. Prueba con otro texto.");
            if (string.IsNullOrWhiteSpace(NombreNueva))
                NombreNueva = T("{0} con voz de {1}", habla.Nombre, timbre.Nombre);
            Mensaje = T("Mezcla lista. Escúchala y prueba «Así sonará al narrar».");
            Reproductor.Reproducir(salida!);
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

    /// <summary>
    /// Iguala la gravedad (tono medio) y el ritmo (letras por segundo con voz) de la mezcla a los de la voz elegida, con
    /// rubberband (el tono conservando los formantes). Devuelve la grabación ajustada, o la misma si no hay nada que ajustar.
    /// </summary>
    private async Task<string> AjustarGravedadYRitmoAsync(string mezcla, string texto)
    {
        if (Gravedad == OrigenPropiedad.ComoSalga && Ritmo == OrigenPropiedad.ComoSalga)
            return mezcla;
        Mensaje = T("Ajustando la gravedad y el ritmo…");
        var medidaMezcla = await _motor.AnalizarVozAsync(mezcla, conDisenador: false);
        var semitonos = 0.0;
        var tempo = 1.0;
        var detalles = new List<string>();
        if (Gravedad != OrigenPropiedad.ComoSalga)
        {
            var objetivo = await _motor.AnalizarVozAsync((Gravedad == OrigenPropiedad.DeA ? VozA! : VozB!).Referencia, conDisenador: false);
            if (objetivo.TonoHz > 0 && medidaMezcla.TonoHz > 0)
            {
                semitonos = Math.Clamp(12 * Math.Log2(objetivo.TonoHz / medidaMezcla.TonoHz), -SemitonosMaximos, SemitonosMaximos);
                detalles.Add(T("gravedad {0:0} → {1:0} Hz", medidaMezcla.TonoHz, medidaMezcla.TonoHz * Math.Pow(2, semitonos / 12)));
            }
        }
        if (Ritmo != OrigenPropiedad.ComoSalga)
        {
            var voz = Ritmo == OrigenPropiedad.DeA ? VozA! : VozB!;
            if (voz.Voz.SoloTimbre)
                Aviso = T("«{0}» no tiene transcripción: no se puede medir su ritmo.", voz.Nombre);
            else
            {
                var objetivo = await _motor.AnalizarVozAsync(voz.Referencia, conDisenador: false);
                var ritmoObjetivo = LetrasPorSegundo(voz.Voz.Transcripcion, objetivo);
                var ritmoMezcla = LetrasPorSegundo(texto, medidaMezcla);
                if (ritmoObjetivo > 0 && ritmoMezcla > 0)
                {
                    tempo = Math.Clamp(ritmoObjetivo / ritmoMezcla, TempoMinimo, TempoMaximo);
                    detalles.Add(T("ritmo ×{0:0.00}", tempo));
                }
            }
        }
        if (Math.Abs(semitonos) < 0.2 && Math.Abs(tempo - 1) < 0.02)
            return mezcla;
        var ajustada = await _voces.ArchivoTemporalAsync("mezcla-ajustada");
        await (await _motor.ProcesadorAsync()).CambiarTempoYTonoAsync(mezcla, ajustada, tempo, semitonos, CancellationToken.None);
        Ajustes = T("Ajustado: {0}.", string.Join(" · ", detalles));
        return ajustada;
    }

    /// <summary>Velocidad de habla: letras por segundo de voz (sin contar los silencios).</summary>
    private static double LetrasPorSegundo(string texto, MedidasVoz m)
    {
        var letras = texto.Count(char.IsLetterOrDigit);
        var conVoz = m.Duracion * m.Sonoridad;
        return conVoz > 0 ? letras / conVoz : 0;
    }

    [RelayCommand]
    private void EscucharMezcla()
    {
        if (Resultado is not null)
            Escuchar(Resultado);
    }

    /// <summary>Lee un párrafo de narración con la mezcla ya clonada por el motor principal: como sonará en Narrar.</summary>
    [RelayCommand]
    private async Task NarrarAsync()
    {
        if (Resultado is null)
            return;
        if (Narracion is { } hecha && File.Exists(hecha))
        {
            Escuchar(hecha);
            return;
        }
        Error = null;
        Trabajando = true;
        Mensaje = T("Leyendo un párrafo con la voz mezclada…");
        try
        {
            var voz = new Voz("prueba-mezcla", NombreNueva, "Spanish", OrigenVoz.Mezclada, TextoMuestra.Trim(), _duracion, DateTimeOffset.Now, true);
            var salida = await _voces.ArchivoTemporalAsync("mezcla-narracion");
            var r = await _motor.SintetizarAsync(voz, Resultado, TextosDeNarracion.Para("Spanish"), salida);
            Narracion = r.Salida;
            Mensaje = "";
            Reproductor.Reproducir(r.Salida);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Trabajando = false;
        }
    }

    [RelayCommand]
    private async Task GuardarAsync()
    {
        if (Resultado is null)
            return;
        Error = null;
        try
        {
            var biblioteca = await _voces.BibliotecaAsync();
            var r = await biblioteca.ImportarAsync(new SolicitudImportacion(Resultado, NombreNueva, "Spanish", TextoMuestra.Trim(),
                Consentimiento: true, OrigenVoz.Mezclada, Resumen));
            _voces.Notificar();
            Guardada = T("Guardada como «{0}».", r.Voz.Nombre) + (r.Aviso is null ? "" : " " + r.Aviso);
        }
        catch (VozInvalidaException e)
        {
            Error = e.Message;
        }
    }

    private void Escuchar(string ruta)
    {
        if (Reproductor.Sonando == ruta)
            Reproductor.Detener();
        else
            Reproductor.Reproducir(ruta);
    }

    [RelayCommand]
    private void IrAVoces() => _navegacion.Navigate(typeof(PaginaVoces));

    [RelayCommand]
    private void IrAComponentes() => _navegacion.Navigate(typeof(PaginaComponentes));
}
