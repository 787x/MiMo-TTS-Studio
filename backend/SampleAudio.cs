using System.Buffers.Binary;
using NLayer;

namespace Studio;

public sealed record SampleInfo(string Format, string Mime, double DurationSeconds, int SampleRate, int Channels);
public static class SampleAudio
{
    // Decimal MB; leave room for the Data URL prefix and request envelope.
    public const int MaxBytes = 7_400_000;
    public const int MaxTransportBytes = 10_000_000;
    public const double MinSeconds = 0.2, MaxSeconds = 120;
    public static string Mime(string format) => format switch { "wav" => "audio/wav", "mp3" => "audio/mpeg", _ => throw new InputError("仅支持实际格式为 WAV 或 MP3 的音频。") };
    public static bool Eligible(long bytes, double seconds, string format) => bytes is > 0 and <= MaxBytes &&
        seconds is >= MinSeconds and <= MaxSeconds && (bytes + 2) / 3 * 4 + ("data:" + Mime(format) + ";base64,").Length <= MaxTransportBytes;
    public static SampleInfo Inspect(byte[] bytes, string format, CancellationToken ct = default)
    {
        _ = Mime(format);
        if (bytes.Length is 0 or > MaxBytes) throw new InputError("参考音频不能为空且最多 7.4 MB（编码后含前缀小于 10 MB）。");
        ct.ThrowIfCancellationRequested();
        SampleInfo info;
        if (format == "wav")
        {
            var pcm = Wave.Decode(bytes);
            info = new("wav", "audio/wav", pcm.Data.Length / (pcm.Rate * 2.0), pcm.Rate, ChannelsFromWave(bytes));
        }
        else
        {
            try { info = InspectMp3(bytes, ct); }
            catch (Exception e) when (e is not (InputError or OperationCanceledException or OutOfMemoryException))
            { throw new InputError("MP3 帧或压缩音频损坏，无法完整解码。"); }
        }
        if (!Eligible(bytes.Length, info.DurationSeconds, format))
            throw new InputError("本应用参考音频范围为 0.2–120 秒、最多 7.4 MB。这是资源保护范围，不是 MiMo 官方时长限制。");
        return info;
    }
    private static int ChannelsFromWave(byte[] b)
    {
        for (int p = 12; p + 8 <= b.Length;)
        {
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4)));
            if (b.AsSpan(p, 4).SequenceEqual("fmt "u8)) return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 10));
            p = checked(p + 8 + size + (size & 1));
        }
        throw new InputError("WAV 缺少采样格式。");
    }
    private static SampleInfo InspectMp3(byte[] b, CancellationToken ct)
    {
        int p = 0, end = b.Length;
        if (b.AsSpan().StartsWith("ID3"u8))
        {
            if (b.Length < 10 || b[3] is < 2 or > 4 || b[4] == 255 || b.AsSpan(6, 4).ContainsAnyInRange((byte)128, (byte)255))
                throw new InputError("MP3 ID3 头无效。");
            int size = b[6] << 21 | b[7] << 14 | b[8] << 7 | b[9];
            p = checked(10 + size + (b[3] == 4 && (b[5] & 16) != 0 ? 10 : 0));
            if (p >= end) throw new InputError("MP3 没有完整音频帧。");
        }
        if (end - p >= 128 && b.AsSpan(end - 128, 3).SequenceEqual("TAG"u8)) end -= 128;
        var decoder = new MpegFrameDecoder();
        var output = new float[2304];
        int rate = 0, channels = 0, frames = 0; long samples = 0;
        while (p < end)
        {
            ct.ThrowIfCancellationRequested();
            var frame = new Mp3Frame(b, p, end);
            if (rate == 0) { rate = frame.SampleRate; channels = frame.Channels; }
            if (rate != frame.SampleRate || channels != frame.Channels) throw new InputError("MP3 中途改变采样率或声道，不支持该编码。");
            if ((samples + frame.SampleCount) / (double)rate > MaxSeconds) throw new InputError("参考音频超过本应用 120 秒上限。");
            // Unlike MpegFile's conceal-and-resync path, DecodeFrame propagates
            // damaged frame errors. Never skip arbitrary bytes or bad frames.
            int read = decoder.DecodeFrame(frame, output, 0);
            if (read != frame.SampleCount * channels || output.AsSpan(0, read).ContainsAnyExceptInRange(float.MinValue, float.MaxValue))
                throw new InputError("MP3 不能完整解码或含无效采样。");
            samples += frame.SampleCount; frames++; p += frame.FrameLength;
        }
        if (frames < 2) throw new InputError("MP3 没有足够的完整音频帧。");
        // Duration includes MPEG encoder delay/padding; no guessed bitrate duration.
        return new("mp3", "audio/mpeg", samples / (double)rate, rate, channels);
    }
    private sealed class Mp3Frame : IMpegFrame
    {
        private readonly byte[] bytes;
        private readonly int start;
        private int bit;
        private readonly int versionBits;
        public int SampleRate { get; }
        public int SampleRateIndex { get; }
        public int FrameLength { get; }
        public int BitRate { get; }
        public MpegVersion Version => versionBits == 3 ? MpegVersion.Version1 : versionBits == 2 ? MpegVersion.Version2 : MpegVersion.Version25;
        public MpegLayer Layer => MpegLayer.LayerIII;
        public MpegChannelMode ChannelMode { get; }
        public int ChannelModeExtension { get; }
        public int SampleCount => versionBits == 3 ? 1152 : 576;
        public int BitRateIndex { get; }
        public bool IsCopyrighted { get; }
        public bool HasCrc { get; }
        public bool IsCorrupted => false;
        public int Channels => ChannelMode == MpegChannelMode.Mono ? 1 : 2;
        public Mp3Frame(byte[] bytes, int start, int end)
        {
            this.bytes = bytes; this.start = start;
            if (end - start < 4) throw new InputError("MP3 帧不完整。");
            uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start));
            versionBits = (int)(h >> 19 & 3); BitRateIndex = (int)(h >> 12 & 15); SampleRateIndex = (int)(h >> 10 & 3);
            if (h >> 21 != 0x7ff || versionBits == 1 || (h >> 17 & 3) != 1 || BitRateIndex is 0 or 15 || SampleRateIndex == 3 || (h & 3) == 2)
                throw new InputError("仅支持完整、非 free-format 的 MPEG Layer III 音频。");
            int[] rates = [44100, 48000, 32000];
            SampleRate = rates[SampleRateIndex] / (versionBits == 3 ? 1 : versionBits == 2 ? 2 : 4);
            int[] high = [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320];
            int[] low = [0,8,16,24,32,40,48,56,64,80,96,112,128,144,160];
            BitRate = (versionBits == 3 ? high : low)[BitRateIndex] * 1000;
            ChannelMode = (MpegChannelMode)(h >> 6 & 3); ChannelModeExtension = (int)(h >> 4 & 3);
            HasCrc = (h >> 16 & 1) == 0; IsCopyrighted = (h & 8) != 0;
            FrameLength = SampleCount / 8 * BitRate / SampleRate + (int)(h >> 9 & 1);
            int sideBytes = versionBits == 3 ? Channels == 1 ? 17 : 32 : Channels == 1 ? 9 : 17;
            if (FrameLength > end - start || FrameLength < 4 + (HasCrc ? 2 : 0) + sideBytes) throw new InputError("MP3 音频帧截断。");
            if (HasCrc)
            {
                ushort crc = 0xffff;
                void Add(byte value) { for (int n = 7; n >= 0; n--) { bool carry = ((crc >> 15) ^ (value >> n)) % 2 != 0; crc <<= 1; if (carry) crc ^= 0x8005; } }
                Add(bytes[start + 2]); Add(bytes[start + 3]);
                for (int n = start + 6; n < start + 6 + sideBytes; n++) Add(bytes[n]);
                if (crc != BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(start + 4))) throw new InputError("MP3 CRC 校验失败。");
            }
            Reset();
        }
        public void Reset() => bit = (4 + (HasCrc ? 2 : 0)) * 8;
        public int ReadBits(int count)
        {
            if (count is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
            if (bit + count > FrameLength * 8) throw new EndOfStreamException();
            int value = 0;
            for (int n = 0; n < count; n++, bit++) value = value << 1 | (bytes[start + bit / 8] >> (7 - bit % 8) & 1);
            return value;
        }
    }
}
