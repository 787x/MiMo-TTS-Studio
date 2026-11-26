using System.Globalization;
using System.Security.Cryptography;

namespace Studio;

public sealed partial class Works
{
    private static string Number(long value) => value.ToString("D19", CultureInfo.InvariantCulture);
    private string SegmentFolder(string id, long segment) => PathFor(id, Path.Combine("audio", Number(segment / 1000), Number(segment)));
    private string VolumePath(string id, long number) => PathFor(id, Path.Combine("volumes", Number(number / 1000), Number(number) + ".json"));

    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        string parent = Path.GetDirectoryName(path)!;
        EnsureDirectory(parent);
        Storage.PrivateDirectory(path);
        Sync(parent);
    }

    public WorkView Control(string id, string owner, WorkAction input)
    {
        lock (gate)
        {
            var state = Owned(id, owner);
            if (recovering) throw new BusyError();
            switch (input.Action)
            {
                case "start" when state.View.State == "ready":
                case "resume" when state.View.State is "paused" or "failed" or "uncertain":
                    if (state.View.State == "uncertain" && !input.Confirm)
                        throw new InputError("上一次请求可能已经计费。确认后才允许重新发送。");
                    if (workerId != null || !jobs.Slot.Enter()) throw new BusyError();
                    try
                    {
                        if (storage.ReadKey() == null) throw new InputError("请先配置 MiMo API Key。");
                        if (state.View.State == "ready" && state.Parameters!.SynthesisMode == "clone")
                        {
                            var sample = storage.SnapshotSample(state.Parameters.SourceSampleId!, owner);
                            CheckSpace(sample.DataUrl.Length + 8192);
                            var snapshot = LongText.Utf8.GetBytes(sample.DataUrl);
                            PutBytes(PathFor(id, "sample.snapshot"), snapshot);
                            state = state with { SampleSha256 = Hash(snapshot), Summary = state.Summary! with { SourceSampleName = sample.Metadata.Name } };
                        }
                        if (state.View.State == "uncertain" && state.InFlight)
                        {
                            Record(state, "retry_confirmed");
                            state = state with { InFlight = false };
                        }
                        return Launch(state);
                    }
                    catch { jobs.Slot.Leave(); throw; }
                case "pause" when state.View.State == "running" && workerId == id:
                    state = state with { Stop = "paused", View = state.View with { State = "pausing" } };
                    Save(state);
                    return state.View;
                case "cancel" when state.View.State is "running" or "pausing" && workerId == id:
                    state = state with { Stop = "canceled", View = state.View with { State = "canceling" } };
                    Save(state);
                    requestCancel?.Cancel();
                    return state.View;
                case "cancel" when state.View.State is "paused" or "failed" or "uncertain" or "ready":
                    state = state with { Stop = "canceled", View = state.View with { State = "canceled" } };
                    Save(state);
                    return state.View;
                default:
                    throw new InputError("当前作品状态不支持此操作。");
            }
        }
    }

    private WorkView Launch(WorkState state)
    {
        state = state with { Stop = null, View = state.View with { State = "running", Error = null, Started = state.View.Started ?? DateTimeOffset.UtcNow } };
        Save(state);
        workerId = state.View.Id;
        requestCancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var ct = requestCancel.Token;
        worker = Task.Run(() => Run(state.View.Id, state.Owner, ct));
        return state.View;
    }

    private void Record(WorkState state, string status, string? error = null)
    {
        string folder = SegmentFolder(state.View.Id, state.View.Completed);
        EnsureDirectory(folder);
        Put(Path.Combine(folder, $"attempt-{state.Attempt:D10}-{status}.json"),
            new SegmentAttempt(state.Attempt, DateTimeOffset.UtcNow, status, error));
    }

    private void PublishSegment(string id, long number, Pcm pcm, int attempt)
    {
        string folder = SegmentFolder(id, number);
        string staged = Path.Combine(folder, "result.new");
        EnsureDirectory(staged);
        var bytes = Wave.Merge([pcm]);
        Put(Path.Combine(staged, "audio.json"), new SegmentAudio(bytes.LongLength, pcm.Rate, Hash(bytes), attempt, DateTimeOffset.UtcNow));
        PutBytes(Path.Combine(staged, "audio.wav"), bytes);
        PromoteResult(folder, staged);
    }

    private static void PromoteResult(string folder, string staged)
    {
        Directory.Move(staged, Path.Combine(folder, "result"));
        Sync(folder);
    }

    private SegmentAudio? Receipt(string id, long n) =>
        Read<SegmentAudio>(Path.Combine(SegmentFolder(id, n), "result", "audio.json"));
    private SegmentAudio? CommittedReceipt(string id, long n)
    {
        string folder = Path.Combine(SegmentFolder(id, n), "result");
        try
        {
            // File.Exists suppresses access and I/O failures. Unreadable
            // evidence must never be interpreted as an unsent request.
            _ = File.GetAttributes(folder);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        return Receipt(id, n) ?? throw new InputError("已提交的音频记录缺失，未重新发送。");
    }

    private string AudioPath(string id, long n) => Path.Combine(SegmentFolder(id, n), "result", "audio.wav");

    private void RecoverStagedResult(string id, long n)
    {
        string folder = SegmentFolder(id, n), staged = Path.Combine(folder, "result.new");
        if (!Directory.Exists(staged) || Directory.Exists(Path.Combine(folder, "result"))) return;
        string file = Path.Combine(staged, "audio.wav");
        var audio = Read<SegmentAudio>(Path.Combine(staged, "audio.json"));
        if (audio != null && File.Exists(file) && Verified(file, audio)) PromoteResult(folder, staged);
        else Directory.Delete(staged, true);
    }

    private static bool Verified(string path, SegmentAudio audio)
    {
        using var file = File.OpenRead(path);
        return Verified(file, audio);
    }

    private static bool Verified(Stream file, SegmentAudio audio)
    {
        if (file.Length != audio.Bytes || !Wave.NormalizedHeader(file, audio.Rate, audio.Bytes)) return false;
        file.Position = 0;
        return Convert.ToHexString(SHA256.HashData(file)).Equals(audio.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Both the receipt and closed volume records are published before advancing
    // the work cursor. Repeating this local step after a crash sends no request.
    private WorkState Advance(WorkState state, SegmentAudio audio, BinaryReader index)
    {
        string id = state.View.Id;
        long n = state.View.Completed;
        var segment = ReadSegment(n, index, null);
        long first = state.NextVolumeStart;
        if (first < n)
        {
            var firstSegment = ReadSegment(first, index, null);
            if (segment.Chapter != firstSegment.Chapter || audio.Rate != Receipt(id, first)!.Rate ||
                n - first >= 16 || VolumeBytes(id, first, n - 1) + audio.Bytes - 44 > 16L * 1024 * 1024)
                state = CloseVolume(state, firstSegment.Chapter, n - 1);
        }
        return state with { InFlight = false, Attempt = 0, View = state.View with { Completed = n + 1 } };
    }

    private long VolumeBytes(string id, long first, long last)
    {
        long bytes = 44;
        for (long n = first; n <= last; n++) bytes += Receipt(id, n)!.Bytes - 44;
        return bytes;
    }

    private WorkState CloseVolume(WorkState state, long chapter, long last)
    {
        long number = state.View.Volumes;
        string id = state.View.Id;
        long first = state.NextVolumeStart;
        string path = VolumePath(id, number);
        EnsureDirectory(Path.GetDirectoryName(path)!);
        Put(path, new WorkVolume(number, chapter, first, last, VolumeBytes(id, first, last), Receipt(id, first)!.Rate));
        return state with { NextVolumeStart = last + 1, View = state.View with { Volumes = number + 1 } };
    }

    private WorkState FinishVolume(WorkState state, BinaryReader index) =>
        state.NextVolumeStart < state.View.Completed ? CloseVolume(state,
            ReadSegment(state.NextVolumeStart, index, null).Chapter, state.View.Completed - 1) : state;

    private async Task Run(string id, string owner, CancellationToken ct)
    {
        try
        {
            using var index = new BinaryReader(File.OpenRead(PathFor(id, "segments.idx")));
            using var source = File.OpenRead(PathFor(id, "source.txt"));
            WorkState state;
            lock (gate) state = Owned(id, owner);
            VerifySource(state, ct);
            CheckResumeTail(state);
            string? sample = null;
            if (state.Parameters!.SynthesisMode == "clone")
            {
                using var snapshot = File.OpenRead(PathFor(id, "sample.snapshot"));
                if (snapshot.Length > SampleAudio.MaxTransportBytes) throw new InputError("参考样本快照过大。");
                var data = new byte[(int)snapshot.Length];
                snapshot.ReadExactly(data);
                if (Hash(data) != state.SampleSha256) throw new InputError("参考样本快照损坏。");
                sample = LongText.Utf8.GetString(data);
            }
            var plan = new ScriptPlan(state.Summary!.User, state.Summary.Prefix, []);
            while (true)
            {
                WorkSegment segment;
                string key;
                lock (gate)
                {
                    state = Owned(id, owner);
                    if (state.View.Completed > state.NextVolumeStart && (state.Stop != null || state.View.Completed == state.View.Total))
                        state = FinishVolume(state, index);
                    if (state.Stop != null || state.View.Completed == state.View.Total || ct.IsCancellationRequested)
                    {
                        var final = state.Stop ?? (state.View.Completed == state.View.Total ? "succeeded" : "running");
                        Save(state with { View = state.View with { State = final } });
                        return;
                    }
                    // A previous attempt may have committed audio before the
                    // metadata write failed. Reuse it before considering dispatch.
                    RecoverStagedResult(id, state.View.Completed);
                    if (CommittedReceipt(id, state.View.Completed) is { } existing)
                    {
                        if (!Verified(AudioPath(id, state.View.Completed), existing)) throw new InputError("已提交的分段音频损坏，未重新发送。");
                        Save(Advance(state, existing, index));
                        continue;
                    }
                    if (state.InFlight) throw new InputError("上一请求结果不确定。");
                    CheckSpace(Wave.MaxBytes + 64 * 1024);
                    key = storage.ReadKey() ?? throw new InputError("请先配置 MiMo API Key，再恢复任务。");
                    segment = ReadSegment(state.View.Completed, index, source);
                }
                var pcm = await mimo.Generate(key, state.Parameters!, plan, segment.Text, ct, sample, () =>
                {
                    lock (gate)
                    {
                        var current = Owned(id, owner);
                        ct.ThrowIfCancellationRequested();
                        if (current.Stop != null) throw new InputError("任务已暂停。");
                        current = current with { Attempt = current.Attempt + 1, InFlight = true };
                        Record(current, "dispatch");
                        Save(current);
                    }
                });
                lock (gate)
                {
                    // Successfully received audio remains useful even if pause,
                    // cancel or process shutdown arrived during reception.
                    var latest = Owned(id, owner);
                    PublishSegment(id, segment.Number, pcm, latest.Attempt);
                    Save(Advance(latest, Receipt(id, segment.Number)!, index));
                }
            }
        }
        catch (Exception e)
        {
            lock (gate)
            {
                var state = Owned(id, owner);
                bool resultStored = false;
                try { resultStored = Receipt(id, state.View.Completed) != null; }
                catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { }
                bool rejected = e is UpstreamError { Status: 400 or 401 or 403 or 404 or 413 or 422 or 429 };
                bool uncertain = state.InFlight && !rejected && !resultStored;
                string? errorText = e is InputError ? e.Message : e is OperationCanceledException ?
                    "请求中断或超时。" : "网络或私有存储异常。";
                string status = state.Stop == "canceled" ? "canceled" : uncertain ? "uncertain" :
                    state.Stop == "paused" || resultStored || e is IOException || errorText == LowSpace ? "paused" :
                    stopping.IsCancellationRequested ? "running" : "failed";
                if (uncertain) errorText += " 云端可能已经执行并计费，须确认后才能重试。";
                try
                {
                    if (state.InFlight) Record(state, uncertain ? "uncertain" : rejected ? "rejected" : "stored", errorText);
                    using var index = new BinaryReader(File.OpenRead(PathFor(id, "segments.idx")));
                    state = FinishVolume(state, index);
                }
                catch (Exception error) when (error is IOException or InputError or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                TrySave(state with { InFlight = uncertain, View = state.View with { State = status, Error = errorText } });
            }
        }
        finally
        {
            lock (gate)
            {
                requestCancel?.Dispose();
                requestCancel = null;
                workerId = null;
                jobs.Slot.Leave();
            }
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        recovering = true;
        recovery = Task.Run(Recover);
        return Task.CompletedTask;
    }

    private Task? recovery;
    internal Task Recovery => recovery ?? Task.CompletedTask;
    private void Recover()
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root))
            {
                stopping.ThrowIfCancellationRequested();
                string id = Path.GetFileName(path);
                WorkState? state;
                lock (gate) state = TryReadState(id);
                if (state == null) continue;
                if (state.View.State == "planning")
                {
                    // Preparation owns its own slot and can scan a large source.
                    lock (gate)
                    {
                        if (preparing is not { IsCompleted: false }) preparing = Task.Run(() => BuildIndex(state));
                        else TrySave(state with { View = state.View with { State = "invalid", Error = "请重新分段。" } });
                    }
                    continue;
                }
                if (state.View.State is not ("running" or "pausing" or "canceling")) continue;
                try
                {
                    CheckResumeTail(state);
                    long completed = state.View.Completed;
                    if (completed < state.View.Total) RecoverStagedResult(id, completed);
                    using var index = new BinaryReader(File.OpenRead(PathFor(id, "segments.idx")));
                    if (completed < state.View.Total && CommittedReceipt(id, completed) is { } pending)
                    {
                        if (!Verified(AudioPath(id, completed), pending)) throw new InputError($"第 {completed + 1} 段音频损坏。");
                        state = Advance(state, pending, index);
                    }
                    if (state.View.State is "running" or "pausing" or "canceling")
                    {
                        string status = state.Stop == "canceled" ? "canceled" : state.InFlight ? "uncertain" :
                            state.Stop == "paused" ? "paused" : "running";
                        state = state with { View = state.View with { State = status, Error = state.InFlight ?
                            "重启时请求结果未确认，云端可能已执行并计费。" : null } };
                    }
                    if (state.View.State != "running") state = FinishVolume(state, index);
                    lock (gate)
                    {
                        if (state.View.State == "running")
                        {
                            if (workerId == null && jobs.Slot.Enter())
                            {
                                try { Launch(state); }
                                catch { jobs.Slot.Leave(); throw; }
                            }
                            else TrySave(state with { View = state.View with { State = "paused", Error = "有其他任务正在执行，请手动恢复。" } });
                        }
                        else if (state != ReadState(id)) TrySave(state);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lock (gate) TrySave(state with { View = state.View with { State = "damaged", Error = e is InputError ? e.Message : "无法验证作品文件，请检查存储。" } });
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { recovering = false; }
    }

    private void VerifySource(WorkState state, CancellationToken ct)
    {
        string id = state.View.Id;
        using var source = File.OpenRead(PathFor(id, "source.txt"));
        using var index = new BinaryReader(File.OpenRead(PathFor(id, "segments.idx")));
        if (source.Length != state.View.Bytes || index.BaseStream.Length != checked(state.View.Total * IndexBytes))
            throw new InputError("正文或分段索引长度已变化。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[65536];
        int count;
        while ((count = source.Read(bytes)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(bytes.AsSpan(0, count));
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(state.View.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InputError("正文 SHA-256 已变化。");
        long cursor = 0;
        long chapter = 0;
        for (long number = 0; number < state.View.Total; number++)
        {
            ct.ThrowIfCancellationRequested();
            var part = ReadSegment(number, index, null);
            if (part.Offset != cursor || part.Chapter < chapter || part.Chapter > chapter + 1)
                throw new InputError("正文分段索引顺序不完整。");
            cursor += part.Bytes;
            chapter = part.Chapter;
        }
        if (cursor != state.View.Bytes || chapter + 1 != state.View.Chapters)
            throw new InputError("正文分段索引覆盖范围不完整。");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (recovery != null) await recovery.WaitAsync(ct);
        Task?[] pending;
        lock (gate) pending = [worker, preparing];
        foreach (var task in pending)
            if (task != null) await task.WaitAsync(ct);
    }
}
