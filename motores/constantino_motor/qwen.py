"""Adaptador de Qwen3-TTS (y Whisper para transcribir): carga un modelo a la vez y lo reutiliza entre peticiones."""
import gc
import os
import random
import time

from . import tarjeta
from .servidor import ErrorMotor
from .textos import t

GIB = 1024 ** 3


class MotorQwen:
    nombre = "qwen3-tts"

    def __init__(self):
        # Importar torch tarda varios segundos: se hace una vez, al arrancar el proceso.
        import torch

        self._torch = torch
        self._modelo = None
        self._tipo = None  # "base", "voice_design" o "asr"
        self._cargado = None  # (repositorio, revision)
        self._prompts = {}  # caché de prompts de clonado por referencia
        self.dispositivo = "cuda:0" if torch.cuda.is_available() else "cpu"
        self._media_precision = tarjeta.media_precision(torch)

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
        """Carga un modelo de voz (tipo «tts») o de transcripción (tipo «asr»). Solo hay uno en memoria a la vez."""
        tarjeta.comprobar(self._torch)
        repositorio, revision = params["repositorio"], params["revision"]
        tipo = params.get("tipo") or "tts"
        if self._cargado == (repositorio, revision):
            return {"segundos": 0.0, "ya_cargado": True, **self._memoria()}
        self._liberar()

        # Importar qwen-tts (y transformers) lleva unos segundos: se avisa antes para que no parezca colgado.
        progreso(None, t("Cargando el modelo de transcripción") if tipo == "asr" else t("Cargando el modelo de voz"))
        from huggingface_hub import snapshot_download

        ruta = snapshot_download(repo_id=repositorio, revision=revision, local_files_only=True)
        inicio = time.perf_counter()
        if tipo == "asr":
            self._modelo = self._cargar_whisper(ruta)
            self._tipo = "asr"
        else:
            from qwen_tts import Qwen3TTSModel

            torch = self._torch
            self._modelo = Qwen3TTSModel.from_pretrained(
                ruta,
                device_map=self.dispositivo,
                dtype=torch.bfloat16 if self._media_precision else torch.float32,
                attn_implementation="sdpa",
            )
            self._tipo = self._modelo.model.tts_model_type
        self._cargado = (repositorio, revision)
        return {"segundos": round(time.perf_counter() - inicio, 1), "ya_cargado": False, **self._memoria()}

    def _cargar_whisper(self, ruta):
        from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline

        torch = self._torch
        dtype = torch.float16 if self._media_precision else torch.float32
        modelo = AutoModelForSpeechSeq2Seq.from_pretrained(ruta, dtype=dtype, low_cpu_mem_usage=True).to(self.dispositivo)
        procesador = AutoProcessor.from_pretrained(ruta)
        return pipeline("automatic-speech-recognition", model=modelo, tokenizer=procesador.tokenizer,
                        feature_extractor=procesador.feature_extractor, dtype=dtype, device=self.dispositivo)

    def op_transcribir(self, params, progreso):
        """Transcribe un audio (hasta unos minutos) en el idioma indicado."""
        self._exigir_modelo("asr", t("transcribir"))
        audio = params["audio"]
        if not os.path.isfile(audio):
            raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=audio))
        import librosa
        import soundfile as sf

        datos, sr = sf.read(audio, dtype="float32", always_2d=True)
        datos = datos.mean(axis=1)
        if sr != 16000:
            datos = librosa.resample(datos, orig_sr=sr, target_sr=16000)
        progreso(None, t("Transcribiendo"))
        inicio = time.perf_counter()
        idioma = (params.get("idioma") or "Spanish").lower()
        resultado = self._modelo({"array": datos, "sampling_rate": 16000}, chunk_length_s=30,
                                 generate_kwargs={"language": idioma, "task": "transcribe"})
        return {"texto": resultado["text"].strip(), "segundos": round(time.perf_counter() - inicio, 1)}

    def op_transcribir_tramos(self, params, progreso):
        """Corta la voz por sus pausas y transcribe cada trozo: frases con su inicio y fin exactos.

        Con audios largos, Whisper con marcas de tiempo pierde los tiempos (devuelve trozos de minutos); cortar antes
        por silencios da frases de 1–15 s, que es lo que necesita el doblaje. «cortes» son instantes donde se corta
        aunque no haya pausa (donde cambia la persona que habla), para que cada frase sea de una sola persona. Con
        «unir_cortos» (actas), los trozos de menos de 1 s se juntan con el vecino: solos, Whisper se inventa frases.
        """
        self._exigir_modelo("asr", t("transcribir"))
        audio = params["audio"]
        if not os.path.isfile(audio):
            raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=audio))
        import librosa
        import numpy as np
        import soundfile as sf

        sr = 16000
        datos, origen = sf.read(audio, dtype="float32", always_2d=True)
        datos = datos.mean(axis=1)
        if origen != sr:
            datos = librosa.resample(datos, orig_sr=origen, target_sr=sr)
        pausa = float(params.get("pausa_minima") or 0.35)
        maximo = float(params.get("maximo") or 15.0)
        progreso(None, t("Buscando las frases"))
        tramos = []
        for a, b in librosa.effects.split(datos, top_db=38, frame_length=1024, hop_length=256):
            if tramos and (a - tramos[-1][1]) / sr < pausa:
                tramos[-1][1] = b
            else:
                tramos.append([a, b])
        # Donde cambia quien habla también se corta (si no queda un trozo de menos de 0,3 s).
        for c in sorted(int(float(x) * sr) for x in (params.get("cortes") or [])):
            for k, (a, b) in enumerate(tramos):
                if a + 0.3 * sr < c < b - 0.3 * sr:
                    tramos[k:k + 1] = [[a, c], [c, b]]
                    break
        frases, pendientes = [], [(a, b) for a, b in tramos if (b - a) / sr >= 0.25]
        while pendientes:
            a, b = pendientes.pop(0)
            if (b - a) / sr <= maximo:
                frases.append((a, b))
                continue
            # Las demasiado largas se parten por su punto más silencioso (en la mitad central).
            rms = librosa.feature.rms(y=datos[a:b], frame_length=1024, hop_length=256)[0]
            q = len(rms) // 4
            corte = a + (q + int(np.argmin(rms[q:3 * q]))) * 256
            pendientes[:0] = [(a, corte), (corte, b)]
        if params.get("unir_cortos"):
            frases = _unir_cortos(sorted(frases), sr, maximo)
        progreso(None, t("Transcribiendo"))
        inicio = time.perf_counter()
        idioma = (params.get("idioma") or "English").lower()
        textos = self._modelo([{"array": datos[a:b], "sampling_rate": sr} for a, b in frases], batch_size=8,
                              generate_kwargs={"language": idioma, "task": "transcribe"}) if frases else []
        resultado = []
        for (a, b), r in zip(frases, textos):
            if texto := _limpiar(r["text"], (b - a) / sr, bool(params.get("unir_cortos"))):
                resultado.append({"inicio": round(a / sr, 2), "fin": round(b / sr, 2), "texto": texto})
        return {"frases": resultado, "duracion": round(len(datos) / sr, 2), "segundos": round(time.perf_counter() - inicio, 1)}

    def op_hablantes_tramos(self, params, progreso):
        """Quién dice cada tramo de una reunión: huella de voz (x-vector del modelo Base) y k-medias.

        La huella de cada tramo es la media de ventanas de 3 s. Con «personas», se buscan exactamente esas; si no, el
        mayor número (2–8) en el que los grupos sigan bien separados (silueta ≥ 0,4) y cada persona tenga al menos el
        10 % del tiempo de voz (probado con una reunión real: la silueta sola se queda en 2). Los tramos de menos de
        1 s toman la persona del tramo más cercano. Devuelve una persona por tramo, numeradas por orden de aparición.
        """
        self._exigir_modelo("base", t("distinguir a las personas"))
        audio = params["audio"]
        if not os.path.isfile(audio):
            raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=audio))
        import librosa
        import numpy as np
        import soundfile as sf
        from sklearn.cluster import KMeans
        from sklearn.metrics import silhouette_score

        progreso(None, t("Distinguiendo a las personas que hablan"))
        inicio = time.perf_counter()
        sr = 24000
        datos, origen = sf.read(audio, dtype="float32", always_2d=True)
        datos = datos.mean(axis=1)
        if origen != sr:
            datos = librosa.resample(datos, orig_sr=origen, target_sr=sr)
        tramos = params["tramos"]
        modelo = self._modelo.model

        huellas = {}
        for i, tramo in enumerate(tramos):
            a, b = float(tramo["inicio"]), float(tramo["fin"])
            if b - a < 1.0:
                continue
            ventanas = [(x, min(b, x + 3)) for x in np.arange(a, max(b - 1, a + 0.01), 3)]
            v = np.mean([modelo.extract_speaker_embedding(datos[int(x * sr):int(y * sr)], sr).float().cpu().numpy()
                         for x, y in ventanas if y - x >= 1.0] or
                        [modelo.extract_speaker_embedding(datos[int(a * sr):int(b * sr)], sr).float().cpu().numpy()], axis=0)
            huellas[i] = v / np.linalg.norm(v)
        ids = sorted(huellas)
        if len(ids) < 3:
            return {"personas": 1, "hablantes": [1] * len(tramos), "segundos": round(time.perf_counter() - inicio, 1)}
        X = np.stack([huellas[i] for i in ids])
        pesos = np.array([tramos[i]["fin"] - tramos[i]["inicio"] for i in ids])

        def agrupar(n):
            return KMeans(n, n_init=20, random_state=0).fit_predict(X)

        pedidas = params.get("personas")
        if pedidas:
            n = max(1, min(int(pedidas), len(ids)))
            etiquetas = agrupar(n) if n > 1 else np.zeros(len(ids), dtype=int)
        else:
            n, etiquetas = 1, np.zeros(len(ids), dtype=int)
            for k in range(2, min(8, len(ids) - 1) + 1):
                e = agrupar(k)
                segundos = [pesos[e == j].sum() for j in range(k)]
                if silhouette_score(X, e, metric="cosine") >= 0.4 and min(segundos) >= 0.1 * sum(segundos):
                    n, etiquetas = k, e

        persona = {i: int(e) for i, e in zip(ids, etiquetas)}
        for i in range(len(tramos)):
            if i not in persona:
                centro = tramos[i]["inicio"] + tramos[i]["fin"]
                cercano = min(ids, key=lambda j: abs(tramos[j]["inicio"] + tramos[j]["fin"] - centro))
                persona[i] = persona[cercano]
        numero = {}
        for i in range(len(tramos)):
            numero.setdefault(persona[i], len(numero) + 1)
        return {"personas": len(numero), "hablantes": [numero[persona[i]] for i in range(len(tramos))],
                "segundos": round(time.perf_counter() - inicio, 1)}

    def op_descargar(self, params, progreso):
        self._liberar()
        return self._memoria()

    def op_sintetizar(self, params, progreso):
        """Clona la voz de la referencia y lee el texto. Escribe un WAV en params['salida']."""
        self._exigir_modelo("base", t("clonar voces"))
        referencia = params["referencia_wav"]
        if not os.path.isfile(referencia):
            raise ErrorMotor("archivo_no_encontrado", t("No existe la grabación de referencia: {ruta}", ruta=referencia))
        texto = self._texto(params)
        progreso(None, t("Preparando la voz"))
        # Sin transcripción se imita solo el timbre (x-vector): algo menos parecido, pero no necesita el texto.
        prompt = self._prompt(referencia, (params.get("referencia_texto") or "").strip() or None)
        return self._generar(params, progreso, lambda ajustes: self._modelo.generate_voice_clone(
            text=texto, language=params.get("idioma", "Spanish"), voice_clone_prompt=prompt, **ajustes))

    def op_disenar(self, params, progreso):
        """Inventa una voz a partir de una descripción (VoiceDesign) y lee el texto con ella."""
        self._exigir_modelo("voice_design", t("diseñar voces"))
        texto = self._texto(params)
        instruccion = (params.get("instruccion") or "").strip()
        if not instruccion:
            raise ErrorMotor("sin_descripcion", t("Describe la voz que quieres crear."))
        return self._generar(params, progreso, lambda ajustes: self._modelo.generate_voice_design(
            text=texto, language=params.get("idioma", "Spanish"), instruct=instruccion, **ajustes))

    def op_susurrar(self, params, progreso):
        """Convierte la grabación de una voz en susurro (WORLD), para su variante «susurro». No necesita modelo."""
        from .retoque import susurrar

        return susurrar(params["entrada"], params["salida"], progreso)

    def _exigir_modelo(self, tipo, para):
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", t("No hay ningún modelo cargado."))
        if self._tipo != tipo:
            raise ErrorMotor("modelo_incorrecto", t("El modelo cargado no sirve para {para}.", para=para))

    @staticmethod
    def _texto(params):
        texto = (params.get("texto") or "").strip()
        if not texto:
            raise ErrorMotor("texto_vacio", t("No hay texto que leer."))
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

        progreso(None, t("Generando la voz"))
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
        self._tipo = None
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


