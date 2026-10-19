using System.Buffers.Binary;
using System.Text;

namespace Studio;

public sealed record Pcm(int Rate, byte[] Data);
public static class Wave
{
    public const int MaxBytes = 64 * 1024 * 1024;
    // Normalize supported integer PCM/IEEE float WAV to mono PCM16 at the source rate.
    public static Pcm Decode(byte[] bytes)
    {
        if (bytes.Length is < 44 or > MaxBytes || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE" || U32(bytes, 4) != bytes.Length - 8)
            throw new InputError("MiMo 返回的 WAV 文件无效或超出大小上限。");
        int format = 0, channels = 0, rate = 0, bits = 0, align = 0; byte[]? data = null;
        for (int p = 12; p < bytes.Length;)
        {
            if (bytes.Length - p < 8) throw new InputError("WAV chunk 不完整。");
            uint raw = U32(bytes, p + 4);
            if (raw > int.MaxValue || raw > bytes.Length - p - 8) throw new InputError("WAV chunk 长度无效。");
            int size = (int)raw, start = p + 8;
            var name = Encoding.ASCII.GetString(bytes, p, 4);
            if (name == "fmt ")
            {
                if (size < 16 || format != 0) throw new InputError("WAV 格式头无效。");
                format = U16(bytes, start); channels = U16(bytes, start + 2); rate = (int)U32(bytes, start + 4);
                align = U16(bytes, start + 12); bits = U16(bytes, start + 14);
                if (channels is < 1 or > 2 || rate is < 8000 or > 96000 ||
                    !((format == 1 && bits is 8 or 16 or 24 or 32) || (format == 3 && bits == 32)) ||
                    align != channels * bits / 8 || U32(bytes, start + 8) != rate * align)
                    throw new InputError("不支持的 WAV 采样格式。");
            }
            if (name == "data")
            {
                if (data != null) throw new InputError("WAV 包含重复 data chunk。");
                data = bytes.AsSpan(start, size).ToArray();
            }
            p = checked(start + size + (size & 1));
            if (p > bytes.Length) throw new InputError("WAV 缺少 padding。");
        }
        if (format == 0 || data == null || data.Length == 0 || data.Length % align != 0) throw new InputError("WAV 没有完整采样。");
        int frames = data.Length / align;
        var output = new byte[checked(frames * 2)];
        for (int f = 0; f < frames; f++)
        {
            double sample = 0;
            for (int c = 0; c < channels; c++)
            {
                int p = f * align + c * bits / 8;
                double value = format == 3 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p))) : bits switch
                {
                    8 => (data[p] - 128) / 128.0,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p)) / 32768.0,
                    24 => ((data[p] | data[p + 1] << 8 | data[p + 2] << 16) << 8 >> 8) / 8388608.0,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)) / 2147483648.0
                };
                if (!double.IsFinite(value)) throw new InputError("WAV 包含无效浮点采样。");
                sample += value / channels;
            }
            short pcm = (short)Math.Clamp(Math.Round(sample * 32768), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(f * 2), pcm);
        }
        return new(rate, output);
    }
    public static byte[] Merge(IReadOnlyList<Pcm> segments)
    {
        if (segments.Count == 0) throw new InputError("没有音频。");
        int rate = segments[0].Rate;
        if (segments.Any(s => s.Rate != rate)) throw new InputError("分段采样率不同，无法安全合并；请重新检查服务响应。");
        long length = segments.Sum(s => (long)s.Data.Length);
        if (length > MaxBytes - 44) throw new InputError("合成结果超出 64 MiB 上限。");
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write((uint)(36 + length)); w.Write("WAVEfmt "u8); w.Write(16);
        w.Write((ushort)1); w.Write((ushort)1); w.Write(rate); w.Write(rate * 2); w.Write((ushort)2); w.Write((ushort)16);
        w.Write("data"u8); w.Write((uint)length);
        foreach (var s in segments) w.Write(s.Data);
        return stream.ToArray();
    }
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
}
