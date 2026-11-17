"""Voz en directo (beta): hablas por el micrófono y sale con otra voz de tu biblioteca, casi al instante.

Dos convertidores, a elegir:
  · «calidad»: Seed-VC, modelo pequeño (seed-uvit-tat-xlsr-tiny, del fork seed-vc-realtime; GPL-3.0, se descarga aparte
    y se usa sin modificar). Más natural; necesita tarjeta gráfica NVIDIA. Retraso del orden de medio segundo.
  · «ligero»: MeanVC2 (ASLP-lab; Apache 2.0). Va en el procesador, con un solo núcleo, y con menos retraso (unos
    200 ms); conserva mejor la zeta de España, aunque suena algo menos natural.

El audio no pasa por la aplicación: este proceso abre el micrófono (o lee un archivo, para probar sin micrófono) y
escribe en una o dos salidas a la vez (los auriculares para oírte y la «Salida de Calíope» para Teams o Zoom).
Las operaciones vuelven enseguida: el audio corre en hilos propios, y la aplicación pregunta el «estado» cada segundo.

Medidas de la fase 0 (RTX 3050, 17/11/2026, lectura con acento de España):
  · MeanVC2: 0,33 del tiempo real en un núcleo; Whisper la entiende entera; conserva la zeta (−12/−13 dB, como el
    original); parecido con la voz destino 0,955.
  · Seed-VC (bloques de 0,25 s, 6 pasos): 130 ms por bloque; Whisper la entiende entera; la zeta se debilita
    (−6 dB); parecido 0,966. En la escucha a ciegas gustó más.
"""
import collections
import os
import sys
import threading
import time

import numpy as np

from .servidor import ErrorMotor
from .textos import t

GIB = 1024 ** 3

# Configuración de WavLM-Large (la del modelo publicado por Microsoft). MeanVC2 solo necesita esta configuración: los
# pesos salen del codificador de hablante ajustado (wavlm_large_finetune.pth). Así no hay que bajar 1,2 GB para nada.
WAVLM_LARGE_CFG = {
    'extractor_mode': 'layer_norm', 'encoder_layers': 24, 'encoder_embed_dim': 1024, 'encoder_ffn_embed_dim': 4096,
    'encoder_attention_heads': 16, 'activation_fn': 'gelu', 'dropout': 0.0, 'attention_dropout': 0.0,
    'activation_dropout': 0.0, 'encoder_layerdrop': 0.0, 'dropout_input': 0.0, 'dropout_features': 0.0,
    'layer_norm_first': True, 'conv_feature_layers': '[(512,10,5)] + [(512,3,2)] * 4 + [(512,2,2)] * 2',
    'conv_bias': False, 'feature_grad_mult': 1.0, 'mask_length': 10, 'mask_prob': 0.8, 'mask_selection': 'static',
    'mask_other': 0.0, 'no_mask_overlap': False, 'mask_min_space': 1, 'mask_channel_length': 10,
    'mask_channel_prob': 0.0, 'mask_channel_selection': 'static', 'mask_channel_other': 0.0,
    'no_mask_channel_overlap': False, 'mask_channel_min_space': 1, 'conv_pos': 128, 'conv_pos_groups': 16,
    'relative_position_embedding': True, 'num_buckets': 320, 'max_distance': 800, 'gru_rel_pos': True,
    'normalize': True,
}

# Cómo se reconoce el cable virtual (VB-CABLE) entre los dispositivos, con su nombre de fábrica o el que le pone Calíope.
NOMBRES_CABLE_SALIDA = ("calíope (uso interno)", "caliope (uso interno)", "cable input")


def _archivo_modelo(modelos, repositorio, archivo):
    """Ruta de un archivo de un modelo ya descargado (sin conexión), en la revisión que fija el catálogo."""
    from huggingface_hub import hf_hub_download

    if repositorio not in modelos:
        raise ErrorMotor("modelo_no_encontrado", t("Falta parte de la voz en directo: repárala desde Componentes."))
    return hf_hub_download(repositorio, archivo, revision=modelos[repositorio], local_files_only=True)


