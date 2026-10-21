"""Adaptador de VoxCPM2 (OpenBMB): clona la voz de una grabación y lee el texto, con audio a 48 kHz.

Mismo protocolo que MotorQwen. Con la transcripción de la referencia imita mejor la voz (la usa como «principio»
que continúa); sin ella, imita solo el timbre. Detecta el idioma del texto solo (30 idiomas). No diseña voces ni
transcribe.
"""
import gc
import os
import random
import time

from .servidor import ErrorMotor
from .textos import t

GIB = 1024 ** 3


class MotorVoxCpm:
    nombre = "voxcpm2"

    def __init__(self):
        import torch

        self._torch = torch
        self._modelo = None
        self._cargado = None  # (repositorio, revision)
        self.dispositivo = "cuda" if torch.cuda.is_available() else "cpu"

    # --- operaciones -------------------------------------------------------------------------

    def op_info(self, params, progreso):
        torch = self._torch
        info = {
            "torch": torch.__version__,
            "dispositivo": self.dispositivo,
            "modelo": None if self._cargado is None else {"repositorio": self._cargado[0], "revision": self._cargado[1]},
        }
        if self.dispositivo == "cuda":
            libre, total = torch.cuda.mem_get_info()
            info.update(gpu=torch.cuda.get_device_name(0), vram_total_gib=round(total / GIB, 2),
                        vram_libre_gib=round(libre / GIB, 2))
        return info

    def op_cargar(self, params, progreso):
        repositorio, revision = params["repositorio"], params["revision"]
        if (params.get("tipo") or "tts") != "tts":
            raise ErrorMotor("no_soportado", t("VoxCPM2 solo genera voz."))
        if self._cargado == (repositorio, revision):
            return {"segundos": 0.0, "ya_cargado": True, **self._memoria()}
        self._liberar()

        progreso(None, t("Cargando VoxCPM2"))
        from huggingface_hub import snapshot_download

        ruta = snapshot_download(repo_id=repositorio, revision=revision, local_files_only=True)
        inicio = time.perf_counter()
        from voxcpm import VoxCPM

        # Sin el limpiador de ruido (necesitaría otro modelo y librerías) y sin torch.compile (no hay triton en Windows).
        self._modelo = VoxCPM.from_pretrained(ruta, load_denoiser=False, optimize=False, device=self.dispositivo)
        self._cargado = (repositorio, revision)
        return {"segundos": round(time.perf_counter() - inicio, 1), "ya_cargado": False, **self._memoria()}

    def op_descargar(self, params, progreso):
        self._liberar()
        return self._memoria()

    def op_sintetizar(self, params, progreso):
        """Clona la voz de la referencia y lee el texto. Escribe un WAV en params['salida']."""
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", t("No hay ningún modelo cargado."))
        referencia = params["referencia_wav"]
        if not os.path.isfile(referencia):
            raise ErrorMotor("archivo_no_encontrado", t("No existe la grabación de referencia: {ruta}", ruta=referencia))
        texto = (params.get("texto") or "").strip()
        if not texto:
            raise ErrorMotor("texto_vacio", t("No hay texto que leer."))

        argumentos = {"reference_wav_path": referencia}
        transcripcion = (params.get("referencia_texto") or "").strip()
        if transcripcion:
            # Con la transcripción, la grabación sirve también de principio que el modelo continúa: se parece más.
            argumentos.update(prompt_wav_path=referencia, prompt_text=transcripcion)

        self._sembrar(int(params.get("semilla", 1234)))
        progreso(None, t("Generando la voz"))
        inicio = time.perf_counter()
        audio = self._modelo.generate(text=texto, cfg_value=float(params.get("cfg") or 2.0), inference_timesteps=10,
                                      **argumentos)
        segundos = time.perf_counter() - inicio

        import soundfile as sf

        sr = self._modelo.tts_model.sample_rate
        salida = params["salida"]
        os.makedirs(os.path.dirname(salida), exist_ok=True)
        sf.write(salida, audio, sr)
        return {"salida": salida, "duracion": round(len(audio) / sr, 2), "segundos": round(segundos, 1),
                "frecuencia": sr, **self._memoria(pico=True)}

    def op_disenar(self, params, progreso):
        raise ErrorMotor("no_soportado", t("VoxCPM2 no diseña voces en Calíope: usa el diseñador de voces."))

    def op_transcribir(self, params, progreso):
        raise ErrorMotor("no_soportado", t("VoxCPM2 no transcribe: usa la transcripción automática."))

    # --- utilidades --------------------------------------------------------------------------

    def _sembrar(self, semilla):
        import numpy as np

        torch = self._torch
        random.seed(semilla)
        np.random.seed(semilla)
        torch.manual_seed(semilla)
        if self.dispositivo == "cuda":
            torch.cuda.manual_seed_all(semilla)
            torch.cuda.reset_peak_memory_stats()

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
