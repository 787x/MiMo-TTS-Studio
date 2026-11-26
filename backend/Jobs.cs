namespace Studio;

public sealed record JobView(string Id, string State, int Completed, int Total, string? AssetId, string? Error,
    string SynthesisMode = "preset", GenerationSummary? Parameters = null);
public sealed class Jobs(Storage storage, Mimo mimo, IHostApplicationLifetime lifetime, GenerationSlot? sharedSlot = null)
{
    private sealed class Job(string owner, int total)
    {
        public string Owner = owner;
        public JobView View = new(Guid.NewGuid().ToString("N"), "running", 0, total, null, null);
        public CancellationTokenSource Cancel = new();
    }
    internal GenerationSlot Slot { get; } = sharedSlot ?? new();
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = [];
    private Task? current;
    public JobView Start(string owner, ScriptRequest request)
    {
        var plan = Composition.Plan(request);
        var elements = plan.Segments.Select(s => System.Globalization.StringInfo.ParseCombiningCharacters(s).Length).ToArray();
        // Plan conservatively for 96 kHz PCM16, allowing at least one second
        // of audio per call and 20 spoken text elements per second.
        long budget = Wave.MaxBytes - 44L;
        int estimatedBytes = 96000 * 2;
        if (elements.Sum(count => Math.Max(20L, count) * estimatedBytes / 20) > budget)
            throw new LongWorkRequired();
        lock (gate)
        {
            if (current is { IsCompleted: false }) throw new BusyError();
            var key = storage.ReadKey();
            if (string.IsNullOrEmpty(key)) throw new InputError("请先在设置中配置 MiMo API Key。");
            if (!storage.HasSpace()) throw new InputError("音频库或磁盘空间不足，请清理旧音频。");
            // Read under Storage's lock before the job starts. Later rename/delete cannot
            // change the bytes or safe summary used by any segment of this task.
            var sample = request.SynthesisMode == "clone" ? storage.SnapshotSample(request.SourceSampleId!, owner) : null;
            var summary = new GenerationSummary(request.SynthesisMode, Composition.Model(request.SynthesisMode),
                request.SynthesisMode == "preset" ? request.Voice : null, plan.User, plan.Prefix, plan.Segments.Length,
                sample?.Metadata.Id, sample?.Metadata.Name);
            if (!Slot.Enter()) throw new BusyError();
            while (jobs.Count >= 100) { var first = jobs.First(); first.Value.Cancel.Dispose(); jobs.Remove(first.Key); }
            var job = new Job(owner, plan.Segments.Length); jobs[job.View.Id] = job;
            job.View = job.View with { SynthesisMode = request.SynthesisMode, Parameters = summary };
            string? sampleDataUrl = sample?.DataUrl;
            current = Task.Run(() => Run(job, request, plan, key, sampleDataUrl, summary, elements)); return job.View;
        }
    }
    public JobView? Get(string id, string owner) { lock (gate) return jobs.TryGetValue(id, out var job) && job.Owner == owner ? job.View : null; }
    public JobView[] List(string owner) { lock (gate) return jobs.Values.Where(j => j.Owner == owner).Select(j => j.View).ToArray(); }
    public JobView? Cancel(string id, string owner)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || job.Owner != owner) return null;
            if (job.View.State == "running") { job.View = job.View with { State = "canceling" }; job.Cancel.Cancel(); }
            return job.View;
        }
    }
    private async Task Run(Job job, ScriptRequest request, ScriptPlan plan, string key, string? sampleDataUrl, GenerationSummary summary, int[] elements)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, lifetime.ApplicationStopping);
        var ct = timeout.Token;
        try
        {
            var pieces = new List<Pcm>(); long bytes = 0;
            double observedBytesPerElement = 0;
            int remainingElements = elements.Sum();
            for (int i = 0; i < plan.Segments.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pcm = await mimo.Generate(key, request, plan, plan.Segments[i], ct, sampleDataUrl);
                bytes += pcm.Data.Length;
                if (bytes > Wave.MaxBytes - 44) throw new InputError("结果超出 64 MiB 音频上限。");
                if (pieces.Count > 0 && pieces[0].Rate != pcm.Rate)
                    throw new InputError("分段采样率不同，无法合并单个 WAV。未自动重试。");
                pieces.Add(pcm);
                lock (gate) job.View = job.View with { Completed = pieces.Count };
                remainingElements -= elements[i];
                observedBytesPerElement = Math.Max(observedBytesPerElement, pcm.Data.Length / (double)Math.Max(1, elements[i]));
                if (i + 1 < elements.Length && bytes + observedBytesPerElement * remainingElements > Wave.MaxBytes - 44)
                    throw new InputError("按已收到的音频估算，剩余分段将超过单个 WAV 容量。已停止发送，未自动重试。可使用长篇作品分卷生成。");
            }
            byte[] wave = Wave.Merge(pieces);
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                var asset = storage.Commit(job.Owner, request, wave, summary);
                job.View = job.View with { State = "succeeded", AssetId = asset.Id };
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate) job.View = job.View with { State = job.Cancel.IsCancellationRequested ? "canceled" : "failed", Error = job.Cancel.IsCancellationRequested ? null : "任务或单段请求超时，或应用已停止。未自动重试。" };
        }
        catch (Exception e)
        {
            // Never log upstream bodies, credentials, text, or raw exception messages.
            lock (gate) job.View = job.View with { State = "failed", Error = e is InputError ? e.Message : "生成失败：网络或本地存储异常。未自动重试。" };
        }
        finally { Slot.Leave(); }
    }
}
public sealed class BusyError : Exception;

public sealed class LongWorkRequired() : InputError("预计音频超出单个 WAV 的安全预算。请使用长篇作品分卷生成（CLI/MCP 使用 create_work）。");
