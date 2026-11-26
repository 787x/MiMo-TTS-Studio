using System.Security.Cryptography;
using System.Text.Json;

namespace Studio;

public sealed record WorkView(string Id, string Title, string State, long Bytes, string? Sha256, long Total,
    long Completed, long Chapters, string? Error, string SynthesisMode, long Volumes, DateTimeOffset? Started = null);
public sealed record WorkCreate(string Title, string? Id = null);
public sealed record WorkPrepare(ScriptRequest Parameters, long Bytes, string? Sha256 = null);
public sealed record WorkAction(string Action, bool Confirm = false);
public sealed record WorkSegment(long Number, long Offset, int Bytes, long Chapter, bool Heading, string Text);
public sealed record WorkVolume(long Number, long Chapter, long First, long Last, long Bytes, int SampleRate);
internal sealed record WorkState(WorkView View, string Owner, ScriptRequest? Parameters = null,
    GenerationSummary? Summary = null, int Attempt = 0, bool InFlight = false,
    long NextVolumeStart = 0, string? Stop = null, string? SampleSha256 = null);
internal sealed record SegmentAudio(long Bytes, int Rate, string Sha256, int Attempt, DateTimeOffset CommittedAt);
internal sealed record SegmentAttempt(int Number, DateTimeOffset At, string State, string? Error = null);

public sealed partial class Works : IHostedService
{
    public const int ChunkBytes = 64 * 1024;
    private const int IndexBytes = 24;
    private const int PageSize = 24;
    private const long Reserve = 256L * 1024 * 1024;
    private const string LowSpace = "私有磁盘空间不足，释放空间后可恢复。";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Storage storage;
    private readonly Mimo mimo;
    private readonly Jobs jobs;
    private readonly CancellationToken stopping;
    private readonly string root;
    private readonly object gate = new();
    private Task? preparing, worker;
    private CancellationTokenSource? requestCancel;
    private string? workerId;
    private readonly Dictionary<string, WorkState> volatileStates = [];
    private volatile bool recovering;

    public Works(Storage storage, Mimo mimo, Jobs jobs, IHostApplicationLifetime lifetime)
    {
        this.storage = storage;
        this.mimo = mimo;
        this.jobs = jobs;
        stopping = lifetime.ApplicationStopping;
        root = storage.WorksRoot;
        Storage.PrivateDirectory(root);
    }

