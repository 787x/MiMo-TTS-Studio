using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio;

internal static class LongWorkTests
{
    private const string Owner = "1000";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string root = Path.Combine(Path.GetTempPath(), "mimo-works-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new Storage(Path.Combine(root, "etc"), Path.Combine(root, "audio"), Path.Combine(root, "temp"));
            storage.SetKey("long-test-example");
            var cloud = new WorkCloud();
            var mimo = new Mimo(new HttpClient(cloud));
            var lifetime = new Lifetime();
            var jobs = new Jobs(storage, mimo, lifetime);
            bool enough = true;
            var works = new Works(storage, mimo, jobs, lifetime) { SpaceAvailable = _ => enough };
            var options = new ScriptRequest("", Voice: "冰糖", Tags: "温柔", Singing: true, SegmentCharacters: 50);
            int beforeShort = cloud.Calls;
            reject(() => jobs.Start(Owner, options with { Text = new string('x', 60_000) }),
                "single-WAV path rejects a large paid workload before any dispatch");
            check(cloud.Calls == beforeShort, "short admission spends no MiMo calls for a 60,000-byte, 1,200-segment plan");

            var listing = new List<WorkView>();
            for (int n = 0; n < 29; n++) listing.Add(works.Create(Owner, new("List " + n)));
            var other = works.Create("1001", new("Owner 1001"));
            var page1 = JsonSerializer.SerializeToElement(works.List(Owner, 0), Json);
            var page2 = JsonSerializer.SerializeToElement(works.List(Owner, page1.GetProperty("nextOffset").GetInt32()), Json);
            var ids = page1.GetProperty("items").EnumerateArray().Concat(page2.GetProperty("items").EnumerateArray())
                .Select(item => item.GetProperty("id").GetString()!).ToArray();
            check(ids.SequenceEqual(listing.OrderByDescending(work => work.Created).ThenBy(work => work.Id, StringComparer.Ordinal).Select(work => work.Id)) &&
                JsonElement.DeepEquals(JsonSerializer.SerializeToElement(works.List(Owner, 0), Json), page1) &&
                page2.GetProperty("nextOffset").ValueKind == JsonValueKind.Null,
                "work pages are newest-first, stable, complete and owner-scoped");
            var replay = works.Create(Owner, new("List 28", listing[^1].Id));
            check(replay.Id == listing[^1].Id &&
                JsonElement.DeepEquals(JsonSerializer.SerializeToElement(works.List(Owner, 0), Json), page1),
                "same creation UUID after a lost response returns the original work");
            reject(() => works.Create("1001", new("List 28", replay.Id)), "creation replay cannot cross ownership");
            works.Delete(other.Id, "1001");
            foreach (var work in listing) works.Delete(work.Id, Owner);

            byte[] full = Encoding.UTF8.GetBytes(new string('字', 60_000) + "结尾");
            const int retainedBytes = 140001;
            byte[] prefix = full[..retainedBytes];
            byte[] changed = (byte[])full.Clone();
            changed[^1] ^= 1;
            var interruptedUpload = works.Create(Owner, new("Bound upload"));
            string uploadId = interruptedUpload.Id;
            await works.BindUpload(uploadId, Owner, new(full.Length, Digest(full), 0, Digest([])), default);
            for (int offset = 0; offset < retainedBytes; offset += Works.ChunkBytes)
            {
                byte[] chunk = prefix[offset..Math.Min(retainedBytes, offset + Works.ChunkBytes)];
                works.Upload(uploadId, Owner, offset, chunk, Digest(chunk));
            }
            var reopenedUpload = new Works(storage, mimo, jobs, lifetime);
            try
            {
                await reopenedUpload.BindUpload(uploadId, Owner, new(changed.Length, Digest(changed), retainedBytes, Digest(prefix)), default);
                throw new Exception("changed suffix accepted");
            }
            catch (InputError) { check(reopenedUpload.Get(uploadId, Owner).Bytes == retainedBytes, "bound identity rejects a changed suffix before append"); }
            try
            {
                await reopenedUpload.BindUpload(uploadId, Owner, new(full.Length, Digest(full), retainedBytes, Digest([])), default);
                throw new Exception("changed prefix accepted");
            }
            catch (InputError) { check(true, "existing prefix must match without retransmitting it"); }
            await reopenedUpload.BindUpload(uploadId, Owner, new(full.Length, Digest(full), retainedBytes, Digest(prefix)), default);
            byte[] tail = full[retainedBytes..];
            check(reopenedUpload.Upload(uploadId, Owner, retainedBytes, tail, Digest(tail)).Bytes == full.Length,
                "restored cursor accepts only the remaining suffix");
            reopenedUpload.Prepare(uploadId, Owner, new(options, full.Length));
            check((await State(reopenedUpload, uploadId, "ready")).Sha256 == Digest(full),
                "bound full digest is verified even when legacy prepare omits its hash");
            reopenedUpload.Delete(uploadId, Owner);

            var mixed = works.Create(Owner, new("Mixed upload"));
            await works.BindUpload(mixed.Id, Owner, new(3, Digest([1, 2, 3]), 0, Digest([])), default);
            works.Upload(mixed.Id, Owner, 0, [1, 2, 4], Digest([1, 2, 4]));
            works.Prepare(mixed.Id, Owner, new(options, 3));
            check((await State(works, mixed.Id, "invalid")).Error!.Contains("SHA-256"),
                "per-chunk checksums cannot make a mixed upload valid");
            works.Delete(mixed.Id, Owner);

            var bigCloud = new WorkCloud { ResponseBytes = 14 * 1024 * 1024 };
            var shortJobs = new Jobs(storage, new Mimo(new HttpClient(bigCloud)), lifetime);
            var oversizedJob = shortJobs.Start(Owner, new(new string('x', 300), SegmentCharacters: 50));
            JobView shortStatus;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                while ((shortStatus = shortJobs.Get(oversizedJob.Id, Owner)!).State == "running")
                    await Task.Delay(10, timeout.Token);
            }
            check(shortStatus.State == "failed" && shortStatus.AssetId == null && bigCloud.Calls == 1 &&
                shortStatus.Error!.Contains("单个 WAV"), "oversized projected single WAV stops after the first response without replay");
            foreach (int size in new[] { 100_000, 1_000_000 })
            {
                var work = works.Create(Owner, new("测试 " + size));
                var actual = Encoding.UTF8.GetBytes(new string('中', size));
                works = await UploadAndPrepare(works, work, actual, options);
                var ready = await State(works, work.Id, "ready");
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long? offset = 0;
                long count = 0, cursor = 0;
                while (offset != null)
                {
                    var page = JsonSerializer.SerializeToElement(works.Preview(work.Id, Owner, offset.Value), Json);
                    foreach (var item in page.GetProperty("items").EnumerateArray())
                    {
                        byte[] data = Encoding.UTF8.GetBytes(item.GetProperty("text").GetString()!);
                        if (item.GetProperty("offset").GetInt64() != cursor || data.Length + Encoding.UTF8.GetByteCount("(唱歌)(温柔)") > 3000)
                            throw new Exception("Invalid preview ordering or budget.");
                        digest.AppendData(data);
                        cursor += data.Length;
                        count++;
                    }
                    var next = page.GetProperty("nextOffset");
                    offset = next.ValueKind == JsonValueKind.Null ? null : next.GetInt64();
                }
                check(ready.State == "ready" && ready.Total == size / 50 && count == ready.Total && cursor == actual.Length &&
                    Convert.ToHexString(digest.GetHashAndReset()).Equals(ready.Sha256, StringComparison.OrdinalIgnoreCase),
                    $"long work {size:N0} Chinese characters import, index and ordered paged byte-for-byte reassembly");
                works.Delete(work.Id, Owner);
            }

