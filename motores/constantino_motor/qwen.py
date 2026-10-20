"""Adaptador de Qwen3-TTS: carga un modelo una vez y lo reutiliza entre peticiones."""
import gc
import os
import random
import time

from .servidor import ErrorMotor

GIB = 1024 ** 3


class MotorQwen:
    nombre = "qwen3-tts"

    def __init__(self):
        # Importar torch tarda varios segundos: se hace una vez, al arrancar el proceso.
        import torch

        self._torch = torch
        self._modelo = None
        self._cargado = None  # (repositorio, revision)
        self._prompts = {}  # caché de prompts de clonado por referencia
        self.dispositivo = "cuda:0" if torch.cuda.is_available() else "cpu"

    # --- operaciones -------------------------------------------------------------------------

    def op_info(self, params, progreso):
        torch = self._torch
        info = {
            "torch": torch.__version__,
            "dispositivo": self.dispositivo,
            "modelo": None if self._cargado is None else {"repositorio": self._cargado[0], "revision": self._cargado[1]},
        }
        if self.dispositivo.startswith("cuda"):
            libre, total = torch.cuda.mem_get_info()
            info.update(gpu=torch.cuda.get_device_name(0), vram_total_gib=round(total / GIB, 2),
                        vram_libre_gib=round(libre / GIB, 2))
        return info

    def op_cargar(self, params, progreso):
        repositorio, revision = params["repositorio"], params["revision"]
        if self._cargado == (repositorio, revision):
            return {"segundos": 0.0, "ya_cargado": True, **self._memoria()}
        self._liberar()

        # Importar qwen-tts (y transformers) lleva unos segundos: se avisa antes para que no parezca colgado.
        progreso(None, "Cargando el modelo de voz")
        from huggingface_hub import snapshot_download
        from qwen_tts import Qwen3TTSModel

        ruta = snapshot_download(repo_id=repositorio, revision=revision, local_files_only=True)
        torch = self._torch
        en_gpu = self.dispositivo.startswith("cuda")
        inicio = time.perf_counter()
        self._modelo = Qwen3TTSModel.from_pretrained(
            ruta,
            device_map=self.dispositivo,
            dtype=torch.bfloat16 if en_gpu else torch.float32,
            attn_implementation="sdpa",
        )
        self._cargado = (repositorio, revision)
        return {"segundos": round(time.perf_counter() - inicio, 1), "ya_cargado": False, **self._memoria()}

    def op_descargar(self, params, progreso):
        self._liberar()
        return self._memoria()

    def op_sintetizar(self, params, progreso):
        """Clona la voz de la referencia y lee el texto. Escribe un WAV en params['salida']."""
        self._exigir_modelo("base", "clonar voces")
        referencia = params["referencia_wav"]
        if not os.path.isfile(referencia):
            raise ErrorMotor("archivo_no_encontrado", f"No existe la grabación de referencia: {referencia}")
        texto = self._texto(params)
        progreso(None, "Preparando la voz")
        # Sin transcripción se imita solo el timbre (x-vector): algo menos parecido, pero no necesita el texto.
        prompt = self._prompt(referencia, (params.get("referencia_texto") or "").strip() or None)
        return self._generar(params, progreso, lambda ajustes: self._modelo.generate_voice_clone(
            text=texto, language=params.get("idioma", "Spanish"), voice_clone_prompt=prompt, **ajustes))

    def op_disenar(self, params, progreso):
        """Inventa una voz a partir de una descripción (VoiceDesign) y lee el texto con ella."""
        self._exigir_modelo("voice_design", "diseñar voces")
        texto = self._texto(params)
        instruccion = (params.get("instruccion") or "").strip()
        if not instruccion:
            raise ErrorMotor("sin_descripcion", "Describe la voz que quieres crear.")
        return self._generar(params, progreso, lambda ajustes: self._modelo.generate_voice_design(
            text=texto, language=params.get("idioma", "Spanish"), instruct=instruccion, **ajustes))

    def _exigir_modelo(self, tipo, para):
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", "No hay ningún modelo cargado.")
        if self._modelo.model.tts_model_type != tipo:
            raise ErrorMotor("modelo_incorrecto", f"El modelo cargado no sirve para {para}.")

    @staticmethod
    def _texto(params):
        texto = (params.get("texto") or "").strip()
        if not texto:
            raise ErrorMotor("texto_vacio", "No hay texto que leer.")
        return texto

    def _generar(self, params, progreso, generar):
        """Fija la semilla, aplica los ajustes de muestreo, genera y escribe el WAV."""
        import soundfile as sf

        torch = self._torch
        semilla = int(params.get("semilla", 1234))
        random.seed(semilla)
        torch.manual_seed(semilla)
        if self.dispositivo.startswith("cuda"):
            torch.cuda.manual_seed_all(semilla)
            torch.cuda.reset_peak_memory_stats()

        ajustes = {}
        for clave_app, clave_qwen in (("temperatura", "temperature"), ("top_p", "top_p"), ("top_k", "top_k"),
                                      ("repeticion", "repetition_penalty"), ("max_tokens", "max_new_tokens")):
            if params.get(clave_app) is not None:
                ajustes[clave_qwen] = params[clave_app]

        progreso(None, "Generando la voz")
        inicio = time.perf_counter()
        wavs, sr = generar(ajustes)
        segundos = time.perf_counter() - inicio

        salida = params["salida"]
        os.makedirs(os.path.dirname(salida), exist_ok=True)
        sf.write(salida, wavs[0], sr)
        duracion = len(wavs[0]) / sr
        return {"salida": salida, "duracion": round(duracion, 2), "segundos": round(segundos, 1),
                "frecuencia": sr, **self._memoria(pico=True)}

    # --- utilidades --------------------------------------------------------------------------

    def _prompt(self, referencia, texto):
        """El prompt de clonado se calcula una vez por referencia (y se rehace si el archivo cambia)."""
        clave = (referencia, os.path.getmtime(referencia), texto)
        if clave not in self._prompts:
            self._prompts[clave] = self._modelo.create_voice_clone_prompt(
                ref_audio=referencia, ref_text=texto, x_vector_only_mode=texto is None)
        return self._prompts[clave]

    def _liberar(self):
        self._modelo = None
        self._cargado = None
        self._prompts.clear()
        gc.collect()
        if self.dispositivo.startswith("cuda"):
            self._torch.cuda.empty_cache()

    def _memoria(self, pico=False):
        if not self.dispositivo.startswith("cuda"):
            return {}
        torch = self._torch
        datos = {"vram_usada_gib": round(torch.cuda.memory_allocated() / GIB, 2)}
        if pico:
            datos["vram_pico_gib"] = round(torch.cuda.max_memory_reserved() / GIB, 2)
        return datos