    private string Folder(string id) => Path.Combine(root, id);
    private string PathFor(string id, string name) => Path.Combine(Folder(id), name);
    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(input, Json);
    }
    private WorkState? ReadState(string id) => !Storage.ValidId(id) ? null :
        volatileStates.TryGetValue(id, out var pending) ? pending : Read<WorkState>(PathFor(id, "work.json"));
    private WorkState Owned(string id, string owner) =>
        ReadState(id) is { } work && work.Owner == owner ? work : throw new InputError("作品不存在或无权访问。");
    private static void Sync(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        ExportDirectory.SyncPrivate(directory);
    }
    private static void Put<T>(string path, T value) => PutBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static void PutBytes(string path, byte[] bytes)
    {
        Storage.AtomicWrite(path, bytes);
        Sync(Path.GetDirectoryName(path)!);
    }
    private void Save(WorkState state)
    {
        Put(PathFor(state.View.Id, "work.json"), state);
        volatileStates.Remove(state.View.Id);
    }
    internal Func<long, bool>? SpaceAvailable { get; init; }
    private bool HasSpace(long reservation) => SpaceAvailable?.Invoke(reservation) ??
        new DriveInfo(root).AvailableFreeSpace >= Reserve + reservation;
    private void CheckSpace(long reservation)
    {
        if (!HasSpace(reservation)) throw new InputError(LowSpace);
    }

    public WorkView Create(string owner, WorkCreate request)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 160 || title.Any(char.IsControl))
            throw new InputError("作品名称需为 1–160 个字符，且不能包含控制字符。");
        string id = request.Id ?? Guid.NewGuid().ToString("N");
        if (!Storage.ValidId(id)) throw new InputError("作品 ID 无效。");
        lock (gate)
        {
            if (ReadState(id) is { } existing)
                return existing.Owner == owner && existing.View.Title == title ? existing.View : throw new InputError("作品 ID 已使用。");
            CheckSpace(65536);
            Storage.PrivateDirectory(Folder(id));
            var state = new WorkState(new(id, title, "uploading", 0, null, 0, 0, 0, null, "preset", 0), owner);
            Save(state);
            Sync(root);
            return state.View;
        }
    }

    public WorkView Get(string id, string owner) { lock (gate) return Owned(id, owner).View; }

    public object List(string owner, int offset)
    {
        if (offset < 0) throw new InputError("分页位置无效。");
        lock (gate)
        {
            var items = Directory.EnumerateDirectories(root).Select(Path.GetFileName)
                .Select(id => TryReadState(id!))
                .Where(x => x?.Owner == owner).Skip(offset).Take(PageSize + 1).Select(x => x!.View).ToArray();
            return new { items = items.Take(PageSize), nextOffset = items.Length > PageSize ? (int?)(offset + PageSize) : null };
        }
    }

    private WorkState? TryReadState(string id)
    {
        try { return ReadState(id); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public WorkView Upload(string id, string owner, long offset, byte[] chunk, string sha256)
    {
        if (offset < 0 || chunk.Length is 0 or > ChunkBytes ||
            !Hash(chunk).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InputError("分块位置、大小或 SHA-256 校验无效。");
        lock (gate)
        {
            var state = Owned(id, owner);
            var view = state.View;
            if (view.State != "uploading") throw new InputError("作品已封存，不能追加正文。");
            using var file = OpenWrite(PathFor(id, "source.txt"));
            if (file.Length < view.Bytes) throw new InputError("已提交的正文文件不完整。");
            if (offset < view.Bytes && chunk.Length <= view.Bytes - offset)
            {
                file.Position = offset;
                var existing = new byte[chunk.Length];
                file.ReadExactly(existing);
                if (!existing.AsSpan().SequenceEqual(chunk)) throw new InputError("重传内容与已保存的分块不同。");
                return view;
            }
            if (offset != view.Bytes) throw new InputError($"分块位置不连续，服务端已保存 {view.Bytes} 字节。");
            CheckSpace(chunk.Length + 8192);
            file.SetLength(offset);
            file.Position = offset;
            file.Write(chunk);
            file.Flush(true);
            state = state with { View = view with { Bytes = checked(offset + chunk.Length) } };
            Save(state);
            return state.View;
        }
    }

    public WorkView Prepare(string id, string owner, WorkPrepare input)
    {
        if (input.Parameters == null || input.Parameters.Text != "") throw new InputError("长文本参数不接受内联正文。");
        var plan = Composition.Parameters(input.Parameters);
        if (input.Parameters.SynthesisMode == "design" || !input.Parameters.Split)
            throw new InputError("长篇需要开启分段并选择预置音色或 VoiceClone。");
        if (input.Sha256 != null && (input.Sha256.Length != 64 || !input.Sha256.All(char.IsAsciiHexDigit)))
            throw new InputError("正文 SHA-256 无效。");
        lock (gate)
        {
            var state = Owned(id, owner);
            if (state.View.State is not ("uploading" or "invalid" or "ready") || state.View.Completed != 0)
                throw new InputError("当前作品不能重新分段。");
            if (recovering || preparing is { IsCompleted: false }) throw new BusyError();
            if (input.Bytes <= 0 || input.Bytes != state.View.Bytes) throw new InputError("上传尚未完成或正文为空。");
            var sample = input.Parameters.SynthesisMode == "clone" ? storage.GetSample(input.Parameters.SourceSampleId!, owner)
                ?? throw new InputError("参考样本不存在或无权使用。") : null;
            var summary = new GenerationSummary(input.Parameters.SynthesisMode, Composition.Model(input.Parameters.SynthesisMode),
                input.Parameters.SynthesisMode == "preset" ? input.Parameters.Voice : null, plan.User, plan.Prefix, 0, sample?.Id, sample?.Name);
            state = state with { Parameters = input.Parameters, Summary = summary, View = state.View with
            {
                State = "planning", Error = null, SynthesisMode = input.Parameters.SynthesisMode, Total = 0, Chapters = 0, Sha256 = input.Sha256
            }};
            Save(state);
            preparing = Task.Run(() => BuildIndex(state));
            return state.View;
        }
    }

    private void BuildIndex(WorkState state)
    {
        string id = state.View.Id, source = PathFor(id, "source.txt"), index = PathFor(id, "segments.idx");
        try
        {

            long count = 0, chapters = 0, bytes = 0;
            bool hasText = false;
            int budget = 3000 - LongText.Utf8.GetByteCount(state.Summary!.Prefix);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sourceDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = OpenWrite(index + ".new"))
            using (var writer = new BinaryWriter(output))
            {
                output.SetLength(0);
                using (var original = OpenWrite(source))
                {
                    if (original.Length < state.View.Bytes) throw new InputError("已提交的正文文件不完整。");
                    original.SetLength(state.View.Bytes);
                    original.Flush(true);
                    var raw = new byte[8192];
                    int n;
                    while ((n = original.Read(raw)) != 0)
                    {
                        stopping.ThrowIfCancellationRequested();
                        sourceDigest.AppendData(raw.AsSpan(0, n));
                    }
                }
                foreach (var segment in LongText.Read(source, budget, state.Parameters!.SegmentCharacters, stopping))
                {
                    if (count % 256 == 0) CheckSpace(64 * 1024);
                    var data = LongText.Utf8.GetBytes(segment.Text);
                    writer.Write(segment.Offset);
                    writer.Write(data.Length);
                    writer.Write(segment.Heading);
                    writer.Write(new byte[3]);
                    writer.Write(segment.Chapter);
                    digest.AppendData(data);
                    bytes += data.Length;
                    count++;
                    chapters = segment.Chapter + 1;
                    hasText |= !string.IsNullOrWhiteSpace(segment.Text);
                }
                output.Flush(true);
            }
            string hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            if (!hasText) throw new InputError("正文不能为空或全为空白。");
            if (bytes != state.View.Bytes || !hash.Equals(Convert.ToHexString(sourceDigest.GetHashAndReset()), StringComparison.OrdinalIgnoreCase) ||
                state.View.Sha256 is { } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InputError("正文长度或 SHA-256 不匹配；请检查原始文件并重新提交。");
            File.Move(index + ".new", index, true);
            Sync(Folder(id));
            lock (gate) Save(state with { View = state.View with { State = "ready", Total = count, Chapters = chapters, Sha256 = hash } });
        }
        catch (Exception e)
        {
            if (stopping.IsCancellationRequested) return;
            string error = e switch
            {
                InputError => e.Message,
                System.Text.DecoderFallbackException => "正文包含无效 UTF-8 序列。",
                _ => "无法读取正文或写入分段索引，请检查私有磁盘空间。"
            };
            lock (gate) TrySave(state with { View = state.View with { State = "invalid", Error = error } });
        }
    }

    private static FileStream OpenWrite(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.Read };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private WorkSegment ReadSegment(long number, BinaryReader index, FileStream? source)
    {
        index.BaseStream.Position = checked(number * IndexBytes);
        long start = index.ReadInt64();
        int length = index.ReadInt32();
        bool heading = index.ReadBoolean();
        index.BaseStream.Position += 3;
        long chapter = index.ReadInt64();
        if (start < 0 || length is < 1 or > 3000 || chapter < 0) throw new IOException("Invalid segment index.");
        if (source == null) return new(number, start, length, chapter, heading, "");
        source.Position = start;
        var data = new byte[length];
        source.ReadExactly(data);
        return new(number, start, length, chapter, heading, LongText.Utf8.GetString(data));
    }

    public object Preview(string id, string owner, long offset)
    {
        WorkState state;
        BinaryReader index;
        FileStream source;
        lock (gate)
        {
            state = Owned(id, owner);
            if (state.View.Total == 0 || offset < 0 || offset >= state.View.Total) throw new InputError("作品尚未分段或分页位置无效。");
            index = new BinaryReader(File.OpenRead(PathFor(id, "segments.idx")));
            try { source = File.OpenRead(PathFor(id, "source.txt")); }
            catch { index.Dispose(); throw; }
        }
        using (index)
        using (source)
        {
            var items = new List<WorkSegment>();
            for (long n = offset; n < state.View.Total && n - offset < 12; n++) items.Add(ReadSegment(n, index, source));
            long next = offset + items.Count;
            return new { items, nextOffset = next < state.View.Total ? (long?)next : null, state.Summary!.User, state.Summary.Prefix, state.Summary.Model, state.Summary.Voice, state.Summary.SourceSampleName, state.Parameters!.SegmentCharacters };
        }
    }

    public void Delete(string id, string owner)
    {
        lock (gate)
        {
            var state = Owned(id, owner);
            if (recovering || workerId == id || state.View.State == "planning") throw new BusyError();
            Directory.Delete(Folder(id), true);
            volatileStates.Remove(id);
            Sync(root);
        }
    }

    private void TrySave(WorkState state)
    {
        try
        {
            Save(state);
        }
        catch (Exception e) when (e is IOException or InputError or UnauthorizedAccessException)
        {
            volatileStates[state.View.Id] = state;
        }
    }
}
