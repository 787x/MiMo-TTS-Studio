namespace Studio;

public sealed record JobView(string Id, string State, int Completed, int Total, string? AssetId, string? Error,
    string SynthesisMode = "preset", GenerationSummary? Parameters = null);
public sealed class Jobs(Storage storage, Mimo mimo, IHostApplicationLifetime lifetime)
{
    private sealed class Job(string owner, int total)
    {
        public string Owner = owner;
        public JobView View = new(Guid.NewGuid().ToString("N"), "running", 0, total, null, null);
        public CancellationTokenSource Cancel = new();
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = [];
    private Task? current;
    public JobView Start(string owner, ScriptRequest request)
    {
        var plan = Composition.Plan(request);
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
            while (jobs.Count >= 100) { var first = jobs.First(); first.Value.Cancel.Dispose(); jobs.Remove(first.Key); }
            var job = new Job(owner, plan.Segments.Length); jobs[job.View.Id] = job;
            job.View = job.View with { SynthesisMode = request.SynthesisMode, Parameters = summary };
            string? sampleDataUrl = sample?.DataUrl;
            current = Task.Run(() => Run(job, request, plan, key, sampleDataUrl, summary)); return job.View;
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
    private async Task Run(Job job, ScriptRequest request, ScriptPlan plan, string key, string? sampleDataUrl, GenerationSummary summary)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, lifetime.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var ct = timeout.Token;
        try
        {
            var pieces = new List<Pcm>(); long bytes = 0;
            foreach (var segment in plan.Segments)
            {
                ct.ThrowIfCancellationRequested();
                var pcm = await mimo.Generate(key, request, plan, segment, ct, sampleDataUrl);
                bytes += pcm.Data.Length;
                if (bytes > Wave.MaxBytes - 44) throw new InputError("结果超出 64 MiB 音频上限。");
                pieces.Add(pcm);
                lock (gate) job.View = job.View with { Completed = pieces.Count };
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
            lock (gate) job.View = job.View with { State = job.Cancel.IsCancellationRequested ? "canceled" : "failed", Error = job.Cancel.IsCancellationRequested ? null : "任务或单段请求超时，或应用已停止。未自动重试，已发送请求仍可能计费。" };
        }
        catch (Exception e)
        {
            // Never log upstream bodies, credentials, text, or raw exception messages.
            lock (gate) job.View = job.View with { State = "failed", Error = e is InputError ? e.Message : "生成失败：网络或本地存储异常。未自动重试。" };
        }
    }
}
public sealed class BusyError : Exception;
