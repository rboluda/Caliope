"""Separador de voz (Demucs): divide un audio en la voz y todo lo demás (música, ambiente, efectos).

Mismo protocolo que los demás motores. El modelo (htdemucs) se carga de su carpeta de Hugging Face ya descargada,
sin conexión. No sintetiza, ni diseña, ni transcribe.
"""
import gc
import os
import time

from .servidor import ErrorMotor
from .textos import t

GIB = 1024 ** 3


class MotorSeparador:
    nombre = "separador"

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
            info.update(gpu=torch.cuda.get_device_name(0), vram_total_gib=round(total / GIB, 2),
                        vram_libre_gib=round(libre / GIB, 2))
        return info

    def op_cargar(self, params, progreso):
        repositorio, revision = params["repositorio"], params["revision"]
        if self._cargado == (repositorio, revision):
            return {"segundos": 0.0, "ya_cargado": True}
        self._liberar()
        progreso(None, t("Cargando el separador de voz"))
        inicio = time.perf_counter()
        import yaml
        from demucs.apply import BagOfModels
        from demucs.hf import load_safetensors_model
        from huggingface_hub import hf_hub_download

        # Archivo a archivo: solo se descargan los dos que hacen falta, y huggingface_hub 2.x no deja abrir con
        # snapshot_download (sin conexión) una copia a la que le faltan archivos del repositorio, aunque no se usen.
        def archivo(nombre):
            return hf_hub_download(repo_id=repositorio, filename=nombre, revision=revision, local_files_only=True)

        with open(archivo("htdemucs.yaml"), encoding="utf-8") as f:
            bolsa = yaml.safe_load(f)
        modelos = [load_safetensors_model(archivo(f"{firma}.safetensors")) for firma in bolsa["models"]]
        self._modelo = BagOfModels(modelos, bolsa.get("weights"), bolsa.get("segment"))
        self._modelo.eval()
        self._cargado = (repositorio, revision)
        return {"segundos": round(time.perf_counter() - inicio, 1)}

    def op_descargar(self, params, progreso):
        self._liberar()
        return {}

    def op_separar(self, params, progreso):
        """Escribe params['voces'] (solo la voz) y params['fondo'] (todo lo demás), estéreo, a la frecuencia del modelo."""
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", t("No hay ningún modelo cargado."))
        entrada = params["entrada"]
        if not os.path.isfile(entrada):
            raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=entrada))
        import numpy as np
        import soundfile as sf
        from demucs.apply import apply_model
        from demucs.audio import convert_audio

        torch = self._torch
        sr = self._modelo.samplerate
        datos, origen = sf.read(entrada, dtype="float32", always_2d=True)
        # Canales y frecuencia del modelo (estéreo, 44,1 kHz) con el conversor del propio Demucs.
        mezcla = convert_audio(torch.from_numpy(np.ascontiguousarray(datos.T)), origen, sr, self._modelo.audio_channels)
        progreso(None, t("Separando la voz del fondo"))
        inicio = time.perf_counter()
        # Igual que demucs.api: se normaliza con la media y la desviación de la mezcla en mono.
        referencia = mezcla.mean(0)
        media, desviacion = referencia.mean(), referencia.std() + 1e-8
        with torch.no_grad():
            partes = apply_model(self._modelo, ((mezcla - media) / desviacion)[None], device=self.dispositivo,
                                 split=True, overlap=0.25, progress=False)[0]
        partes = partes * desviacion + media
        indice = self._modelo.sources.index("vocals")
        voces = partes[indice]
        fondo = partes.sum(0) - voces
        for ruta, pista in ((params["voces"], voces), (params["fondo"], fondo)):
            os.makedirs(os.path.dirname(ruta), exist_ok=True)
            sf.write(ruta, pista.T.cpu().numpy(), sr)
        return {"voces": params["voces"], "fondo": params["fondo"], "frecuencia": sr,
                "duracion": round(mezcla.shape[1] / sr, 2), "segundos": round(time.perf_counter() - inicio, 1)}

    def op_sintetizar(self, params, progreso):
        raise ErrorMotor("no_soportado", t("El separador de voz no genera voz."))

    def op_disenar(self, params, progreso):
        raise ErrorMotor("no_soportado", t("El separador de voz no genera voz."))

    def op_transcribir(self, params, progreso):
        raise ErrorMotor("no_soportado", t("El separador de voz no transcribe."))

    def _liberar(self):
        self._modelo = None
        self._cargado = None
        gc.collect()
        if self.dispositivo == "cuda":
            self._torch.cuda.empty_cache()
