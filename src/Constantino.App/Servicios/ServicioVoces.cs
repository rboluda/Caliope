using System.IO;
using Constantino.Core.Instalaciones;
using Constantino.Core.Voces;

namespace Constantino.App.Servicios;

/// <summary>La biblioteca de voces de la carpeta de datos actual, más los archivos temporales de pruebas.</summary>
public sealed class ServicioVoces(ServicioInstalacion instalacion, ServicioAjustes ajustes)
{
    public event EventHandler? Cambiada;

    public async Task<BibliotecaVoces> BibliotecaAsync()
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        return new BibliotecaVoces(rutas.Voces, rutas.Ffmpeg, new EjecutorProcesos());
    }

    public string? Predeterminada => ajustes.Actual.VozPredeterminada;

    public void MarcarPredeterminada(string? id)
    {
        ajustes.Guardar(ajustes.Actual with { VozPredeterminada = id });
        Cambiada?.Invoke(this, EventArgs.Empty);
    }

    public void Notificar() => Cambiada?.Invoke(this, EventArgs.Empty);

    /// <summary>Ruta nueva para un audio de prueba; borra los de sesiones anteriores.</summary>
    public async Task<string> ArchivoTemporalAsync(string prefijo)
    {
        var carpeta = (await instalacion.InstaladorAsync()).Rutas.Temporales;
        Directory.CreateDirectory(carpeta);
        foreach (var viejo in Directory.EnumerateFiles(carpeta, "*.wav").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)))
        {
            try { File.Delete(viejo); }
            catch (IOException) { }
        }
        return Path.Combine(carpeta, $"{prefijo}-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.wav");
    }
}
