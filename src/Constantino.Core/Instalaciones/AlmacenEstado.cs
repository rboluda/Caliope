using System.Text.Json;

namespace Constantino.Core.Instalaciones;

/// <summary>Qué se instaló, en qué versión y para qué dispositivo.</summary>
public sealed record RegistroInstalacion(string Version, DateTimeOffset Fecha, string Dispositivo);

/// <summary>estado.json de la carpeta de datos.</summary>
public sealed class AlmacenEstado(string ruta)
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _cerrojo = new();

    public IReadOnlyDictionary<string, RegistroInstalacion> Leer()
    {
        lock (_cerrojo)
        {
            if (!File.Exists(ruta))
                return new Dictionary<string, RegistroInstalacion>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, RegistroInstalacion>>(File.ReadAllText(ruta), Opciones)
                       ?? new Dictionary<string, RegistroInstalacion>();
            }
            catch (JsonException)
            {
                // Un estado dañado equivale a «nada registrado»: la comprobación de archivos dirá el resto.
                return new Dictionary<string, RegistroInstalacion>();
            }
        }
    }

    public void Registrar(string id, RegistroInstalacion registro) => Modificar(d => d[id] = registro);

    public void Quitar(string id) => Modificar(d => d.Remove(id));

    private void Modificar(Action<Dictionary<string, RegistroInstalacion>> cambio)
    {
        lock (_cerrojo)
        {
            var datos = new Dictionary<string, RegistroInstalacion>(Leer());
            cambio(datos);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            // Escritura atómica: si se corta a medias, el estado anterior sigue intacto.
            var temporal = ruta + ".tmp";
            File.WriteAllText(temporal, JsonSerializer.Serialize(datos, Opciones));
            File.Move(temporal, ruta, overwrite: true);
        }
    }
}
