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


def analizar(entrada):
    """Mide una voz: su tono medio (Hz), qué parte suena con tono (0–1; un susurro, casi nada) y su duración."""
    import numpy as np

    pw = _world()
    x = _leer(entrada)
    f0, _ = pw.harvest(x, FRECUENCIA, f0_floor=55, f0_ceil=500, frame_period=10.0)
    sonoros = f0[f0 > 0]
    movimiento = 0.0
    if len(sonoros) > 10:
        st = 12 * np.log2(sonoros / np.median(sonoros))
        movimiento = float(np.percentile(st, 90) - np.percentile(st, 10))
    return {
        "tono_hz": round(float(np.median(sonoros)), 1) if len(sonoros) else 0.0,
        "movimiento": round(movimiento, 2),
        "sonoridad": round(len(sonoros) / max(len(f0), 1), 3),
        "duracion": round(len(x) / FRECUENCIA, 2),
    }


def prosodia(audio, tramos):
    """Cómo suena cada tramo (para que el doblaje copie la expresión): volumen (dB), tono medio (Hz), movimiento de la
    entonación (semitonos entre los percentiles 10 y 90) y qué parte tiene tono (0–1)."""
    import numpy as np

    pw = _world()
    x = _leer(audio)
    resultado = []
    for tramo in tramos:
        a = max(0, int(float(tramo["inicio"]) * FRECUENCIA))
        b = min(len(x), int(float(tramo["fin"]) * FRECUENCIA))
        trozo = np.ascontiguousarray(x[a:b])
        if len(trozo) < FRECUENCIA // 5:
            resultado.append({"volumen_db": -90.0, "tono_hz": 0.0, "movimiento": 0.0, "sonoridad": 0.0})
            continue
        volumen = 20 * np.log10(max(float(np.sqrt(np.mean(trozo ** 2))), 1e-6))
        f0, _ = pw.harvest(trozo, FRECUENCIA, f0_floor=55, f0_ceil=500, frame_period=10.0)
        sonoros = f0[f0 > 0]
        movimiento = 0.0
        if len(sonoros) > 10:
            st = 12 * np.log2(sonoros / np.median(sonoros))
            movimiento = float(np.percentile(st, 90) - np.percentile(st, 10))
        resultado.append({"volumen_db": round(volumen, 1), "tono_hz": round(float(np.median(sonoros)), 1) if len(sonoros) else 0.0,
                          "movimiento": round(movimiento, 2), "sonoridad": round(len(sonoros) / max(len(f0), 1), 3)})
    return {"tramos": resultado}


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
