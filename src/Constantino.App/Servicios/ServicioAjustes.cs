using System.IO;
using System.Text.Json;
using Constantino.Core.Equipo;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Constantino.App.Servicios;

public enum TemaApp
{
    Sistema,
    Claro,
    Oscuro,
}

public sealed record AjustesApp(
    TemaApp Tema = TemaApp.Sistema,
    string? CarpetaDatos = null,
    bool AsistenteCompletado = false,
    string? VozPredeterminada = null,
    bool BuscarActualizaciones = true,
    DateTimeOffset? UltimaComprobacion = null);

/// <summary>Preferencias del usuario, en %LOCALAPPDATA%\Caliope\ajustes.json (fuera de la carpeta de datos, que puede cambiar).</summary>
public sealed class ServicioAjustes
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _ruta = Path.Combine(CarpetaDatos.Predeterminada, "ajustes.json");
    private static readonly string RutaAntigua = Path.Combine(CarpetaDatos.PredeterminadaAntigua, "ajustes.json");

    public AjustesApp Actual { get; private set; }

    public ServicioAjustes()
    {
        Actual = Leer();
    }

    public string CarpetaDatosEfectiva => Actual.CarpetaDatos ?? CarpetaDatos.Predeterminada;

    public void Guardar(AjustesApp ajustes)
    {
        Actual = ajustes;
        Directory.CreateDirectory(Path.GetDirectoryName(_ruta)!);
        File.WriteAllText(_ruta, JsonSerializer.Serialize(ajustes, Opciones));
    }

    public void AplicarTema()
    {
        switch (Actual.Tema)
        {
            case TemaApp.Claro:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                break;
            case TemaApp.Oscuro:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(true);
                break;
        }
        Marca.AplicarAcento(ApplicationThemeManager.GetAppTheme());
    }

    private AjustesApp Leer()
    {
        try
        {
            // Primera vez con el nombre de Calíope: se traen los ajustes de Constantino. Si usaba la carpeta por
            // defecto, se apunta explícitamente para que no se pierda al cambiar el nombre por defecto.
            if (!File.Exists(_ruta) && File.Exists(RutaAntigua))
            {
                var antiguos = JsonSerializer.Deserialize<AjustesApp>(File.ReadAllText(RutaAntigua), Opciones) ?? new AjustesApp();
                if (antiguos.CarpetaDatos is null && antiguos.AsistenteCompletado)
                    antiguos = antiguos with { CarpetaDatos = CarpetaDatos.PredeterminadaAntigua };
                Guardar(antiguos);
                return antiguos;
            }
            return File.Exists(_ruta)
                ? JsonSerializer.Deserialize<AjustesApp>(File.ReadAllText(_ruta), Opciones) ?? new AjustesApp()
                : new AjustesApp();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Un ajustes.json dañado no debe impedir arrancar: se vuelve a los valores por defecto.
            return new AjustesApp();
        }
    }
}
