using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Studio;

static class Pr2Tests
{
    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var design = new ScriptRequest("这是试音正文。", SynthesisMode: "design", VoiceDescription: "成年女声，温暖醇厚", Style: "语速稍慢");
        var plan = Composition.Plan(design);
        var payload = JsonSerializer.SerializeToElement(Mimo.Payload(design, plan, plan.Segments[0]));
        check(payload.GetProperty("model").GetString() == "mimo-v2.5-tts-voicedesign" && !payload.GetProperty("audio").TryGetProperty("voice", out _), "design model and no preset voice");
        check(!payload.GetProperty("audio").GetProperty("optimize_text_preview").GetBoolean() && plan.Assistant[0] == design.Text && plan.User == design.VoiceDescription + "\n" + design.Style, "design keeps original text with explicit optimization disabled");
        check(plan.Segments.Length == 1, "design is exactly one request");
        reject(() => Composition.Plan(design with { Text = new string('中', 1001), SegmentCharacters = 50 }), "long design rejected instead of generating inconsistent voices");
        reject(() => Composition.Plan(design with { VoiceDescription = "" }), "design description required");
        reject(() => Composition.Plan(design with { Tags = "清亮" }), "design rejects preset tag control");
        reject(() => Composition.Plan(design with { Voice = "冰糖" }), "design rejects preset voice input");
        reject(() => Composition.Plan(design with { Singing = true }), "design rejects singing");
        reject(() => Composition.Plan(design with { OptimizeTextPreview = true }), "text optimization cannot silently rewrite text");
        reject(() => Composition.Plan(new("正文", SourceSampleId: Guid.NewGuid().ToString("N"))), "preset rejects reference sample");
        reject(() => Composition.Plan(new("正文", SynthesisMode: "clone")), "clone requires server sample ID");
        reject(() => Composition.Plan(new("正文", SynthesisMode: "clone", SourceSampleId: "../../etc/passwd")), "clone rejects path as ID");
        var wav = Wave.Merge([new(24000, new byte[48000])]);
        var mp3 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "tone.mp3"));
        check(SampleAudio.Inspect(wav, "wav") is { DurationSeconds: 1, SampleRate: 24000, Channels: 1 }, "WAV reference duration from complete audio");
        var mp3Info = SampleAudio.Inspect(mp3, "mp3");
        check(mp3Info.DurationSeconds is > 0.95 and < 1.1 && mp3Info.Channels == 1 && mp3Info.SampleRate == 44100, "Linux-capable MP3 full frame decode and measured duration");
        var vbr = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "tone-vbr.mp3"));
        check(SampleAudio.Inspect(vbr, "mp3") is { SampleRate: 24000, Channels: 2, DurationSeconds: > 0.95 and < 1.2 }, "MPEG2 stereo VBR with ID3 and Xing header fully decodes");
        reject(() => SampleAudio.Inspect(mp3, "wav"), "disguised MP3-as-WAV rejected");
        reject(() => SampleAudio.Inspect(wav, "mp3"), "disguised WAV-as-MP3 rejected");
        reject(() => SampleAudio.Inspect(mp3[..^1], "mp3"), "truncated MP3 rejected");
        var damaged = (byte[])mp3.Clone(); damaged[313] = 0;
        reject(() => SampleAudio.Inspect(damaged, "mp3"), "damaged middle MP3 frame rejected without resync");
        damaged = (byte[])mp3.Clone(); damaged[1] &= 0xfe;
        reject(() => SampleAudio.Inspect(damaged, "mp3"), "MP3 bad CRC rejected");
        reject(() => SampleAudio.Inspect([], "wav"), "empty reference rejected");
        reject(() => SampleAudio.Inspect(new byte[SampleAudio.MaxBytes + 1], "mp3"), "raw sample size bound");
        reject(() => SampleAudio.Inspect(Wave.Merge([new(8000, new byte[8000 * 2 * 121])]), "wav"), "decoded duration bound");
        reject(() => SampleAudio.Inspect(Wave.Merge([new(24000, new byte[48])]), "wav"), "empty-scale reference duration rejected");
        check(!SampleAudio.Eligible(SampleAudio.MaxBytes + 1L, 1, "wav") &&
              (SampleAudio.MaxBytes + 2L) / 3 * 4 + "data:audio/mpeg;base64,".Length < SampleAudio.MaxTransportBytes, "Data URL prefix and decimal 10 MB budget");
        string root = Path.Combine(Path.GetTempPath(), "mimotts-pr2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new Storage(Path.Combine(root, "etc"), Path.Combine(root, "audio"), Path.Combine(root, "tmp"));
            storage.SetKey("test-only-private-key");
            var original = storage.Commit("1000", design, wav);
            var sample = storage.SampleFromAsset(original.Id, "1000", "设计样本")!;
            check(original.SampleEligible && sample is { Source: "design", SourceAssetId: not null, DurationSeconds: 1 }, "design result converts to persistent independent sample");
            storage.Delete(original.Id, "1000");
            var reopened = new Storage(storage.Config, storage.Data, storage.Temp);
            check(reopened.GetSample(sample.Id, "1000") != null && reopened.Get(original.Id, "1000") == null, "sample survives source deletion and restart");
            check(reopened.SnapshotSample(sample.Id, "1000").DataUrl == "data:audio/wav;base64," + Convert.ToBase64String(wav), "clone WAV Data URL has exact stored bytes");
            var uploaded = storage.CreateSample("1000", "MP3 样本", "mp3", mp3);
            check(uploaded is { Format: "mp3", Mime: "audio/mpeg", Source: "upload" } && storage.SnapshotSample(uploaded.Id, "1000").DataUrl.StartsWith("data:audio/mpeg;base64,"), "MP3 persists without disguised MIME");
            check(storage.GetSample(sample.Id, "1001") == null && storage.OpenSample(sample.Id, "1001") == null && storage.SampleFromAsset(Guid.NewGuid().ToString("N"), "1001", "x") == null, "sample and conversion ownership");
            check(storage.GetSample("../audio", "1000") == null && !storage.DeleteSample(sample.Id, "1001"), "sample traversal and cross-user deletion denied");
            check(storage.RenameSample(sample.Id, "1000", "新的名称")?.Name == "新的名称", "sample rename");
            reject(() => storage.RenameSample(sample.Id, "1000", "\nname"), "sample name validation");
            var clone = new ScriptRequest(new string('甲', 55) + "。" + new string('乙', 55), Style: "平静", Tags: "温柔", SegmentCharacters: 50, SynthesisMode: "clone", SourceSampleId: sample.Id);
            var clonePlan = Composition.Plan(clone);
            var snapshot = storage.SnapshotSample(sample.Id, "1000");
            var clonePayload = JsonSerializer.SerializeToElement(Mimo.Payload(clone, clonePlan, clonePlan.Segments[0], snapshot.DataUrl));
            check(clonePayload.GetProperty("model").GetString() == "mimo-v2.5-tts-voiceclone" && clonePayload.GetProperty("audio").GetProperty("voice").GetString() == snapshot.DataUrl && !clonePayload.GetProperty("audio").TryGetProperty("optimize_text_preview", out _), "clone exact protocol without design optimizer");
            reject(() => Composition.Plan(clone with { Singing = true }), "clone singing input rejected");
            reject(() => Composition.Plan(clone with { VoiceDescription = "unsupported" }), "clone description input rejected");
            reject(() => Mimo.Payload(clone, clonePlan, "正文", "data:audio/wav;base64," + new string('A', SampleAudio.MaxTransportBytes)), "clone encoded transport cap");
            // Legacy PR1 asset metadata contains none of the added fields.
            string legacy = Guid.NewGuid().ToString("N"), legacyDir = Path.Combine(storage.Data, legacy);
            Directory.CreateDirectory(legacyDir); File.WriteAllBytes(Path.Combine(legacyDir, "audio.wav"), wav);
            File.WriteAllText(Path.Combine(legacyDir, "asset.json"), JsonSerializer.Serialize(new { id = legacy, owner = "1000", title = "PR1 asset", voice = "冰糖", created = DateTimeOffset.UtcNow, saved = true, bytes = wav.Length }));
            check(storage.Get(legacy, "1000") is { SynthesisMode: "preset", SourceSampleId: null } && storage.List("1000").Any(a => a.Id == legacy), "PR1 metadata defaults remain compatible");
            using (var audio = storage.Open(legacy, "1000")) check(audio?.Length == wav.Length, "PR1 audio remains readable");
            var handler = new Controlled(wav);
            var jobs = new Jobs(storage, new Mimo(new HttpClient(handler)), new Lifetime());
            var job = jobs.Start("1000", clone);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            storage.RenameSample(sample.Id, "1000", "任务开始后改名"); storage.DeleteSample(sample.Id, "1000");
            handler.Resume.TrySetResult();
            var done = await Finish(jobs, job.Id);
            check(done is { State: "succeeded", SynthesisMode: "clone", AssetId: not null } && handler.Payloads.Count == clonePlan.Segments.Length, "all clone segments finish after source deletion");
            check(handler.Payloads.All(p => p.GetProperty("audio").GetProperty("voice").GetString() == snapshot.DataUrl), "all segments use one immutable reference snapshot");
            check(done.Parameters?.SourceSampleName == "新的名称" && storage.Get(done.AssetId!, "1000") is { SynthesisMode: "clone", SourceSampleId: not null }, "safe job and asset summary preserve original source parameters");
            string safe = JsonSerializer.Serialize(done);
            check(!safe.Contains("base64") && !safe.Contains("test-only-private-key"), "task response never contains reference Data URL or Key");
            int calls = handler.Payloads.Count;
            storage.Save(done.AssetId!, "1000"); using (storage.OpenSample(uploaded.Id, "1000")?.File) { } using (storage.Open(done.AssetId!, "1000")) { }
            check(handler.Payloads.Count == calls, "sample list playback download and result save cause no synthesis");
            reject(() => jobs.Start("1000", clone), "deleted sample cannot start another clone task");
            handler.Reset();
            var canceled = jobs.Start("1000", design);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); jobs.Cancel(canceled.Id, "1000");
            check(await Finish(jobs, canceled.Id) is { State: "canceled", AssetId: null }, "design cancellation reuses existing cancellation path");
            handler.Reset();handler.Fail = true;
            var failed = jobs.Start("1000", clone with { SourceSampleId = uploaded.Id });
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); handler.Resume.TrySetResult();
            check(await Finish(jobs, failed.Id) is { State: "failed", AssetId: null }, "clone invalid WAV cannot commit successful asset");
            check(!Directory.GetDirectories(storage.Samples, ".stage-*").Any() && !Directory.GetDirectories(storage.Data, ".stage-*").Any(), "PR2 failure/cancel has no staged half-results");
            check(storage.DeleteSample(uploaded.Id, "1000") && storage.Get(legacy, "1000") != null && storage.Get(done.AssetId!, "1000") != null, "deleting sample preserves other audio");
            // Samples and audio participate in the same capacity check.
            File.WriteAllText(Path.Combine(storage.Samples, "quota-test"), "x");
            using (var quota = new FileStream(Path.Combine(storage.Samples, "quota-test"), FileMode.Open)) quota.SetLength(Storage.Quota);
            check(!storage.HasSpace(), "samples count toward shared disk quota");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<JobView> Finish(Jobs jobs, string id)
    {
        for (int i = 0; i < 1000; i++)
        {
            var view = jobs.Get(id, "1000")!;
            if (view.State is not ("running" or "canceling")) { await Task.Delay(5); return view; }
            await Task.Delay(5);
        }
        throw new Exception("job did not finish");
    }
    private sealed class Controlled(byte[] wave) : HttpMessageHandler
    {
        public List<JsonElement> Payloads = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail;
        public void Reset() { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Resume = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Payloads.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct)));
            Entered.TrySetResult(); await Resume.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(Fail ? "{}" : JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(wave) } } } } })) };
        }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default; public CancellationToken ApplicationStopping => default; public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