def _carpeta_modelo(modelos, repositorio):
    from huggingface_hub import snapshot_download

    if repositorio not in modelos:
        raise ErrorMotor("modelo_no_encontrado", t("Falta parte de la voz en directo: repárala desde Componentes."))
    return snapshot_download(repositorio, revision=modelos[repositorio], local_files_only=True)


class ConversorLigero:
    """MeanVC2 en el procesador: bloques de 160 ms a 16 kHz."""
    nombre = "ligero"

    def __init__(self, modelos, voz):
        import torch

        codigo = os.path.join(sys.prefix, "meanvc2")
        runtime = os.path.join(codigo, "runtime")
        if not os.path.isfile(os.path.join(runtime, "run_rt.py")):
            raise ErrorMotor("modelo_no_encontrado", t("Falta parte de la voz en directo: repárala desde Componentes."))
        if runtime not in sys.path:
            sys.path.insert(0, runtime)
        # La configuración de WavLM, junto al resto del entorno (se crea la primera vez).
        cfg = os.path.join(sys.prefix, "modelos", "wavlm_large_cfg.pt")
        if not os.path.isfile(cfg):
            os.makedirs(os.path.dirname(cfg), exist_ok=True)
            torch.save(WAVLM_LARGE_CFG, cfg)
        finetune = os.path.join(sys.prefix, "modelos", "wavlm_large_finetune.pth")
        if not os.path.isfile(finetune):
            raise ErrorMotor("modelo_no_encontrado", t("Falta parte de la voz en directo: repárala desde Componentes."))

        import run_rt

        run_rt.VOCODER_PATH = _archivo_modelo(modelos, "ASLP-lab/MeanVC2", "vocos.pt")
        run_rt.SPEAKER_MODEL_PATH = finetune
        run_rt.WAVLM_CONFIG_PATH = cfg
        run_rt.MODEL_PATHS["120ms"].update(
            ckpt=_archivo_modelo(modelos, "ASLP-lab/MeanVC2", "meanvc2_120ms_40ms.safetensors"),
            config=os.path.join(runtime, "src", "config", "config_120ms_40ms.json")
            if os.path.isfile(os.path.join(runtime, "src", "config", "config_120ms_40ms.json"))
            else os.path.join(codigo, "src", "config", "config_120ms_40ms.json"),
            asr_ckpt=_archivo_modelo(modelos, "ASLP-lab/MeanVC2", "fastu2pp_160ms.pt"),
        )
        self._run_rt = run_rt
        self._vc = run_rt.VCRunner(target_wav=voz, device="cpu", model="120ms")
        self.sr = 16000
        self.bloque = self._vc.CHUNK
        # 160 ms de bloque + 40 ms de lo que el modelo mira por delante.
        self.retraso_algoritmo = self.bloque / self.sr + 0.04
        self.calentar()

    def calentar(self):
        for _ in range(3):
            self._vc.process_chunk(np.zeros(self.bloque, np.float32))
        self._vc._init_cache()

    def reiniciar(self):
        self._vc._init_cache()

    def cambiar_voz(self, voz):
        import torch

        vc = self._vc
        vc.vc_spk_emb = self._run_rt.extract_embedding(vc.spk_model, voz, device=vc.device)
        with torch.no_grad():
            vc.vc_gtm_kv = vc.vc.gtm(vc.vc_spk_emb)

    def convertir(self, bloque):
        y = self._vc.process_chunk(bloque)
        return np.zeros(0, np.float32) if y is None else y.astype(np.float32)


