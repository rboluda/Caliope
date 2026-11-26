using System.Globalization;

namespace Constantino.Core.Audio;

/// <summary>
/// Limpieza de una grabación antes de clonarla, con FFmpeg: reparar la distorsión (picos recortados) y quitar el ruido de
/// fondo. Quitar la música lo hace el separador de voz (Demucs), aparte.
/// <para>
/// Todo es suave a propósito: el motor copia también los defectos de la limpieza (un sonido metálico, una voz «bajo el
/// agua»), así que vale más dejar un poco de ruido que estropear la voz. Medido con una grabación de prueba (voz con
/// música, ruido y saturación): la saturación pasa del 0,06 % al 0,001 % de las muestras y el fondo, de −7 a −54 dB
/// respecto a la voz.
/// </para>
/// </summary>
public static class LimpiezaGrabacion
{
    public const int Frecuencia = 24000;

    /// <summary>
    /// Reparar la distorsión (adeclip reconstruye los picos recortados). Se baja antes 6 dB para que los picos
    /// reconstruidos quepan; luego se vuelve a igualar el volumen.
    /// </summary>
    public static string[] Reparar(string entrada, string salida) =>
        ["-hide_banner", "-nostdin", "-y", "-i", entrada, "-ac", "1", "-ar", Frecuencia.ToString(CultureInfo.InvariantCulture),
         "-af", "volume=-6dB,adeclip", "-c:a", "pcm_f32le", salida];

    /// <summary>
    /// Quitar el ruido de fondo (afftdn, con seguimiento del ruido), y de paso pasar a mono a 24 kHz. Reducción de
    /// 10 dB: suave. <paramref name="ruidoDb"/> es el nivel del fondo medido (si no se sabe, el de una grabación limpia).
    /// </summary>
    public static string[] QuitarRuido(string entrada, string salida, double? ruidoDb) =>
        ["-hide_banner", "-nostdin", "-y", "-i", entrada, "-ac", "1", "-ar", Frecuencia.ToString(CultureInfo.InvariantCulture),
         "-af", FormattableString.Invariant($"afftdn=nr=10:nf={NivelRuido(ruidoDb):0}:tn=1"), "-c:a", "pcm_f32le", salida];

    /// <summary>Solo pasar a mono a 24 kHz (tras el separador, que entrega estéreo a 44,1 kHz).</summary>
    public static string[] AMono(string entrada, string salida) =>
        ["-hide_banner", "-nostdin", "-y", "-i", entrada, "-ac", "1", "-ar", Frecuencia.ToString(CultureInfo.InvariantCulture),
         "-c:a", "pcm_f32le", salida];

    /// <summary>afftdn admite el nivel de ruido entre −80 y −20 dB.</summary>
    public static double NivelRuido(double? ruidoDb) => Math.Clamp(ruidoDb ?? -50, -80, -20);

    /// <summary>El pico a −1 dBFS: el mismo volumen que tenía, sin que la reparación lo deje bajo ni nada sature.</summary>
    public static Pcm Igualar(Pcm audio)
    {
        var pico = audio.Muestras.Length == 0 ? 0 : audio.Muestras.Max(Math.Abs);
        if (pico < 1e-6)
            return audio;
        var objetivo = (float)Math.Pow(10, -1 / 20.0);
        return new Pcm(audio.Muestras.Select(x => x * objetivo / pico).ToArray(), audio.Frecuencia);
    }
}