# Lo que Whisper se inventa en silencios y ruidos (aprendido de subtítulos de YouTube).
_INVENTADAS = None


def _limpiar(texto, segundos, inventadas):
    """Quita bucles («no, no, no…» infinito) y, con <inventadas>, las frases que Whisper se inventa en trozos cortos
    (en un vídeo, «Thanks for watching!» puede ser real; en una reunión en castellano, no). "" si no queda nada."""
    import re

    global _INVENTADAS
    if _INVENTADAS is None:
        _INVENTADAS = re.compile(
            r"^(¡?gracias por ver el v[ií]deo!?|subt[ií]tulos? (realizados|por) .*|suscr[ií]bete.*|cc por .*|amara\.org.*)$", re.I)
    texto = texto.strip()
    # Una palabra o un grupo de hasta 4 repetido más de 3 veces seguidas se deja en 2.
    texto = re.sub(r"\b((?:\w+[,.]?\s+){1,4}?)\1{3,}", r"\1\1", texto + " ").strip()
    if inventadas and _INVENTADAS.match(texto) and segundos < 2.5:
        return ""
    return texto


def _unir_cortos(frases, sr, maximo):
    """Los trozos de menos de 1 s se unen al vecino más cercano si la pausa es corta (< 0,8 s) y no pasan del máximo."""
    frases = list(frases)
    i = 0
    while i < len(frases):
        a, b = frases[i]
        if (b - a) / sr >= 1.0 or len(frases) == 1:
            i += 1
            continue
        anterior = (a - frases[i - 1][1]) / sr if i > 0 else 1e9
        siguiente = (frases[i + 1][0] - b) / sr if i + 1 < len(frases) else 1e9
        if anterior <= siguiente and anterior < 0.8 and (b - frases[i - 1][0]) / sr <= maximo:
            frases[i - 1] = (frases[i - 1][0], b)
            del frases[i]
        elif siguiente < 0.8 and (frases[i + 1][1] - a) / sr <= maximo:
            frases[i + 1] = (a, frases[i + 1][1])
            del frases[i]
        else:
            i += 1
    return frases