            string text = "\uFEFF第1章 前言\r\n" + string.Concat(Enumerable.Repeat("中文👨‍👩‍👧‍👦🇨🇳e\u0301[轻笑]【低声】(停顿)\r\n", 999))
                + "# 第二章\n" + new string('[', 6200) + "\r\nchapter III\r\n结尾";
            var unicode = works.Create(Owner, new("Unicode"));
            byte[] raw = Encoding.UTF8.GetBytes(text);
            int offsetForEmoji = Array.IndexOf(raw, (byte)0xf0) + 1;
            check(works.Upload(unicode.Id, Owner, 0, raw[..offsetForEmoji], Digest(raw[..offsetForEmoji])).Bytes == offsetForEmoji,
                "upload allows a UTF-8 sequence split across chunks");
            check(works.Upload(unicode.Id, Owner, 0, raw[..offsetForEmoji], Digest(raw[..offsetForEmoji])).Bytes == offsetForEmoji,
                "identical replay does not duplicate a chunk");
            reject(() => works.Upload(unicode.Id, Owner, 0, [(byte)'a'], Digest([(byte)'a'])), "different replay rejected");
            reject(() => works.Upload(unicode.Id, Owner, offsetForEmoji + 1, [(byte)'a'], Digest([(byte)'a'])), "upload gaps rejected");
            reject(() => works.Get(unicode.Id, "1001"), "work owner isolation");
            await UploadAndPrepare(works, unicode, raw, options, offsetForEmoji);
            var unicodeReady = await State(works, unicode.Id, "ready");
            check(unicodeReady.Chapters == 3 && unicodeReady.Sha256 == Digest(raw), "BOM, emoji, Unicode, CRLF, unmatched brackets and chapter detection retain raw TXT");
            var unicodePage = JsonSerializer.SerializeToElement(works.Preview(unicode.Id, Owner, 0), Json);
            var unicodeSegments = unicodePage.GetProperty("items").EnumerateArray().ToArray();
            check(unicodeSegments[0].GetProperty("heading").GetBoolean() && unicodeSegments.All(item =>
            {
                string value = item.GetProperty("text").GetString()!;
                return !value.StartsWith('‍') && !value.StartsWith('́') && !value.StartsWith('\n') &&
                    value.Count(c => c == '[') == value.Count(c => c == ']');
            }), "Unicode and annotations stay intact across streaming segmentation");
            works.Delete(unicode.Id, Owner);

