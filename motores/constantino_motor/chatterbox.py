"""Adaptador de Chatterbox Multilingual (Resemble AI): clona la voz de una grabación y lee el texto.

Mismo protocolo que MotorQwen. Diferencias: no usa la transcripción de la referencia, no diseña voces ni
transcribe, y admite «intensidad» (exaggeration: cuánto se exagera la emoción) y «cfg» (cuánto se ciñe a la
referencia; más bajo, más lento y dramático).
"""
import gc
import os
import random
import time

from .servidor import ErrorMotor

GIB = 1024 ** 3

# Del repositorio solo se descargan los archivos del modelo multilingüe (el resto son otras versiones: 12 GB).
ARCHIVOS = ["ve.pt", "t3_mtl23ls_v2.safetensors", "s3gen.pt", "grapheme_mtl_merged_expanded_v1.json", "conds.pt",
            "Cangjie5_TC.json"]

# Idiomas de la aplicación → códigos de Chatterbox.
IDIOMAS = {
    "Spanish": "es", "English": "en", "French": "fr", "German": "de", "Italian": "it", "Portuguese": "pt",
    "Russian": "ru", "Chinese": "zh", "Japanese": "ja", "Korean": "ko",
}


class MotorChatterbox:
    nombre = "chatterbox"

    def __init__(self):
        import torch

        self._torch = torch
        self._modelo = None
        self._cargado = None  # (repositorio, revision)
        self._condiciones = {}  # caché de la voz preparada por referencia
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
            raise ErrorMotor("no_soportado", "Chatterbox solo genera voz.")
        if self._cargado == (repositorio, revision):
            return {"segundos": 0.0, "ya_cargado": True, **self._memoria()}
        self._liberar()

        progreso(None, "Cargando Chatterbox")
        from huggingface_hub import snapshot_download

        ruta = snapshot_download(repo_id=repositorio, revision=revision, allow_patterns=ARCHIVOS,
                                 local_files_only=True)
        inicio = time.perf_counter()
        import perth
        from chatterbox.mtl_tts import ChatterboxMultilingualTTS

        # Si a perth le falta algo (p. ej. setuptools), se carga sin marca de agua y Chatterbox falla al generar.
        if perth.PerthImplicitWatermarker is None:
            raise ErrorMotor("instalacion_incompleta", "Falta parte de Chatterbox: repáralo desde Componentes.")

        self._modelo = ChatterboxMultilingualTTS.from_local(ruta, self.dispositivo)
        self._cargado = (repositorio, revision)
        return {"segundos": round(time.perf_counter() - inicio, 1), "ya_cargado": False, **self._memoria()}

    def op_descargar(self, params, progreso):
        self._liberar()
        return self._memoria()

    def op_sintetizar(self, params, progreso):
        """Clona la voz de la referencia y lee el texto. Escribe un WAV en params['salida']."""
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", "No hay ningún modelo cargado.")
        referencia = params["referencia_wav"]
        if not os.path.isfile(referencia):
            raise ErrorMotor("archivo_no_encontrado", f"No existe la grabación de referencia: {referencia}")
        texto = (params.get("texto") or "").strip()
        if not texto:
            raise ErrorMotor("texto_vacio", "No hay texto que leer.")
        idioma = IDIOMAS.get(params.get("idioma") or "Spanish")
        if idioma is None:
            raise ErrorMotor("idioma_no_soportado", f"Chatterbox no sabe leer en {params.get('idioma')}.")

        intensidad = float(params.get("intensidad") or 0.5)
        progreso(None, "Preparando la voz")
        self._modelo.conds = self._condiciones_de(referencia, intensidad)

        ajustes = {"exaggeration": intensidad}
        for clave_app, clave_cb in (("cfg", "cfg_weight"), ("temperatura", "temperature"), ("top_p", "top_p"),
                                    ("repeticion", "repetition_penalty")):
            if params.get(clave_app) is not None:
                ajustes[clave_cb] = float(params[clave_app])

        self._sembrar(int(params.get("semilla", 1234)))
        progreso(None, "Generando la voz")
        inicio = time.perf_counter()
        wav = self._modelo.generate(texto, language_id=idioma, **ajustes)
        segundos = time.perf_counter() - inicio

        import soundfile as sf

        audio = wav.squeeze(0).numpy()
        sr = self._modelo.sr
        salida = params["salida"]
        os.makedirs(os.path.dirname(salida), exist_ok=True)
        sf.write(salida, audio, sr)
        return {"salida": salida, "duracion": round(len(audio) / sr, 2), "segundos": round(segundos, 1),
                "frecuencia": sr, **self._memoria(pico=True)}

    def op_disenar(self, params, progreso):
        raise ErrorMotor("no_soportado", "Chatterbox no diseña voces: usa Qwen3-TTS VoiceDesign.")

    def op_transcribir(self, params, progreso):
        raise ErrorMotor("no_soportado", "Chatterbox no transcribe: usa Whisper.")

    # --- utilidades --------------------------------------------------------------------------

    def _condiciones_de(self, referencia, intensidad):
        """La voz se prepara una vez por referencia (y se rehace si el archivo cambia)."""
        clave = (referencia, os.path.getmtime(referencia))
        if clave not in self._condiciones:
            self._modelo.prepare_conditionals(referencia, exaggeration=intensidad)
            self._condiciones[clave] = self._modelo.conds
        return self._condiciones[clave]

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
        self._condiciones.clear()
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
