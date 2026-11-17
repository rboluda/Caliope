using Constantino.Core.Instalaciones;

namespace Constantino.App.Servicios;

/// <summary>Mide lo que ocupa Calíope y libera espacio (ver <see cref="Espacio"/>).</summary>
public sealed class ServicioEspacio(ServicioInstalacion instalacion)
{
    public async Task<Espacio> EspacioAsync() => new(await instalacion.InstaladorAsync(), instalacion.Catalogo);

    /// <summary>Unos segundos con muchos gigas: va en segundo plano.</summary>
    public async Task<InformeEspacio> MedirAsync(CancellationToken ct = default)
    {
        var espacio = await EspacioAsync();
        return await Task.Run(() => espacio.Medir(ct), ct);
    }

    /// <summary>Lo que se libera de verdad al desinstalar <paramref name="id"/> (descontando lo que comparte con otros).</summary>
    public async Task<long> LiberaAsync(string id) =>
        (await MedirAsync()).Componentes.FirstOrDefault(c => c.Componente.Id == id)?.Libera ?? 0;

    /// <summary>Borra algo interno. Devuelve lo que no se pudo borrar porque estaba en uso.</summary>
    public async Task<IReadOnlyList<string>> LimpiarAsync(EspacioLimpiable limpiable)
    {
        var espacio = await EspacioAsync();
        return await Task.Run(() => espacio.Limpiar(limpiable));
    }
}
