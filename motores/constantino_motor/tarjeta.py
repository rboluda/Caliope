"""Lo que puede hacer la tarjeta gráfica con el PyTorch instalado en el entorno del motor."""
from .servidor import ErrorMotor
from .textos import t

# Las tarjetas anteriores a Turing (2018: GTX 10xx, Volta, Maxwell…) no tienen bfloat16 útil. Coincide con
# Gpu.CapacidadModerna en la aplicación, que con ellas cuenta el doble de memoria.
CAPACIDAD_MEDIA_PRECISION = (7, 5)


def comprobar(torch):
    """
    Avisa con un error claro si este PyTorch no trae código para la tarjeta (p. ej. el de CUDA 12.8 en una GTX 1070),
    en vez del «no kernel image is available for execution on the device» de CUDA al generar.
    """
    if not torch.cuda.is_available():
        return
    mayor, menor = torch.cuda.get_device_capability(0)
    for arquitectura in torch.cuda.get_arch_list():  # «sm_61», «sm_86», «compute_90»…
        tipo, _, numero = arquitectura.partition("_")
        if not numero.isdigit():
            continue
        a, b = int(numero[:-1]), int(numero[-1])
        # El código para sm_XY funciona en las tarjetas X.Z con Z >= Y; el PTX (compute_XY), en todas las posteriores.
        if (tipo == "sm" and a == mayor and b <= menor) or (tipo == "compute" and (a, b) <= (mayor, menor)):
            return
    raise ErrorMotor("gpu_no_compatible", t(
        "Este motor se instaló con una versión que no funciona con tu {gpu}. Abre «Componentes» y pulsa «Actualizar» "
        "en el motor: Calíope instalará la versión adecuada para tu tarjeta.", gpu=torch.cuda.get_device_name(0)))


def media_precision(torch):
    """Si conviene bfloat16/float16 en la GPU. En las antiguas son muy lentas o fallan: allí se usa float32."""
    return torch.cuda.is_available() and tuple(torch.cuda.get_device_capability(0)) >= CAPACIDAD_MEDIA_PRECISION
