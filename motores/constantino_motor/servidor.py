"""Bucle del protocolo: una petición JSON por línea en stdin, eventos JSON por línea en stdout.

Petición:  {"id": "…", "op": "sintetizar", "params": {…}}
Eventos:   {"id": "…", "tipo": "progreso", "valor": 0.5, "mensaje": "…"}
           {"id": "…", "tipo": "resultado", "datos": {…}}
           {"id": "…", "tipo": "error", "codigo": "sin_memoria", "mensaje": "…", "detalle": "traceback"}
Al arrancar se envía {"id": null, "tipo": "listo", "datos": {…}}.

stdout queda reservado al protocolo: todo lo que impriman las librerías (avisos de flash-attn,
SoX…) se desvía a stderr, que la aplicación guarda como diagnóstico.
"""
import json
import os
import sys
import threading
import traceback

from . import PROTOCOLO, VERSION
from .textos import t


class ErrorMotor(Exception):
    """Error previsto, con un código que la aplicación sabe explicar."""

    def __init__(self, codigo, mensaje):
        super().__init__(mensaje)
        self.codigo = codigo


class Canal:
    def __init__(self, salida):
        self._salida = salida
        self._cerrojo = threading.Lock()

    def enviar(self, **mensaje):
        linea = json.dumps(mensaje, ensure_ascii=False)
        with self._cerrojo:
            self._salida.write(linea + "\n")
            self._salida.flush()


def _reservar_stdout():
    """Duplica el stdout real para el protocolo y redirige el descriptor 1 a stderr."""
    protocolo = os.fdopen(os.dup(1), "w", encoding="utf-8", newline="\n")
    sys.stdout.flush()
    os.dup2(2, 1)
    sys.stdout = sys.stderr
    return protocolo


def _crear_motor():
    if os.environ.get("CONSTANTINO_MOTOR_FALSO") == "1":
        from .falso import MotorFalso
        return MotorFalso()
    # La aplicación arranca un proceso por motor, cada uno con el Python de su entorno.
    if os.environ.get("CONSTANTINO_MOTOR") == "chatterbox":
        from .chatterbox import MotorChatterbox
        return MotorChatterbox()
    if os.environ.get("CONSTANTINO_MOTOR") == "voxcpm":
        from .voxcpm import MotorVoxCpm
        return MotorVoxCpm()
    from .qwen import MotorQwen
    return MotorQwen()


def clasificar(error):
    """Código de error para excepciones no previstas."""
    nombre = type(error).__name__
    texto = str(error)
    if nombre == "OutOfMemoryError" or "out of memory" in texto.lower():
        return "sin_memoria"
    if nombre in ("LocalEntryNotFoundError", "RepositoryNotFoundError", "RevisionNotFoundError"):
        return "modelo_no_encontrado"
    if isinstance(error, FileNotFoundError):
        return "archivo_no_encontrado"
    return "desconocido"


def main():
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
    canal = Canal(_reservar_stdout())
    motor = _crear_motor()
    canal.enviar(id=None, tipo="listo", datos={"protocolo": PROTOCOLO, "version": VERSION, "pid": os.getpid(),
                                                 "motor": motor.nombre})

    for linea in sys.stdin:
        linea = linea.strip()
        if not linea:
            continue
        id_peticion = None
        try:
            peticion = json.loads(linea)
            id_peticion = peticion.get("id")
            op = peticion.get("op")
            params = peticion.get("params") or {}

            def progreso(valor, mensaje, _id=id_peticion):
                canal.enviar(id=_id, tipo="progreso", valor=valor, mensaje=mensaje)

            if op == "salir":
                canal.enviar(id=id_peticion, tipo="resultado", datos={})
                break
            accion = getattr(motor, "op_" + str(op), None)
            if accion is None:
                raise ErrorMotor("operacion_desconocida", t("Operación desconocida: {op}", op=op))
            datos = accion(params, progreso)
            canal.enviar(id=id_peticion, tipo="resultado", datos=datos or {})
        except ErrorMotor as e:
            canal.enviar(id=id_peticion, tipo="error", codigo=e.codigo, mensaje=str(e), detalle=None)
        except Exception as e:  # noqa: BLE001 — cualquier fallo se devuelve a la aplicación, el motor sigue vivo
            traceback.print_exc()
            canal.enviar(id=id_peticion, tipo="error", codigo=clasificar(e), mensaje=f"{type(e).__name__}: {e}",
                         detalle=traceback.format_exc())
