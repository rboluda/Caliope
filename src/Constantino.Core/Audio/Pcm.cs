using System.Text;

namespace Constantino.Core.Audio;

/// <summary>Audio mono en memoria: muestras entre -1 y 1 a una frecuencia dada.</summary>
public sealed class Pcm(float[] muestras, int frecuencia)
{
    public float[] Muestras { get; } = muestras;
    public int Frecuencia { get; } = frecuencia;
    public double Segundos => (double)Muestras.Length / Frecuencia;

    public static Pcm Silencio(double segundos, int frecuencia) => new(new float[(int)Math.Round(segundos * frecuencia)], frecuencia);

    /// <summary>Lee un WAV PCM de 16/24/32 bits o de coma flotante de 32 bits. Si es estéreo, mezcla los canales.</summary>
    public static Pcm LeerWav(string ruta)
    {
        using var r = new BinaryReader(File.OpenRead(ruta));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException($"{ruta} no es un WAV.");
        r.ReadUInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException($"{ruta} no es un WAV.");

        int formato = 0, canales = 0, frecuencia = 0, bits = 0;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            var tamano = r.ReadUInt32();
            if (id == "fmt ")
            {
                formato = r.ReadUInt16();
                canales = r.ReadUInt16();
                frecuencia = (int)r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt16();
                bits = r.ReadUInt16();
                if (formato == 0xFFFE && tamano >= 40)
                {
                    // WAVE_FORMAT_EXTENSIBLE: el formato real va en el subformato.
                    r.ReadBytes(8);
                    formato = r.ReadUInt16();
                    r.BaseStream.Seek(tamano - 26, SeekOrigin.Current);
                }
                else
                {
                    r.BaseStream.Seek(tamano - 16, SeekOrigin.Current);
                }
            }
            else if (id == "data")
            {
                var bytes = tamano == uint.MaxValue || r.BaseStream.Position + tamano > r.BaseStream.Length
                    ? (int)(r.BaseStream.Length - r.BaseStream.Position)
                    : (int)tamano;
                return new Pcm(Decodificar(r.ReadBytes(bytes), formato, bits, canales), frecuencia);
            }
            else
            {
                r.BaseStream.Seek(tamano + (tamano & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException($"{ruta} no tiene audio.");
    }

    private static float[] Decodificar(byte[] datos, int formato, int bits, int canales)
    {
        if (canales < 1)
            throw new InvalidDataException("WAV sin canales.");
        var bytesPorMuestra = bits / 8;
        var tramas = datos.Length / (bytesPorMuestra * canales);
        var salida = new float[tramas];
        for (var t = 0; t < tramas; t++)
        {
            float suma = 0;
            for (var c = 0; c < canales; c++)
            {
                var o = (t * canales + c) * bytesPorMuestra;
                suma += (formato, bits) switch
                {
                    (1, 16) => BitConverter.ToInt16(datos, o) / 32768f,
                    (1, 24) => ((datos[o] | (datos[o + 1] << 8) | (datos[o + 2] << 16)) << 8 >> 8) / 8388608f,
                    (1, 32) => BitConverter.ToInt32(datos, o) / 2147483648f,
                    (3, 32) => BitConverter.ToSingle(datos, o),
                    _ => throw new InvalidDataException($"Formato de WAV no admitido ({formato}, {bits} bits)."),
                };
            }
            salida[t] = suma / canales;
        }
        return salida;
    }

    /// <summary>Escribe en coma flotante de 32 bits (sin pérdida para los pasos intermedios).</summary>
    public void EscribirWav(string ruta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ruta))!);
        using var w = new BinaryWriter(File.Create(ruta));
        var datos = Muestras.Length * 4;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + datos);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)3);
        w.Write((short)1);
        w.Write(Frecuencia);
        w.Write(Frecuencia * 4);
        w.Write((short)4);
        w.Write((short)32);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(datos);
        foreach (var m in Muestras)
            w.Write(m);
    }

    /// <summary>
    /// Quita el silencio del principio y del final (por debajo de <paramref name="umbralDb"/> en ventanas de 10 ms),
    /// deja <paramref name="margen"/> segundos de cortesía y suaviza los cortes con fundidos de 5 ms.
    /// </summary>
    public Pcm Recortar(double umbralDb = -50, double margen = 0.05)
    {
        var ventana = Math.Max(1, Frecuencia / 100);
        var n = Muestras.Length / ventana;
        int? primera = null, ultima = null;
        for (var i = 0; i < n; i++)
        {
            double suma = 0;
            for (var j = i * ventana; j < (i + 1) * ventana; j++)
                suma += Muestras[j] * Muestras[j];
            if (20 * Math.Log10(Math.Sqrt(suma / ventana) + 1e-9) > umbralDb)
            {
                primera ??= i;
                ultima = i;
            }
        }
        if (primera is null)
            return new Pcm([], Frecuencia);
        var inicio = Math.Max(0, primera.Value * ventana - (int)(margen * Frecuencia));
        var fin = Math.Min(Muestras.Length, (ultima!.Value + 1) * ventana + (int)(margen * Frecuencia));
        var recorte = Muestras[inicio..fin];
        var fundido = Math.Min(Frecuencia / 200, recorte.Length / 2);
        for (var i = 0; i < fundido; i++)
        {
            var g = (float)i / fundido;
            recorte[i] *= g;
            recorte[^(i + 1)] *= g;
        }
        return new Pcm(recorte, Frecuencia);
    }

    public Pcm ConGanancia(double db)
    {
        var factor = (float)Math.Pow(10, db / 20);
        return new Pcm(Muestras.Select(m => m * factor).ToArray(), Frecuencia);
    }

    public static Pcm Unir(IReadOnlyList<Pcm> partes, int frecuencia)
    {
        if (partes.Any(p => p.Frecuencia != frecuencia))
            throw new InvalidOperationException("Todas las partes deben tener la misma frecuencia.");
        var total = new float[partes.Sum(p => p.Muestras.Length)];
        var pos = 0;
        foreach (var p in partes)
        {
            p.Muestras.CopyTo(total, pos);
            pos += p.Muestras.Length;
        }
        return new Pcm(total, frecuencia);
    }
}