            foreach (var (bad, token) in new[]
            {
                (new byte[] { 0xf0, 0x9f }, "UTF-8"),
                (Encoding.UTF8.GetBytes("前\r\n[" + new string('中', 4600) + "]"), "字节偏移 5"),
                (Encoding.UTF8.GetBytes("x" + new string('\u0301', 5000)), "字节偏移 0")
            })
            {
                var work = works.Create(Owner, new("Invalid"));
                await UploadAndPrepare(works, work, bad, options);
                check((await State(works, work.Id, "invalid")).Error!.Contains(token), "invalid UTF-8 or indivisible unit rejects with accurate reason");
                works.Delete(work.Id, Owner);
            }

            var transcript = works.Create(Owner, new("Synthesis"));
            await UploadAndPrepare(works, transcript, Encoding.UTF8.GetBytes("# Chapter 1\r\n" + new string('甲', 2400) +
                "\r\n# Chapter 2\r\n" + new string('乙', 2400)), options);
            check((await State(works, transcript.Id, "ready")).Total > 80, "large controlled work plans more than 80 requests");
            cloud.Before = n =>
            {
                if (n == 3) enough = false;
                return n == 5 ? "length" : "stop";
            };
            await Control(works, transcript.Id, "start");
            var paused = await State(works, transcript.Id, "paused");
            check(paused.Completed == 3 && paused.Volumes > 0 && cloud.Calls == 3, "disk reserve pauses before dispatch and keeps prior audio");
            enough = true;
            await Control(works, transcript.Id, "resume");
            var uncertain = await State(works, transcript.Id, "uncertain");
            check(uncertain.Completed == 4 && cloud.Calls == 5 && uncertain.Error!.Contains("提前结束"),
                "valid-looking WAV with length termination is not committed");
            reject(() => works.Control(transcript.Id, Owner, new("resume")), "uncertain retry requires explicit confirmation");
            var local = Path.Combine(storage.WorksRoot, transcript.Id, "work.json");
            var json = JsonNode.Parse(File.ReadAllText(local))!;
            json["view"]!["started"] = DateTimeOffset.UtcNow.AddDays(-2);
            File.WriteAllText(local, json.ToJsonString());
            var restarted = new Works(storage, mimo, jobs, lifetime) { SpaceAvailable = _ => enough };
            await restarted.StartAsync(default);
            await restarted.Recovery;
            var retained = restarted.Get(transcript.Id, Owner);
            check(retained.State == "uncertain" && retained.Completed == 4 && cloud.Calls == 5 &&
                retained.Started < DateTimeOffset.UtcNow.AddDays(-1), "restart retains multi-day task and never resends uncertainty");
            await Control(restarted, transcript.Id, "resume", true);
            var done = await State(restarted, transcript.Id, "succeeded");
            long outputBytes = 0, cursorOut = 0;
            for (int n = 0; n < done.Volumes; n++)
            {
                var volume = restarted.OpenVolume(transcript.Id, Owner, n);
                using var stream = volume.File;
                var header = new byte[44];
                stream.ReadExactly(header);
                check(BitConverter.ToUInt32(header, 4) == stream.Length - 8 && BitConverter.ToUInt32(header, 40) == stream.Length - 44,
                    $"volume {n + 1} has one consistent RIFF header");
                outputBytes += volume.Bytes;
                stream.Seek(-1, SeekOrigin.End);
                check(stream.ReadByte() >= 0, $"volume {n + 1} supports range/seek");
                cursorOut++;
            }
            check(done.Completed == done.Total && outputBytes > Wave.MaxBytes && cursorOut > 1 && cloud.Calls == done.Total + 1,
                $"ordered WAV volumes commit {outputBytes:N0} bytes without repeating confirmed requests");
            using (var tar = new MemoryStream())
            {
                await restarted.WriteChapter(transcript.Id, Owner, 1, tar, default);
                tar.Position = 0;
                using var reader = new TarReader(tar);
                int count = 0;
                while (reader.GetNextEntry() is { } entry)
                    if (entry.Name.EndsWith(".wav") && entry.Length > 44) count++;
                check(count >= 2, "chapter TAR contains its ordered standalone WAV files");
            }
            string first = Directory.EnumerateFiles(Path.Combine(storage.WorksRoot, transcript.Id, "audio"), "audio.wav", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
            using (var file = new FileStream(first, FileMode.Open, FileAccess.Write)) { file.Position = 44; file.WriteByte(255); }
            var corrupt = new Works(storage, mimo, jobs, lifetime);
            await corrupt.StartAsync(default);
            await corrupt.Recovery;
            check(corrupt.Get(transcript.Id, Owner).State == "succeeded" && cloud.Calls == done.Total + 1,
                "startup does not scrub finished historical audio");
            reject(() => corrupt.OpenVolume(transcript.Id, Owner, 0), "historical volume integrity is checked before serving");
            check(corrupt.Get(transcript.Id, Owner).State == "damaged" && cloud.Calls == done.Total + 1,
                "same-length PCM corruption is isolated on demand without cloud replay");

            var shutdown = new WorkLifetime();
            var disconnect = new WorkCloud { Block = true };
            var remote = new Mimo(new HttpClient(disconnect));
            var original = new Works(storage, remote, new Jobs(storage, remote, shutdown), shutdown);
            var interrupted = original.Create(Owner, new("Disconnect"));
            await UploadAndPrepare(original, interrupted, Encoding.UTF8.GetBytes(new string('字', 100)), options);
            await State(original, interrupted.Id, "ready");
            await Control(original, interrupted.Id, "start");
            await disconnect.Sent.Task.WaitAsync(TimeSpan.FromSeconds(15));
            shutdown.StopApplication();
            await original.StopAsync(default);
            var reopening = new Works(storage, remote, new Jobs(storage, remote, lifetime), lifetime);
            await reopening.StartAsync(default);
            await reopening.Recovery;
            check(reopening.Get(interrupted.Id, Owner).State == "uncertain" && disconnect.Calls == 1,
                "shutdown after dispatch leaves uncertain work and does not auto-retry");
            reopening.Control(interrupted.Id, Owner, new("cancel"));
            reopening.Delete(interrupted.Id, Owner);

            var staged = works.Create(Owner, new("Crash during local commit"));
            await UploadAndPrepare(works, staged, Encoding.UTF8.GetBytes("落盘恢复。"), options);
            await State(works, staged.Id, "ready");
            await Control(works, staged.Id, "start");
            await State(works, staged.Id, "succeeded");
            int callsBeforeRestart = cloud.Calls;
            string stagedRoot = Path.Combine(storage.WorksRoot, staged.Id);
            string committed = Directory.EnumerateDirectories(stagedRoot, "result", SearchOption.AllDirectories).Single();
            Directory.Move(committed, committed + ".new");
            string workRecord = Path.Combine(stagedRoot, "work.json");
            var interruptedCommit = JsonNode.Parse(File.ReadAllText(workRecord))!;
            interruptedCommit["view"]!["state"] = "running";
            interruptedCommit["view"]!["completed"] = 0;
            interruptedCommit["view"]!["volumes"] = 0;
            interruptedCommit["nextVolumeStart"] = 0;
            interruptedCommit["inFlight"] = true;
            interruptedCommit["attempt"] = 1;
            File.WriteAllText(workRecord, interruptedCommit.ToJsonString());
            var historical = new List<string>();
            for (int n = 0; n < 3; n++)
            {
                var old = works.Create(Owner, new("Historical fixture"));
                string manifest = Path.Combine(storage.WorksRoot, old.Id, "work.json");
                var previous = JsonNode.Parse(File.ReadAllText(manifest))!;
                previous["view"]!["state"] = "succeeded";
                previous["view"]!["total"] = 1;
                previous["view"]!["completed"] = 1;
                previous["view"]!["volumes"] = 1;
                File.WriteAllText(manifest, previous.ToJsonString());
                historical.Add(old.Id);
            }
            var afterCommit = new Works(storage, mimo, jobs, lifetime);
            await afterCommit.StartAsync(default);
            await afterCommit.Recovery;
            var reconciled = await State(afterCommit, staged.Id, "succeeded");
            check(reconciled.Completed == 1 && reconciled.Volumes == 1 && Directory.Exists(committed) && cloud.Calls == callsBeforeRestart,
                "restart promotes fully flushed staged WAV before advancing, with zero cloud replay");
            check(afterCommit.Get(transcript.Id, Owner).State == "damaged" &&
                historical.All(id => afterCommit.Get(id, Owner).State == "succeeded"),
                "active recovery does not open audio from historical works");
            foreach (var historicalId in historical) afterCommit.Delete(historicalId, Owner);
            File.Delete(Path.Combine(committed, "audio.json"));
            File.WriteAllText(workRecord, interruptedCommit.ToJsonString());
            var damagedPending = new Works(storage, mimo, jobs, lifetime);
            await damagedPending.StartAsync(default);
            await damagedPending.Recovery;
            check(damagedPending.Get(staged.Id, Owner).State == "damaged" && cloud.Calls == callsBeforeRestart,
                "missing receipt for a committed file isolates only that job and never resends");
            File.Delete(workRecord);
            reject(() => damagedPending.Create(Owner, new("Crash during local commit", staged.Id)),
                "an unreadable work directory cannot be recreated under a used ID");
            damagedPending.Delete(transcript.Id, Owner);

            var sample = storage.CreateSample(Owner, "参考样本", "wav", Wave.Merge([new(24000, new byte[24000])]));
            var clone = works.Create(Owner, new("Clone snapshot"));
            cloud.Before = _ => { storage.DeleteSample(sample.Id, Owner); return "stop"; };
            await UploadAndPrepare(works, clone, Encoding.UTF8.GetBytes(new string('字', 110)),
                options with { Singing = false, Voice = "mimo_default", SynthesisMode = "clone", SourceSampleId = sample.Id });
            await State(works, clone.Id, "ready");
            await Control(works, clone.Id, "start");
            var cloned = await State(works, clone.Id, "succeeded");
            check(cloned.Completed == 3 && cloud.CloneRequests == 3 && storage.GetSample(sample.Id, Owner) == null,
                "three VoiceClone requests reuse the private snapshot after source sample deletion");
            works.Delete(clone.Id, Owner);
            cloud.Before = null;

            await DamageDuringResume(storage, lifetime, options, check, reject);
            await DamageDuringDispatch(storage, lifetime, options, check, reject, uncertain: false);
            await DamageDuringDispatch(storage, lifetime, options, check, reject, uncertain: true);

            var beforeSend = works.Create(Owner, new("Local stop"));
            await UploadAndPrepare(works, beforeSend, Encoding.UTF8.GetBytes("为恢复保留。"), options);
            await State(works, beforeSend.Id, "ready");
            enough = false;
            await Control(works, beforeSend.Id, "start");
            var localPause = await State(works, beforeSend.Id, "paused");
            check(localPause.Completed == 0, "space exhaustion before first dispatch leaves a resumable work");
            works.Delete(beforeSend.Id, Owner);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string FirstCommittedWav(Storage storage, string id) =>
        Directory.EnumerateFiles(Path.Combine(storage.WorksRoot, id, "audio"), "audio.wav", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();

    private static void DamagePcm(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.Position = 44;
        stream.WriteByte(255);
    }

    private static async Task<WorkView> StartDamageFixture(Works works, ScriptRequest options)
    {
        var work = works.Create(Owner, new("Multi-volume damage fixture"));
        string text = string.Join("\n", Enumerable.Range(1, 9).Select(n => $"# Chapter {n}\nText."));
        await UploadAndPrepare(works, work, Encoding.UTF8.GetBytes(text), options);
        await State(works, work.Id, "ready");
        await Control(works, work.Id, "start");
        return work;
    }

    private static async Task DamageDuringResume(Storage storage, Lifetime lifetime, ScriptRequest options,
        Action<bool, string> check, Action<Action, string> reject)
    {
        var cloud = new WorkCloud();
        var mimo = new Mimo(new HttpClient(cloud));
        var jobs = new Jobs(storage, mimo, lifetime);
        var works = new Works(storage, mimo, jobs, lifetime);
        async Task<WorkView> PauseWithVolumes()
        {
            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int cutoff = cloud.Calls + 5;
            cloud.BeforeResponse = async (call, ct) =>
            {
                if (call != cutoff) return;
                dispatched.SetResult();
                await reply.Task.WaitAsync(ct);
            };
            var work = await StartDamageFixture(works, options);
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(15));
            works.Control(work.Id, Owner, new("pause"));
            reply.SetResult();
            return await State(works, work.Id, "paused");
        }

        var paused = await PauseWithVolumes();
        check(paused.Volumes >= 3 && paused.Completed < paused.Total, "pause fixture retains multiple sealed volumes");
        string path = FirstCommittedWav(storage, paused.Id);
        int calls = cloud.Calls;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            reject(() => works.OpenVolume(paused.Id, Owner, 0), "volume under temporary sharing conflict cannot be read");
            check(works.Get(paused.Id, Owner).State == "paused", "temporary volume I/O error does not mark the work damaged");
            await Control(works, paused.Id, "resume");
            var blocked = await State(works, paused.Id, "paused");
            check(blocked.Error!.Contains("暂时") && cloud.Calls == calls,
                "resume with transient I/O error pauses before dispatch and remains retryable");
        }
        DamagePcm(path);
        await Control(works, paused.Id, "resume");
        var damaged = await State(works, paused.Id, "damaged");
        check(damaged.Completed == paused.Completed && cloud.Calls == calls && damaged.Error!.Contains("第 1 段"),
            "manual resume checks the earliest sealed WAV and blocks billing in a paused work");
        reject(() => works.Control(paused.Id, Owner, new("resume", true)), "explicit retry cannot bypass confirmed corruption");

        var badIndex = await PauseWithVolumes();
        string volumeDir = Path.Combine(storage.WorksRoot, badIndex.Id, "volumes");
        string volume = Directory.EnumerateFiles(volumeDir, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();
        File.WriteAllText(volume, "{");
        reject(() => works.Playlist(badIndex.Id, Owner, 0), "invalid committed volume record is detected when listing");
        check(works.Get(badIndex.Id, Owner).State == "damaged", "confirmed historical metadata damage blocks a paused work");
        reject(() => works.Control(badIndex.Id, Owner, new("resume", true)), "corrupt volume metadata cannot be bypassed");

        var interrupted = await PauseWithVolumes();
        await works.StopAsync(default);
        DamagePcm(FirstCommittedWav(storage, interrupted.Id));
        string manifest = Path.Combine(storage.WorksRoot, interrupted.Id, "work.json");
        var state = JsonNode.Parse(File.ReadAllText(manifest))!;
        state["view"]!["state"] = "running";
        state["stop"] = null;
        File.WriteAllText(manifest, state.ToJsonString());
        calls = cloud.Calls;
        var reopened = new Works(storage, mimo, jobs, lifetime);
        await reopened.StartAsync(default);
        await reopened.Recovery;
        check((await State(reopened, interrupted.Id, "damaged")).Completed == interrupted.Completed && cloud.Calls == calls,
            "restart checks sealed audio for the interrupted work before any paid request");
        cloud.BeforeResponse = null;
        var healthy = await StartDamageFixture(reopened, options);
        var done = await State(reopened, healthy.Id, "succeeded");
        check(done.Completed == done.Total && cloud.Calls == calls + done.Total,
            "other healthy work can still finish after corruption is isolated");
        await reopened.StopAsync(default);
    }

    private static async Task DamageDuringDispatch(Storage storage, Lifetime lifetime, ScriptRequest options,
        Action<bool, string> check, Action<Action, string> reject, bool uncertain)
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new WorkCloud
        {
            BeforeResponse = async (call, ct) =>
            {
                if (call != 5) return;
                dispatched.SetResult();
                await reply.Task.WaitAsync(ct);
                if (uncertain) throw new HttpRequestException("controlled failure after dispatch");
            }
        };
        var mimo = new Mimo(new HttpClient(cloud));
        var jobs = new Jobs(storage, mimo, lifetime);
        var works = new Works(storage, mimo, jobs, lifetime);
        var work = await StartDamageFixture(works, options);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var active = works.Get(work.Id, Owner);
        if (uncertain) works.Control(work.Id, Owner, new("pause"));
        DamagePcm(FirstCommittedWav(storage, work.Id));
        reject(() => works.OpenVolume(work.Id, Owner, 0), "history read detects SHA mismatch during dispatch");
        var damaged = works.Get(work.Id, Owner);
        check(damaged.State == "damaged" && damaged.Completed == active.Completed,
            "history read immediately blocks a running or pausing work");
        reject(() => works.Control(work.Id, Owner, new("resume", true)), "cannot resume a damaged work with request still in flight");
        string directory = Path.Combine(storage.WorksRoot, work.Id);
        string manifest = Path.Combine(directory, "work.json");
        string beforeReply = File.ReadAllText(manifest);
        reply.SetResult();
        await works.StopAsync(default);
        var settled = works.Get(work.Id, Owner);
        var saved = JsonNode.Parse(File.ReadAllText(manifest))!;
        check(settled.State == "damaged" && cloud.Calls == 5 && saved["inFlight"]!.GetValue<bool>() == uncertain &&
            settled.Completed == active.Completed + (uncertain ? 0 : 1),
            uncertain ? "uncertain in-flight result remains recorded without another dispatch" :
            "valid in-flight result is committed without another dispatch");
        using (var intact = works.OpenSegment(work.Id, Owner, settled.Completed - 1))
            check(intact.Length > 44, "intact PCM is still readable by segment after the work is damage-blocked");
        if (uncertain)
        {
            check(settled.Error!.Contains("结果不确定"), "damage reason preserves unknown cloud outcome");
        }
        else
        {
            // Recreate the crash window after publishing audio and before
            // advancing the damage-blocked cursor.
            string latest = Directory.EnumerateDirectories(directory, "result", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Last();
            Directory.Move(latest, latest + ".new");
            File.WriteAllText(manifest, beforeReply);
        }
        var reopened = new Works(storage, mimo, jobs, lifetime);
        await reopened.StartAsync(default);
        await reopened.Recovery;
        var recovered = reopened.Get(work.Id, Owner);
        check(recovered.State == "damaged" && recovered.Completed == settled.Completed && cloud.Calls == 5,
            uncertain ? "restarting a damaged uncertain work makes no paid request" :
            "restart reconciles a staged valid response for a damaged work without restarting generation");
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<Works> UploadAndPrepare(Works works, WorkView work, byte[] bytes, ScriptRequest options, int start = 0)
    {
        for (int offset = start; offset < bytes.Length; offset += Works.ChunkBytes)
        {
            byte[] chunk = bytes[offset..Math.Min(bytes.Length, offset + Works.ChunkBytes)];
            works.Upload(work.Id, Owner, offset, chunk, Digest(chunk));
        }
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { works.Prepare(work.Id, Owner, new(options, bytes.Length, Digest(bytes))); return works; }
            catch (BusyError) { await Task.Delay(5, time.Token); }
        }
    }

    private static async Task<WorkView> State(Works works, string id, string state)
    {
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var view = works.Get(id, Owner);
            if (view.State == state) return view;
            if (view.State is "invalid" or "failed" or "damaged" or "uncertain" or "succeeded")
                throw new Exception($"Expected {state}, got {view.State}: {view.Error}");
            await Task.Delay(5, time.Token);
        }
    }