class ConversorCalidad:
    """Seed-VC (modelo pequeño) en la tarjeta gráfica: bloques de 0,25 s a 22 050 Hz, 6 pasos de difusión."""
    nombre = "calidad"
    TIEMPO_BLOQUE = 0.25
    CONTEXTO_DERECHA = 0.02
    PASOS = 6

    def __init__(self, modelos, voz):
        import types

        import torch
        import yaml

        if not torch.cuda.is_available():
            raise ErrorMotor("necesita_gpu", t("El modo «calidad» necesita una tarjeta gráfica NVIDIA. Usa el modo «ligero»."))
        codigo = os.path.join(sys.prefix, "seedvc")
        if not os.path.isfile(os.path.join(codigo, "realtime_vc_engine.py")):
            raise ErrorMotor("modelo_no_encontrado", t("Falta parte de la voz en directo: repárala desde Componentes."))
        if codigo not in sys.path:
            sys.path.insert(0, codigo)

        # Los modelos ya descargados, sin conexión y en la revisión del catálogo (no en ./checkpoints).
        import hf_utils

        def cargar_de_hf(repo_id, model_filename="pytorch_model.bin", config_filename=None):
            modelo = _archivo_modelo(modelos, repo_id, model_filename)
            return modelo if config_filename is None else (modelo, _archivo_modelo(modelos, repo_id, config_filename))

        hf_utils.load_custom_model_from_hf = cargar_de_hf
        import realtime_vc_engine as rve

        rve.load_custom_model_from_hf = cargar_de_hf
        # El detector de voz (FSMN-VAD), desde su copia en Hugging Face y no desde ModelScope.
        carpeta_vad = _carpeta_modelo(modelos, "funasr/fsmn-vad")
        from funasr import AutoModel

        rve.AutoModel = lambda **kw: AutoModel(model=carpeta_vad, disable_update=True, disable_pbar=True, log_level="ERROR")

        dit, config = cargar_de_hf("Plachta/Seed-VC", "DiT_uvit_tat_xlsr_ema.pth", "config_dit_mel_seed_uvit_xlsr_tiny.yml")
        with open(config, encoding="utf-8") as f:
            ajustes = yaml.safe_load(f)
        ajustes["model_params"]["speech_tokenizer"]["name"] = _carpeta_modelo(modelos, "facebook/wav2vec2-xls-r-300m")
        config_local = os.path.join(sys.prefix, "modelos", "seedvc_xlsr_tiny.yml")
        os.makedirs(os.path.dirname(config_local), exist_ok=True)
        with open(config_local, "w", encoding="utf-8") as f:
            yaml.safe_dump(ajustes, f, allow_unicode=True)

        motor = rve.RealtimeVCEngine(types.SimpleNamespace(checkpoint_path=dit, config_path=config_local, fp16=True, gpu=0))
        # Calíope abre el audio por su cuenta: el motor de Seed-VC solo convierte bloques.
        motor.get_device_channels = lambda: 1
        motor.set_config(reference_audio_path=voz, block_time=self.TIEMPO_BLOQUE, extra_time_right=self.CONTEXTO_DERECHA,
                         diffusion_steps=self.PASOS, inference_cfg_rate=0.7, max_prompt_length=3.0, extra_time_ce=2.5,
                         extra_time=0.5, crossfade_time=0.04)
        motor._prepare_buffers()
        self._motor = motor
        self.sr = motor.config.samplerate
        self.bloque = motor.block_frame
        self.retraso_algoritmo = self.TIEMPO_BLOQUE + self.CONTEXTO_DERECHA
        self.calentar()

    def calentar(self):
        for _ in range(3):
            self._motor._process_block(np.zeros(self.bloque, np.float32))

    def reiniciar(self):
        self._motor._prepare_buffers()

    def cambiar_voz(self, voz):
        self._motor.set_config(reference_audio_path=voz)

    def convertir(self, bloque):
        return self._motor._process_block(bloque).astype(np.float32)


