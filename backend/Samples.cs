using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Studio;

public sealed record VoiceSample(string Id, string Owner, string Name, string Source, string Format, string Mime,
    long Bytes, double DurationSeconds, int SampleRate, int Channels, DateTimeOffset Created, string? SourceAssetId, string Sha256);
public sealed record SampleSnapshot(VoiceSample Metadata, string DataUrl);
public sealed partial class Storage
{
    public static string SampleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || StringInfo.ParseCombiningCharacters(name).Length > 80 || name.Any(char.IsControl))
            throw new InputError("样本名需为 1–80 个文本元素，不含控制字符。");
        return name.Trim();
    }
    private VoiceSample? ReadSample(string path)
    {
        try { return JsonSerializer.Deserialize<VoiceSample>(File.ReadAllText(path), Json); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
    public VoiceSample? GetSample(string id, string owner)
    {
        lock (gate)
        {
            if (!ValidId(id)) return null;
            var sample = ReadSample(Path.Combine(Samples, id, "sample.json"));
            return sample?.Owner == owner && sample.Id == id && sample.Format is "wav" or "mp3" &&
                File.Exists(Path.Combine(Samples, id, "reference." + sample.Format)) ? sample : null;
        }
    }
    public VoiceSample[] ListSamples(string owner)
    {
        lock (gate) return Directory.GetDirectories(Samples).Select(Path.GetFileName).OfType<string>().Where(ValidId)
            .Select(id => GetSample(id, owner)).OfType<VoiceSample>().OrderByDescending(s => s.Created).ToArray();
    }
    public VoiceSample CreateSample(string owner, string name, string format, byte[] bytes, string source = "upload", string? sourceAssetId = null, CancellationToken ct = default)
    {
        name = SampleName(name);
        var info = SampleAudio.Inspect(bytes, format, ct);
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!HasSpace(bytes.LongLength)) throw new InputError("音频与样本共用的 1 GiB / 1000 条配额或磁盘空间不足。");
            string id = Guid.NewGuid().ToString("N"), stage = Path.Combine(Samples, ".stage-" + id);
            var sample = new VoiceSample(id, owner, name, source, info.Format, info.Mime, bytes.LongLength, info.DurationSeconds,
                info.SampleRate, info.Channels, DateTimeOffset.UtcNow, sourceAssetId, Convert.ToHexString(SHA256.HashData(bytes)));
            PrivateDirectory(stage);
            try
            {
                AtomicWrite(Path.Combine(stage, "reference." + info.Format), bytes);
                AtomicWrite(Path.Combine(stage, "sample.json"), JsonSerializer.SerializeToUtf8Bytes(sample, Json));
                ct.ThrowIfCancellationRequested();
                Directory.Move(stage, Path.Combine(Samples, id)); return sample;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
    }
    public VoiceSample? SampleFromAsset(string id, string owner, string name)
    {
        byte[] bytes;
        lock (gate)
        {
            var asset = Get(id, owner); if (asset == null) return null;
            if (asset.SynthesisMode != "design" || !asset.SampleEligible) throw new InputError("该结果不是可用的 VoiceDesign 短音频样本，请检查时长与大小。");
            bytes = File.ReadAllBytes(Path.Combine(Data, id, "audio.wav"));
        }
        // Independent copy: deletion/expiry of the source asset cannot affect it.
        return CreateSample(owner, name, "wav", bytes, "design", id);
    }
    public VoiceSample? RenameSample(string id, string owner, string name)
    {
        name = SampleName(name);
        lock (gate)
        {
            var sample = GetSample(id, owner); if (sample == null) return null;
            sample = sample with { Name = name };
            AtomicWrite(Path.Combine(Samples, id, "sample.json"), JsonSerializer.SerializeToUtf8Bytes(sample, Json)); return sample;
        }
    }
    public bool DeleteSample(string id, string owner)
    {
        lock (gate)
        {
            if (GetSample(id, owner) == null) return false;
            Directory.Delete(Path.Combine(Samples, id), true); return true;
        }
    }
    public (FileStream File, string Mime)? OpenSample(string id, string owner)
    {
        lock (gate)
        {
            var sample = GetSample(id, owner);
            return sample == null ? null : (new FileStream(Path.Combine(Samples, id, "reference." + sample.Format), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete), sample.Mime);
        }
    }
    public SampleSnapshot SnapshotSample(string id, string owner)
    {
        lock (gate)
        {
            var sample = GetSample(id, owner) ?? throw new InputError("参考样本不存在或无权使用。");
            string path = Path.Combine(Samples, id, "reference." + sample.Format);
            if (new FileInfo(path).Length is 0 or > SampleAudio.MaxBytes) throw new InputError("参考样本大小无效。");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != sample.Bytes || Convert.ToHexString(SHA256.HashData(bytes)) != sample.Sha256)
                throw new InputError("参考样本已损坏，请重新上传。");
            string url = "data:" + SampleAudio.Mime(sample.Format) + ";base64," + Convert.ToBase64String(bytes);
            if (url.Length > SampleAudio.MaxTransportBytes) throw new InputError("参考样本编码后超过 10 MB 上限。");
            return new(sample, url);
        }
    }
}
