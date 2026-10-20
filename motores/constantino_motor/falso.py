"""Motor falso para pruebas: mismo protocolo que MotorQwen, sin torch ni GPU.

Se activa con CONSTANTINO_MOTOR_FALSO=1. «Sintetiza» un tono cuya duración depende del texto.
"""
import math
import os
import struct
import wave

from .servidor import ErrorMotor


class MotorFalso:
    nombre = "falso"

    def __init__(self):
        self._cargado = None

    def op_info(self, params, progreso):
        return {"torch": None, "dispositivo": "cpu", "modelo": self._cargado}

    def op_cargar(self, params, progreso):
        progreso(None, "Cargando el modelo de voz")
        if params.get("repositorio") == "no/existe":
            raise ErrorMotor("modelo_no_encontrado", "El modelo no está instalado.")
        self._cargado = {"repositorio": params["repositorio"], "revision": params["revision"]}
        return {"segundos": 0.0, "ya_cargado": False}

    def op_descargar(self, params, progreso):
        self._cargado = None
        return {}

    def op_sintetizar(self, params, progreso):
        if self._cargado is None:
            raise ErrorMotor("sin_modelo", "No hay ningún modelo cargado.")
        if not os.path.isfile(params["referencia_wav"]):
            raise ErrorMotor("archivo_no_encontrado", "No existe la grabación de referencia.")
        return self._tono(params, progreso)

    def op_disenar(self, params, progreso):
        if self._cargado is None:
            raise ErrorMotor("sin_modelo", "No hay ningún modelo cargado.")
        if not (params.get("instruccion") or "").strip():
            raise ErrorMotor("sin_descripcion", "Describe la voz que quieres crear.")
        return self._tono(params, progreso)

    def op_transcribir(self, params, progreso):
        if self._cargado is None:
            raise ErrorMotor("sin_modelo", "No hay ningún modelo cargado.")
        if not os.path.isfile(params["audio"]):
            raise ErrorMotor("archivo_no_encontrado", "No existe el audio.")
        progreso(None, "Transcribiendo")
        return {"texto": f"Texto transcrito en {params.get('idioma', 'Spanish')}.", "segundos": 0.0}

    def _tono(self, params, progreso):
        texto = params.get("texto", "")
        if texto == "falla":
            raise RuntimeError("fallo provocado")
        progreso(None, "Generando la voz")
        sr = 24000
        duracion = max(0.2, len(texto) / 15)
        n = int(sr * duracion)
        os.makedirs(os.path.dirname(params["salida"]), exist_ok=True)
        with wave.open(params["salida"], "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(sr)
            w.writeframes(b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * 220 * i / sr))) for i in range(n)))
        print("aviso de librería que no debe romper el protocolo")
        return {"salida": params["salida"], "duracion": round(duracion, 2), "segundos": 0.0, "frecuencia": sr}