class Salida:
    """
    Un dispositivo de salida que reproduce lo convertido, con un bloque de margen para absorber los altibajos (MeanVC2
    entrega el audio a saltos de 120 ms). Si se queda sin sonido, calla y vuelve a juntar el margen antes de seguir:
    así un corte no se repite en cada bloque. Los silencios del principio, mientras llega el primer audio, no cuentan.
    """

    def __init__(self, sd, dispositivo, sr, bloque):
        self._cola = collections.deque()
        self._cerrojo = threading.Lock()
        self._pendiente = np.zeros(0, np.float32)
        self._margen = bloque
        self._esperando = True
        self._empezado = False
        self.vacios = 0
        self._stream = sd.OutputStream(samplerate=sr, blocksize=bloque, device=dispositivo, channels=1,
                                       dtype="float32", latency="low", callback=self._llamada)
        self.latencia = self._stream.latency

    def poner(self, audio):
        with self._cerrojo:
            self._cola.append(audio)

    def _llamada(self, outdata, frames, tiempo, estado):
        with self._cerrojo:
            while self._cola:
                self._pendiente = np.concatenate([self._pendiente, self._cola.popleft()])
            if self._esperando:
                if len(self._pendiente) < frames + self._margen:
                    outdata[:, 0] = 0
                    return
                self._esperando = False
                self._empezado = True
            n = min(frames, len(self._pendiente))
            outdata[:n, 0] = self._pendiente[:n]
            outdata[n:, 0] = 0
            self._pendiente = self._pendiente[n:]
            if n < frames:
                self.vacios += 1
                self._esperando = True

    def empezar(self):
        self._stream.start()

    def parar(self):
        self._stream.stop()
        self._stream.close()


class Sesion:
    """Micrófono (o archivo) → convertidor → salidas, en hilos propios."""

    def __init__(self, sd, conversor, entrada, archivo, salidas):
        self._sd = sd
        self._conv = conversor
        self._entradas = collections.deque()
        self._hay = threading.Event()
        self._vivo = True
        self.error = None
        self.fin_archivo = False
        self.bloques = 0
        self.lentos = 0
        self.tiempos = collections.deque(maxlen=50)
        self.nivel_entrada = -90.0
        self.nivel_salida = -90.0
        sr, n = conversor.sr, conversor.bloque
        self._salidas = [Salida(sd, d, sr, n) for d in salidas]
        self._entrada = None
        self._lector = None
        if archivo:
            import librosa

            x, _ = librosa.load(archivo, sr=sr, mono=True)
            self._archivo = np.concatenate([x, np.zeros(sr, np.float32)]).astype(np.float32)
            self._lector = threading.Thread(target=self._leer_archivo, daemon=True)
            self.latencia_entrada = 0.0
        else:
            self._entrada = sd.InputStream(samplerate=sr, blocksize=n, device=entrada, channels=1, dtype="float32",
                                           latency="low", callback=self._llamada_entrada)
            self.latencia_entrada = self._entrada.latency
        self._trabajador = threading.Thread(target=self._trabajar, daemon=True)

    def empezar(self):
        self._conv.reiniciar()
        self._trabajador.start()
        for s in self._salidas:
            s.empezar()
        if self._entrada is not None:
            self._entrada.start()
        else:
            self._lector.start()

    def _llamada_entrada(self, indata, frames, tiempo, estado):
        self._entradas.append(indata[:, 0].copy())
        self._hay.set()

    def _leer_archivo(self):
        n, sr = self._conv.bloque, self._conv.sr
        siguiente = time.perf_counter()
        for i in range(0, len(self._archivo) - n + 1, n):
            if not self._vivo:
                return
            self._entradas.append(self._archivo[i:i + n].copy())
            self._hay.set()
            siguiente += n / sr
            time.sleep(max(0.0, siguiente - time.perf_counter()))
        self.fin_archivo = True

    def _trabajar(self):
        n, sr = self._conv.bloque, self._conv.sr
        while self._vivo:
            if not self._hay.wait(0.1):
                continue
            self._hay.clear()
            while self._vivo and self._entradas:
                x = self._entradas.popleft()
                inicio = time.perf_counter()
                try:
                    y = self._conv.convertir(x)
                except Exception as e:  # noqa: BLE001 — se informa en el estado y se para
                    import traceback

                    traceback.print_exc()
                    self.error = f"{type(e).__name__}: {e}"
                    self._vivo = False
                    return
                gasto = time.perf_counter() - inicio
                self.tiempos.append(gasto)
                self.bloques += 1
                if gasto > n / sr:
                    self.lentos += 1
                self.nivel_entrada = _decibelios(x)
                if len(y):
                    self.nivel_salida = _decibelios(y)
                for s in self._salidas:
                    s.poner(y)

    def estado(self):
        tiempos = list(self.tiempos)
        n, sr = self._conv.bloque, self._conv.sr
        dispositivos = self.latencia_entrada + max([s.latencia for s in self._salidas] or [0.0])
        # Lo que se tarda en juntar un bloque + lo que el modelo mira por delante + el bloque de margen + el sonido de Windows.
        retraso = self._conv.retraso_algoritmo + n / sr + dispositivos
        return {
            "activo": self._vivo and self.error is None and not (self.fin_archivo and not self._entradas),
            "error": self.error,
            "fin_archivo": self.fin_archivo,
            "bloques": self.bloques,
            "lentos": self.lentos,
            "cortes": sum(s.vacios for s in self._salidas),
            "proceso_ms": round(1000 * float(np.mean(tiempos)), 1) if tiempos else 0.0,
            "proceso_max_ms": round(1000 * float(np.max(tiempos)), 1) if tiempos else 0.0,
            "bloque_ms": round(1000 * n / sr, 1),
            "retraso_ms": round(1000 * retraso),
            "nivel_entrada_db": round(float(self.nivel_entrada), 1),
            "nivel_salida_db": round(float(self.nivel_salida), 1),
        }

    def parar(self):
        self._vivo = False
        self._hay.set()
        if self._entrada is not None:
            try:
                self._entrada.stop()
                self._entrada.close()
            except Exception:  # noqa: BLE001
                pass
        for s in self._salidas:
            try:
                s.parar()
            except Exception:  # noqa: BLE001
                pass
        self._trabajador.join(timeout=2)


