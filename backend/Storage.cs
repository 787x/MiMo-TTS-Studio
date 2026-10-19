using System.Text.Json;

namespace Studio;

public sealed record Asset(string Id, string Owner, string Title, string Voice, DateTimeOffset Created, bool Saved, long Bytes);
public sealed class Storage
{
    public string Config { get; }
    public string Data { get; }
    public string Temp { get; }
    private readonly object gate = new();
    private string KeyPath => Path.Combine(Config, "mimo-key");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const long Quota = 1024L * 1024 * 1024;
    public Storage(string config, string data, string temp)
    {
        Config = config; Data = data; Temp = temp;
        foreach (var path in new[] { Config, Data, Temp }) PrivateDirectory(path);
        // fnOS reapplies package ACLs during reinstall (observed mode 0700 on
        // retained files). Reassert the credential's exact mode before reading it.
        if (OperatingSystem.IsLinux() && File.Exists(KeyPath))
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        foreach (var dir in Directory.GetDirectories(Data, ".stage-*")) Directory.Delete(dir, true);
        foreach (var file in Directory.GetFiles(Temp, "*.partial")) File.Delete(file);
        foreach (var asset in ListAll().Where(a => !a.Saved && a.Created < DateTimeOffset.UtcNow.AddHours(-24)))
            Directory.Delete(Path.Combine(Data, asset.Id), true);
    }
    public static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static bool ValidId(string id) => Guid.TryParseExact(id, "N", out _);
    public string? ReadKey() { lock (gate) return File.Exists(KeyPath) ? File.ReadAllText(KeyPath) : null; }
    public void SetKey(string? key)
    {
        lock (gate)
        {
            if (string.IsNullOrEmpty(key)) { File.Delete(KeyPath); return; }
            if (key.Length is < 8 or > 512 || key.Any(c => c < 33 || c > 126)) throw new InputError("API Key 必须为 8–512 个可打印 ASCII 字符，不含空白。");
            AtomicWrite(KeyPath, System.Text.Encoding.UTF8.GetBytes(key));
        }
    }
    public bool HasSpace()
    {
        lock (gate)
        {
            long used = Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            var drive = new DriveInfo(Path.GetFullPath(Data));
            return ListAll().Take(1000).Count() < 1000 && used < Quota - Wave.MaxBytes && drive.AvailableFreeSpace > 256L * 1024 * 1024;
        }
    }
    public Asset Commit(string owner, ScriptRequest input, byte[] bytes)
    {
        lock (gate)
        {
            if (!HasSpace()) throw new InputError("私有音频库空间不足（1 GiB 配额，磁盘需保留 256 MiB）。请删除旧音频。");
            string id = Guid.NewGuid().ToString("N"), stage = Path.Combine(Data, ".stage-" + id);
            PrivateDirectory(stage);
            var title = new string(input.Text.Take(60).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            var asset = new Asset(id, owner, title, input.Voice, DateTimeOffset.UtcNow, false, bytes.Length);
            try
            {
                AtomicWrite(Path.Combine(stage, "audio.wav"), bytes);
                AtomicWrite(Path.Combine(stage, "asset.json"), JsonSerializer.SerializeToUtf8Bytes(asset, Json));
                Directory.Move(stage, Path.Combine(Data, id));
                return asset;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
    }
    public Asset? Get(string id, string owner)
    {
        lock (gate)
        {
            if (!ValidId(id)) return null;
            var a = ReadMetadata(Path.Combine(Data, id, "asset.json"));
            return a?.Owner == owner && a.Id == id && File.Exists(Path.Combine(Data, id, "audio.wav")) ? a : null;
        }
    }
    private static Asset? ReadMetadata(string path)
    {
        try { return JsonSerializer.Deserialize<Asset>(File.ReadAllText(path), Json); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
    private IEnumerable<Asset> ListAll() => Directory.GetDirectories(Data).Where(d => ValidId(Path.GetFileName(d)))
        .Select(d => ReadMetadata(Path.Combine(d, "asset.json"))).OfType<Asset>();
    public Asset[] List(string owner) { lock (gate) return ListAll().Where(a => a.Owner == owner).OrderByDescending(a => a.Created).ToArray(); }
    public FileStream? Open(string id, string owner)
    {
        lock (gate) return Get(id, owner) != null ? new FileStream(Path.Combine(Data, id, "audio.wav"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
    }
    public Asset? Save(string id, string owner)
    {
        lock (gate)
        {
            var asset = Get(id, owner); if (asset == null) return null;
            asset = asset with { Saved = true };
            AtomicWrite(Path.Combine(Data, id, "asset.json"), JsonSerializer.SerializeToUtf8Bytes(asset, Json)); return asset;
        }
    }
    public bool Delete(string id, string owner)
    {
        lock (gate)
        {
            if (Get(id, owner) == null) return false;
            Directory.Delete(Path.Combine(Data, id), true); return true;
        }
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".new";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options)) { file.Write(bytes); file.Flush(true); }
        File.Move(temp, path, true);
    }
}