    private static async Task Control(Works works, string id, string action, bool confirm = false)
    {
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { works.Control(id, Owner, new(action, confirm)); return; }
            catch (BusyError) { await Task.Delay(5, time.Token); }
        }
    }

    private sealed class WorkCloud : HttpMessageHandler
    {
        public int Calls, CloneRequests;
        public int? ResponseBytes;
        public string? FirstClone;
        public Func<int, string>? Before;
        public Func<int, CancellationToken, Task>? BeforeResponse;
        public bool Block;
        public TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static string Response(int rate, string reason, int bytes = 800_000) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = reason, message = new { audio = new
            {
                data = Convert.ToBase64String(Wave.Merge([new(rate, new byte[bytes])]))
            } } } }
        });
        private readonly string ok = Response(24000, "stop");
        private readonly string otherRate = Response(16000, "stop");
        private readonly string limited = Response(24000, "length");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref Calls);
            using (var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
            {
                var root = sent.RootElement;
                if (root.GetProperty("model").GetString() == "mimo-v2.5-tts-voiceclone")
                {
                    string? value = root.GetProperty("audio").GetProperty("voice").GetString();
                    FirstClone ??= value;
                    if (value != FirstClone || value == null || !value.StartsWith("data:audio/wav;base64,"))
                        throw new Exception("Invalid cloned sample snapshot.");
                    CloneRequests++;
                }
            }
            Sent.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, ct);
            if (BeforeResponse != null) await BeforeResponse(call, ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(Before?.Invoke(call) == "length" ? limited : call == 33 ? otherRate : ResponseBytes is { } size ? Response(24000, "stop", size) : ok) };
        }
    }

    private sealed class WorkLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        private readonly CancellationTokenSource shutdown = new();
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => shutdown.Token;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => shutdown.Cancel();
    }
}
