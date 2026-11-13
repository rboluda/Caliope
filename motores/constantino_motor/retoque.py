"""Retoques de una grabación de voz con el analizador WORLD (pyworld, MIT), sin modelos ni tarjeta gráfica.

WORLD separa la voz en tres partes: el tono (la vibración de las cuerdas vocales), la envolvente espectral (el timbre,
las resonancias de la boca) y la aperiodicidad (cuánto aire hay). Cambiando cada parte y volviendo a juntarlas se
transforma la voz sin que deje de ser la misma persona.
"""
import os

from .servidor import ErrorMotor
from .textos import t

# Frecuencia de trabajo: la de los modelos de voz. Basta para la voz y hace WORLD más rápido.
FRECUENCIA = 24000


def _world():
    try:
        import pyworld
    except ImportError:
        raise ErrorMotor("falta_pyworld",
                         t("Falta una pieza del motor principal para fabricar susurros: actualízalo desde Componentes.")) from None
    return pyworld


def _leer(ruta):
    import numpy as np
    import soundfile as sf
    from scipy.signal import resample_poly

    if not os.path.isfile(ruta):
        raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=ruta))
    x, sr = sf.read(ruta, dtype="float64", always_2d=True)
    x = x.mean(axis=1)
    if sr != FRECUENCIA:
        x = resample_poly(x, FRECUENCIA, sr)
    return np.ascontiguousarray(x)


def susurrar(entrada, salida, progreso):
    """
    Convierte voz normal en susurro: conserva el timbre y cambia la vibración de las cuerdas vocales por aire.

    Como en un susurro de verdad, los formantes suben un poco (la laringe sube y la boca se abre) y desaparecen los
    graves. Suena algo peor que un susurro grabado, pero el motor lo clona como susurro con la voz de la persona.
    """
    import numpy as np
    import soundfile as sf
    from scipy.signal import butter, sosfilt

    pw = _world()
    progreso(None, t("Fabricando el susurro"))
    x = _leer(entrada)
    f0, tiempos = pw.harvest(x, FRECUENCIA, f0_floor=60, f0_ceil=400, frame_period=5.0)
    envolvente = pw.cheaptrick(x, f0, tiempos, FRECUENCIA)

    # Formantes un 6 % más altos: la envolvente se estira hacia arriba.
    alfa = 1.06
    bandas = np.arange(envolvente.shape[1])
    envolvente = np.array([np.interp(bandas / alfa, bandas, fila) for fila in envolvente])

    # Sin tono (f0 = 0) y todo aire (aperiodicidad 1): WORLD lo genera con ruido filtrado por el timbre.
    y = pw.synthesize(np.zeros_like(f0), envolvente, np.ones_like(envolvente), FRECUENCIA, frame_period=5.0)
    y = sosfilt(butter(4, 300, "highpass", fs=FRECUENCIA, output="sos"), y)
    pico = float(np.max(np.abs(y))) if len(y) else 0.0
    if pico > 0:
        y = y / pico * 0.5
    os.makedirs(os.path.dirname(os.path.abspath(salida)), exist_ok=True)
    sf.write(salida, y.astype(np.float32), FRECUENCIA)
    return {"salida": salida, "duracion": round(len(y) / FRECUENCIA, 2)}
