"""Adaptador de Chatterbox Multilingual v3 con el paquete de castellano de España (Resemble AI).

Mismo protocolo que MotorQwen. Diferencias: no usa la transcripción de la referencia, no diseña voces ni
transcribe, solo lee en español, y admite «intensidad» (exaggeration: cuánto se exagera la emoción) y «cfg»
(cuánto se ciñe a la referencia; más bajo, más lento y dramático).

El modelo se monta a mano porque viene en dos repositorios: del principal, el codificador de voz (ve.pt) y la voz
por defecto (conds.pt); del paquete de castellano, el modelo de texto a voz (t3_es_es) y el decodificador v3.
"""
import gc
import os
import random
import time

from .servidor import ErrorMotor
from .textos import t

GIB = 1024 ** 3

# Del repositorio principal (el que pide la aplicación al cargar) solo hacen falta estos dos archivos.
ARCHIVOS = ["ve.pt", "conds.pt"]

# Paquete de castellano de España. Tiene que coincidir con el paso «modelo» del catálogo.
PAQUETE = "ResembleAI/Chatterbox-Multilingual-es-es"
REVISION_PAQUETE = "7262b364e3222ba9446c9289e7485eadc47ed902"
ARCHIVOS_PAQUETE = ["t3_es_es.safetensors", "s3gen_v3.pt", "grapheme_mtl_merged_expanded_v1.json"]


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
            raise ErrorMotor("no_soportado", t("Chatterbox solo genera voz."))
        if self._cargado == (repositorio, revision):
            return {"segundos": 0.0, "ya_cargado": True, **self._memoria()}
        self._liberar()

        progreso(None, t("Cargando Chatterbox"))
        from huggingface_hub import snapshot_download

        principal = snapshot_download(repo_id=repositorio, revision=revision, allow_patterns=ARCHIVOS,
                                      local_files_only=True)
        paquete = snapshot_download(repo_id=PAQUETE, revision=REVISION_PAQUETE, allow_patterns=ARCHIVOS_PAQUETE,
                                    local_files_only=True)
        inicio = time.perf_counter()
        import perth

        # Si a perth le falta algo (p. ej. setuptools), se carga sin marca de agua y Chatterbox falla al generar.
        if perth.PerthImplicitWatermarker is None:
            raise ErrorMotor("instalacion_incompleta", t("Falta parte de Chatterbox: repáralo desde Componentes."))

        self._modelo = self._montar(principal, paquete)
        self._cargado = (repositorio, revision)
        return {"segundos": round(time.perf_counter() - inicio, 1), "ya_cargado": False, **self._memoria()}

    def _montar(self, principal, paquete):
        import torch
        from safetensors.torch import load_file
        from chatterbox.models.s3gen import S3Gen
        from chatterbox.models.t3 import T3
        from chatterbox.models.t3.modules.t3_config import T3Config
        from chatterbox.models.tokenizers import MTLTokenizer
        from chatterbox.models.voice_encoder import VoiceEncoder
        from chatterbox.mtl_tts import ChatterboxMultilingualTTS, Conditionals

        dispositivo = self.dispositivo
        ubicacion = torch.device("cpu") if dispositivo == "cpu" else None
        ve = VoiceEncoder()
        ve.load_state_dict(torch.load(os.path.join(principal, "ve.pt"), map_location=ubicacion, weights_only=True))
        ve.to(dispositivo).eval()
        t3 = T3(T3Config.multilingual())
        estado = load_file(os.path.join(paquete, "t3_es_es.safetensors"))
        if "model" in estado:
            estado = estado["model"][0]
        t3.load_state_dict(estado)
        t3.to(dispositivo).eval()
        s3 = S3Gen()
        r = s3.load_state_dict(torch.load(os.path.join(paquete, "s3gen_v3.pt"), map_location=ubicacion, weights_only=True),
                               strict=False)
        # El decodificador v3 no guarda las tablas fijas de su tokenizador: se calculan al crearlo.
        faltan = [k for k in r.missing_keys if not k.startswith("tokenizer.")]
        if faltan or r.unexpected_keys:
            raise ErrorMotor("instalacion_incompleta", t("El modelo de Chatterbox no encaja: repáralo desde Componentes."))
        s3.to(dispositivo).eval()
        tokenizador = MTLTokenizer(os.path.join(paquete, "grapheme_mtl_merged_expanded_v1.json"))
        conds = Conditionals.load(os.path.join(principal, "conds.pt"), map_location=ubicacion).to(dispositivo)
        return ChatterboxMultilingualTTS(t3, s3, ve, tokenizador, dispositivo, conds=conds)

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
        if (params.get("idioma") or "Spanish") != "Spanish":
            raise ErrorMotor("idioma_no_soportado",
                             t("Este motor está afinado para castellano: con voces en otros idiomas usa el motor principal o VoxCPM2."))

        intensidad = float(params.get("intensidad") or 0.5)
        progreso(None, t("Preparando la voz"))
        self._modelo.conds = self._condiciones_de(referencia, intensidad)
        return self._generar(texto, intensidad, params, progreso)

    def op_sintetizar_doblaje(self, params, progreso):
        """Doblaje: la pronunciación sale de una voz castellana nativa y el timbre, de la voz original.

        El modelo tiene dos etapas: T3 decide qué se dice y cómo (pronunciación, ritmo) a partir de una muestra de
        habla, y S3Gen pone el timbre a partir de otra. Si las dos salen de una grabación en otro idioma, se copia
        también su acento; así, la grabación original solo aporta el timbre.
        """
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", t("No hay ningún modelo cargado."))
        pronunciacion, timbre = params["referencia_pronunciacion"], params["referencia_timbre"]
        for ruta in (pronunciacion, timbre):
            if not os.path.isfile(ruta):
                raise ErrorMotor("archivo_no_encontrado", t("No existe la grabación de referencia: {ruta}", ruta=ruta))
        texto = (params.get("texto") or "").strip()
        if not texto:
            raise ErrorMotor("texto_vacio", t("No hay texto que leer."))
        intensidad = float(params.get("intensidad") or 0.5)
        progreso(None, t("Preparando la voz"))
        nativa = self._condiciones_de(pronunciacion, intensidad)
        original = self._condiciones_de(timbre, intensidad)
        self._modelo.conds = type(nativa)(nativa.t3, original.gen)
        return self._generar(texto, intensidad, params, progreso)

    def _generar(self, texto, intensidad, params, progreso):
        ajustes = {"exaggeration": intensidad}
        for clave_app, clave_cb in (("cfg", "cfg_weight"), ("temperatura", "temperature"), ("top_p", "top_p"),
                                    ("repeticion", "repetition_penalty")):
            if params.get(clave_app) is not None:
                ajustes[clave_cb] = float(params[clave_app])

        self._sembrar(int(params.get("semilla", 1234)))
        progreso(None, t("Generando la voz"))
        inicio = time.perf_counter()
        wav = self._modelo.generate(texto, language_id="es", **ajustes)
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
        raise ErrorMotor("no_soportado", t("Chatterbox no diseña voces: usa Qwen3-TTS VoiceDesign."))

    def op_transcribir(self, params, progreso):
        raise ErrorMotor("no_soportado", t("Chatterbox no transcribe: usa Whisper."))

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
