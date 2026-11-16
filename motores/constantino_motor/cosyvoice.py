"""Conversión de voz con CosyVoice 3 (FunAudioLLM, Alibaba; Apache 2.0).

Toma una grabación y le pone el timbre de otra voz conservando todo lo demás: lo que se dice, el ritmo, las pausas, la
emoción y el acento. Mismo protocolo que MotorQwen. El código de CosyVoice no está en PyPI: el instalador lo deja en
<entorno>/codigo (con Matcha-TTS en third_party).

Las grabaciones largas se trocean por sus silencios (trozos de 25 s como mucho) y cada trozo convertido se coloca en su
sitio: el resultado dura lo mismo que el original.
"""
import gc
import os
import sys
import tempfile
import time

from . import tarjeta
from .servidor import ErrorMotor
from .textos import t

GIB = 1024 ** 3
TROZO_MAXIMO = 25.0


def _sin_normalizador_de_texto():
    """
    CosyVoice prepara al arrancar un normalizador de texto (wetext) que se descarga de ModelScope. La conversión de voz
    no lee textos, así que se pone uno vacío: no hace falta conexión (y ModelScope a veces responde «prohibido»).
    """
    import types

    if "wetext" in sys.modules:
        return

    class Normalizer:
        def __init__(self, *args, **kwargs):
            pass

        def normalize(self, texto):
            return texto

    modulo = types.ModuleType("wetext")
    modulo.Normalizer = Normalizer
    sys.modules["wetext"] = modulo


class MotorCosyVoice:
    nombre = "cosyvoice"

    def __init__(self):
        import torch

        self._torch = torch
        self._modelo = None
        self._cargado = None
        self.dispositivo = "cuda" if torch.cuda.is_available() else "cpu"

    def op_info(self, params, progreso):
        torch = self._torch
        info = {"torch": torch.__version__, "dispositivo": self.dispositivo,
                "modelo": None if self._cargado is None else {"repositorio": self._cargado[0], "revision": self._cargado[1]}}
        if self.dispositivo == "cuda":
            libre, total = torch.cuda.mem_get_info()
            info.update(gpu=torch.cuda.get_device_name(0), vram_total_gib=round(total / GIB, 2), vram_libre_gib=round(libre / GIB, 2))
        return info

    def op_cargar(self, params, progreso):
        tarjeta.comprobar(self._torch)
        repositorio, revision = params["repositorio"], params["revision"]
        if self._cargado == (repositorio, revision):
            return {"segundos": 0.0, "ya_cargado": True, **self._memoria()}
        self._liberar()
        progreso(None, t("Cargando la conversión de voz"))
        from huggingface_hub import snapshot_download

        ruta = snapshot_download(repo_id=repositorio, revision=revision, local_files_only=True)
        codigo = os.path.join(sys.prefix, "codigo")
        if not os.path.isfile(os.path.join(codigo, "cosyvoice", "cli", "cosyvoice.py")):
            raise ErrorMotor("modelo_no_encontrado", t("Falta parte de la conversión de voz: repárala desde Componentes."))
        for ruta_codigo in (codigo, os.path.join(codigo, "third_party", "Matcha-TTS")):
            if ruta_codigo not in sys.path:
                sys.path.append(ruta_codigo)
        inicio = time.perf_counter()
        _sin_normalizador_de_texto()
        from cosyvoice.cli.cosyvoice import AutoModel

        # CosyVoice deja todo el registro en DEBUG al importarse: miles de líneas de numba en el diagnóstico.
        import logging
        logging.getLogger().setLevel(logging.WARNING)
        self._modelo = AutoModel(model_dir=ruta)
        self._cargado = (repositorio, revision)
        return {"segundos": round(time.perf_counter() - inicio, 1), "ya_cargado": False, **self._memoria()}

    def op_descargar(self, params, progreso):
        self._liberar()
        return self._memoria()

    def op_convertir(self, params, progreso):
        """Convierte «origen» al timbre de «voz» y escribe un WAV en «salida»."""
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", t("No hay ningún modelo cargado."))
        origen, voz, salida = params["origen"], params["voz"], params["salida"]
        for ruta in (origen, voz):
            if not os.path.isfile(ruta):
                raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=ruta))
        import librosa
        import numpy as np
        import soundfile as sf

        torch = self._torch
        if self.dispositivo == "cuda":
            torch.cuda.reset_peak_memory_stats()
        sr_origen = 16000
        x, _ = librosa.load(origen, sr=sr_origen, mono=True)
        trozos = self._trozos(x, sr_origen)
        sr = self._modelo.sample_rate
        resultado = np.zeros(int(len(x) / sr_origen * sr) + sr, dtype=np.float32)
        inicio = time.perf_counter()
        with tempfile.TemporaryDirectory() as temporal:
            for k, (a, b) in enumerate(trozos):
                progreso(k / max(len(trozos), 1), t("Convirtiendo la voz: trozo {n} de {total}", n=k + 1, total=len(trozos)))
                ruta = os.path.join(temporal, f"{k:04d}.wav")
                sf.write(ruta, x[a:b], sr_origen)
                partes = [p["tts_speech"] for p in self._modelo.inference_vc(ruta, voz, stream=False)]
                audio = torch.cat(partes, dim=1).squeeze(0).float().cpu().numpy()
                pos = int(a / sr_origen * sr)
                fin = min(len(resultado), pos + len(audio))
                resultado[pos:fin] += audio[:fin - pos]
        largo = int(len(x) / sr_origen * sr)
        resultado = np.clip(resultado[:largo], -1, 1)
        os.makedirs(os.path.dirname(os.path.abspath(salida)), exist_ok=True)
        sf.write(salida, resultado, sr)
        return {"salida": salida, "duracion": round(largo / sr, 2), "segundos": round(time.perf_counter() - inicio, 1),
                "frecuencia": sr, **self._memoria(pico=True)}

    @staticmethod
    def _trozos(x, sr):
        """Tramos de voz agrupados en trozos de TROZO_MAXIMO segundos como mucho, cortando siempre en un silencio."""
        import librosa

        tramos = librosa.effects.split(x, top_db=40, frame_length=1024, hop_length=256)
        if len(tramos) == 0:
            return [(0, len(x))]
        trozos = []
        a, b = int(tramos[0][0]), int(tramos[0][1])
        for c, d in tramos[1:]:
            if (int(d) - a) / sr <= TROZO_MAXIMO:
                b = int(d)
            else:
                trozos.append((a, b))
                a, b = int(c), int(d)
        trozos.append((a, b))
        # Un poco de margen a cada lado para no comerse el principio o el final de las palabras.
        margen = int(0.05 * sr)
        return [(max(0, a - margen), min(len(x), b + margen)) for a, b in trozos]

    def _liberar(self):
        self._modelo = None
        self._cargado = None
        gc.collect()
        if self.dispositivo == "cuda":
            self._torch.cuda.empty_cache()

    def _memoria(self, pico=False):
        if self.dispositivo != "cuda":
            return {}
        torch = self._torch
        datos = {"vram_usada_gib": round(torch.cuda.memory_allocated() / GIB, 2)}
        if pico:
            datos["vram_pico_gib"] = round(torch.cuda.max_memory_reserved() / GIB, 2)
        return datos
