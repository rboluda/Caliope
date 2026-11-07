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

from . import tarjeta
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
        tarjeta.comprobar(self._torch)
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

    def op_hablantes(self, params, progreso):
        """Quién habla en cada momento de una pista de voz (doblaje con varias personas).

        Huella de voz (el codificador del propio motor) de ventanas de 1,6 s cada 0,5 s sobre lo que suena, y
        k-medias: se acepta el mayor número de personas (hasta «max_hablantes») cuyos grupos se parezcan entre sí
        menos que «umbral» (una persona partida a la fuerza se parece ~0,97; dos personas, ~0,83). Los cambios de
        menos de 1 s no cuentan. Devuelve turnos {inicio, fin, hablante} con las personas numeradas por orden de
        aparición.
        """
        if self._modelo is None:
            raise ErrorMotor("sin_modelo", t("No hay ningún modelo cargado."))
        audio = params["audio"]
        if not os.path.isfile(audio):
            raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=audio))
        import librosa
        import numpy as np
        import soundfile as sf
        from chatterbox.models.voice_encoder.melspec import melspectrogram

        maximo = int(params.get("max_hablantes") or 4)
        umbral = float(params.get("umbral") or 0.87)
        progreso(None, t("Distinguiendo a las personas que hablan"))
        inicio = time.perf_counter()
        ve = self._modelo.ve
        sr = ve.hp.sample_rate
        datos, origen = sf.read(audio, dtype="float32", always_2d=True)
        datos = datos.mean(axis=1)
        if origen != sr:
            datos = librosa.resample(datos, orig_sr=origen, target_sr=sr)

        # Qué suena (mismo criterio que al cortar las frases) y el espectro de todo el audio de una vez.
        voz = np.zeros(len(datos), dtype=bool)
        for a, b in librosa.effects.split(datos, top_db=38, frame_length=1024, hop_length=256):
            voz[a:b] = True
        mel = melspectrogram(datos, ve.hp).T  # (tramas, mels), 100 tramas por segundo
        tramas, paso = ve.hp.ve_partial_frames, int(round(0.5 * sr / ve.hp.hop_size))
        ventanas, centros = [], []
        for k in range(0, max(0, len(mel) - tramas), paso):
            a, b = k * ve.hp.hop_size, (k + tramas) * ve.hp.hop_size
            if voz[a:b].mean() >= 0.7:
                ventanas.append(mel[k:k + tramas])
                centros.append((a + b) / 2 / sr)
        if len(ventanas) < 4:
            return {"hablantes": 1, "turnos": [{"inicio": 0.0, "fin": round(len(datos) / sr, 2), "hablante": 1}],
                    "segundos": round(time.perf_counter() - inicio, 1)}
        torch = self._torch
        huellas = []
        with torch.inference_mode():
            for k in range(0, len(ventanas), 64):
                lote = torch.from_numpy(np.stack(ventanas[k:k + 64]).astype(np.float32)).to(self.dispositivo)
                huellas.append(ve(lote).cpu().numpy())
        X = np.concatenate(huellas)
        X /= np.linalg.norm(X, axis=1, keepdims=True)

        etiquetas = np.zeros(len(X), dtype=int)
        for n in range(2, maximo + 1):
            centros_k, e = _kmedias(X, n)
            parecido = centros_k @ centros_k.T
            if np.max(parecido[~np.eye(n, dtype=bool)]) >= umbral or min(np.bincount(e, minlength=n)) < 6:
                break
            etiquetas = e

        # Cambios de menos de 1 s (2 ventanas) no cuentan: pasan a la persona de al lado.
        cambiado = True
        while cambiado:
            cambiado = False
            k = 0
            while k < len(etiquetas):
                j = k
                while j + 1 < len(etiquetas) and etiquetas[j + 1] == etiquetas[k]:
                    j += 1
                if j - k + 1 < 2 and (k > 0 or j + 1 < len(etiquetas)):
                    etiquetas[k:j + 1] = etiquetas[k - 1] if k > 0 else etiquetas[j + 1]
                    cambiado = True
                k = j + 1

        # Turnos: ventanas seguidas de la misma persona (una pausa de más de 0,6 s también corta).
        turnos = []
        medio = 0.25
        for c, e in zip(centros, etiquetas):
            if turnos and turnos[-1][2] == e and c - medio - turnos[-1][1] <= 0.6:
                turnos[-1][1] = c + medio
            else:
                turnos.append([c - medio, c + medio, e])
        numero = {}
        for _, _, e in turnos:
            numero.setdefault(int(e), len(numero) + 1)
        return {
            "hablantes": len(numero),
            "turnos": [{"inicio": round(a, 2), "fin": round(b, 2), "hablante": numero[int(e)]} for a, b, e in turnos],
            "segundos": round(time.perf_counter() - inicio, 1),
        }

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


def _kmedias(X, n, intentos=8, iteraciones=50, semilla=0):
    """k-medias sobre huellas normalizadas (similitud del coseno). Devuelve (centros normalizados, etiquetas)."""
    import numpy as np

    azar = np.random.default_rng(semilla)
    mejor = None
    for _ in range(intentos):
        centros = [X[azar.integers(len(X))]]
        for _ in range(1, n):
            distancia = 1 - np.max(X @ np.stack(centros).T, axis=1)
            p = np.maximum(distancia, 0) ** 2
            centros.append(X[azar.choice(len(X), p=p / p.sum()) if p.sum() > 0 else azar.integers(len(X))])
        centros = np.stack(centros)
        for _ in range(iteraciones):
            etiquetas = np.argmax(X @ centros.T, axis=1)
            nuevos = np.stack([X[etiquetas == k].mean(axis=0) if np.any(etiquetas == k) else centros[k] for k in range(n)])
            nuevos /= np.linalg.norm(nuevos, axis=1, keepdims=True)
            if np.allclose(nuevos, centros):
                break
            centros = nuevos
        coste = np.sum(1 - np.max(X @ centros.T, axis=1))
        if mejor is None or coste < mejor[0]:
            mejor = (coste, centros, np.argmax(X @ centros.T, axis=1))
    return mejor[1], mejor[2]