def _decibelios(x):
    if len(x) == 0:
        return -90.0
    return max(-90.0, 20 * np.log10(float(np.sqrt(np.mean(np.square(x)))) + 1e-9))


class MotorEnDirecto:
    nombre = "endirecto"

    def __init__(self):
        import torch

        self._torch = torch
        self._conversor = None
        self._voz = None
        self._sesion = None
        self.dispositivo = "cuda" if torch.cuda.is_available() else "cpu"

    def op_info(self, params, progreso):
        torch = self._torch
        info = {"torch": torch.__version__, "dispositivo": self.dispositivo,
                "modelo": None if self._conversor is None else {"repositorio": self._conversor.nombre, "revision": ""}}
        if self.dispositivo == "cuda":
            libre, total = torch.cuda.mem_get_info()
            info.update(gpu=torch.cuda.get_device_name(0), vram_total_gib=round(total / GIB, 2), vram_libre_gib=round(libre / GIB, 2))
        return info

    def op_dispositivos(self, params, progreso):
        """Micrófonos y salidas (MME, el sistema de sonido que funciona con cualquier frecuencia de muestreo)."""
        # Refrescar la lista reinicia el sistema de sonido: no se hace con el audio en marcha.
        sd = _sounddevice(refrescar=self._sesion is None)
        mme = next((i for i, h in enumerate(sd.query_hostapis()) if h["name"] == "MME"), None)
        defecto = sd.query_hostapis(mme)["default_input_device"] if mme is not None else -1
        defecto_salida = sd.query_hostapis(mme)["default_output_device"] if mme is not None else -1
        lista = []
        for d in sd.query_devices():
            if mme is not None and d["hostapi"] != mme:
                continue
            nombre = d["name"]
            # El «Asignador de sonido de Microsoft» solo apunta al dispositivo por defecto: no aporta nada.
            if "asignador de sonido" in nombre.lower() or "sound mapper" in nombre.lower():
                continue
            lista.append({
                "indice": d["index"], "nombre": nombre,
                "entrada": d["max_input_channels"] > 0, "salida": d["max_output_channels"] > 0,
                "por_defecto": d["index"] in (defecto, defecto_salida),
                "cable": "vb-audio" in nombre.lower() or any(c in nombre.lower() for c in NOMBRES_CABLE_SALIDA)
                         or "salida de calíope" in nombre.lower(),
            })
        return {"dispositivos": lista}

    def op_cargar(self, params, progreso):
        """Carga el convertidor («calidad» o «ligero») con la voz indicada. Los modelos, en la revisión del catálogo."""
        modo, voz, modelos = params["modo"], params["voz"], params.get("modelos") or {}
        if not os.path.isfile(voz):
            raise ErrorMotor("archivo_no_encontrado", t("No existe la grabación de referencia: {ruta}", ruta=voz))
        if self._conversor is not None and self._conversor.nombre == modo:
            if voz != self._voz:
                self._parar()
                self._conversor.cambiar_voz(voz)
                self._voz = voz
            return {"segundos": 0.0, "ya_cargado": True, **self._memoria()}
        self._liberar()
        progreso(None, t("Preparando la voz en directo"))
        inicio = time.perf_counter()
        self._conversor = (ConversorCalidad if modo == "calidad" else ConversorLigero)(modelos, voz)
        self._voz = voz
        return {"segundos": round(time.perf_counter() - inicio, 1), "ya_cargado": False, **self._memoria()}

    def op_iniciar(self, params, progreso):
        """Empieza a convertir: del micrófono «entrada» (o de «archivo») a las «salidas» (índices de dispositivo)."""
        if self._conversor is None:
            raise ErrorMotor("sin_modelo", t("No hay ningún modelo cargado."))
        self._parar()
        archivo = params.get("archivo")
        if archivo and not os.path.isfile(archivo):
            raise ErrorMotor("archivo_no_encontrado", t("No existe el audio: {ruta}", ruta=archivo))
        salidas = [int(s) for s in params.get("salidas") or []]
        if not salidas:
            raise ErrorMotor("sin_salida", t("Elige al menos una salida: tus auriculares o la «Salida de Calíope»."))
        sd = _sounddevice()
        entrada = params.get("entrada")
        try:
            self._sesion = Sesion(sd, self._conversor, None if entrada is None else int(entrada), archivo, salidas)
            self._sesion.empezar()
        except sd.PortAudioError as e:
            self._sesion = None
            raise ErrorMotor("sin_audio", t("No se ha podido abrir el sonido: {error}. Comprueba que el micrófono y la salida "
                                            "están conectados y no los tiene en exclusiva otro programa.", error=e)) from e
        return self._sesion.estado()

    def op_estado(self, params, progreso):
        if self._sesion is None:
            return {"activo": False}
        return {**self._sesion.estado(), **self._memoria()}

    def op_parar(self, params, progreso):
        estado = self._sesion.estado() if self._sesion is not None else {"activo": False}
        self._parar()
        estado["activo"] = False
        return estado

    def op_descargar(self, params, progreso):
        self._liberar()
        return self._memoria()

    def _parar(self):
        if self._sesion is not None:
            self._sesion.parar()
            self._sesion = None

    def _liberar(self):
        import gc

        self._parar()
        self._conversor = None
        self._voz = None
        gc.collect()
        if self.dispositivo == "cuda":
            self._torch.cuda.empty_cache()

    def _memoria(self):
        if self.dispositivo != "cuda":
            return {}
        return {"vram_usada_gib": round(self._torch.cuda.memory_allocated() / GIB, 2)}


def _sounddevice(refrescar=False):
    import sounddevice as sd

    if refrescar:
        # PortAudio guarda la lista de dispositivos al arrancar: sin esto no aparece un micrófono recién conectado
        # ni el cable virtual recién instalado. Solo si no hay audio en marcha.
        try:
            sd._terminate()
            sd._initialize()
        except Exception:  # noqa: BLE001
            pass
    return sd
