using System.Formats.Tar;

namespace Studio;

public sealed partial class Works
{
    public object Chapters(string id, string owner, long offset)
    {
        lock (gate)
        {
            var view = Owned(id, owner).View;
            if (offset < 0 || offset > view.Chapters) throw new InputError("分页位置无效。");
            if (view.Chapters == 0) return new { items = Array.Empty<object>(), nextOffset = (long?)null };
            using var index = new BinaryReader(File.OpenRead(PathFor(id, "segments.idx")));
            using var source = File.OpenRead(PathFor(id, "source.txt"));
            var items = new List<object>();
            long chapter = offset;
            for (; chapter < view.Chapters && chapter - offset < PageSize; chapter++)
            {
                long low = 0, high = view.Total;
                while (low < high)
                {
                    long mid = low + (high - low) / 2;
                    if (ReadSegment(mid, index, null).Chapter < chapter) low = mid + 1;
                    else high = mid;
                }
                var segment = ReadSegment(low, index, source);
                string title = segment.Heading ? segment.Text.Split(['\r', '\n'], 2)[0] : "正文";
                items.Add(new { chapter, first = low, title });
            }
            return new { items = items.ToArray(), nextOffset = chapter < view.Chapters ? (long?)chapter : null };
        }
    }

    public object Playlist(string id, string owner, long offset, long? chapter = null)
    {
        lock (gate)
        {
            var state = Owned(id, owner);
            var view = state.View;
            if (offset < 0 || offset > view.Volumes || chapter < 0) throw new InputError("分页位置无效。");
            if (view.State == "damaged") throw new InputError("作品存储已损坏。");
            WorkVolume Volume(long n) => VolumeForRead(state, n);
            if (chapter != null)
            {
                long high = view.Volumes;
                while (offset < high)
                {
                    long mid = offset + (high - offset) / 2;
                    if (Volume(mid).Chapter < chapter) offset = mid + 1;
                    else high = mid;
                }
            }
            var items = new List<WorkVolume>();
            long next = offset;
            for (; next < view.Volumes && next - offset < PageSize; next++)
            {
                var volume = Volume(next);
                if (chapter != null && volume.Chapter != chapter) break;
                items.Add(volume);
            }
            return new { items, nextOffset = next < view.Volumes && (chapter == null || Volume(next).Chapter == chapter) ? (long?)next : null };
        }
    }

    private sealed class AudioDamage(string message) : InputError(message);

    private static bool IsAudioDamage(Exception error) =>
        error is AudioDamage or System.Text.Json.JsonException or FileNotFoundException or DirectoryNotFoundException;

    private static string AudioDamageMessage(Exception error) => error is AudioDamage ? error.Message :
        "作品恢复所需的已提交文件缺失或无效。";

    private static bool IsStorageReadError(Exception error) => error is IOException or UnauthorizedAccessException;

    // Use the same validator for playback and the pre-dispatch scan. Missing
    // committed data and invalid JSON are evidence of damage; other I/O
    // failures can be retried after the storage is available again.
    private T ReadCommitted<T>(string path, string message) where T : class
    {
        try
        {
            using var file = File.OpenRead(path);
            return System.Text.Json.JsonSerializer.Deserialize<T>(file, Json) ?? throw new AudioDamage(message);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or System.Text.Json.JsonException)
        {
            throw new AudioDamage(message);
        }
    }

    private WorkVolume CommittedVolume(WorkState state, long n)
    {
        string message = $"第 {n + 1} 卷的记录缺失或无效。";
        var volume = ReadCommitted<WorkVolume>(VolumePath(state.View.Id, n), message);
        if (volume.Number != n || volume.Chapter < 0 || volume.Chapter >= state.View.Chapters ||
            volume.First < 0 || volume.Last < volume.First || volume.Last >= state.View.Completed ||
            volume.Last - volume.First >= 16 || volume.Bytes is <= 44 or > Wave.MaxBytes ||
            volume.SampleRate is < 8000 or > 96000)
            throw new AudioDamage(message);
        return volume;
    }

    private WorkVolume VolumeForRead(WorkState state, long n)
    {
        try { return CommittedVolume(state, n); }
        catch (AudioDamage error) { MarkDamage(state, error.Message); throw; }
        catch (Exception error) when (IsStorageReadError(error))
        { throw new InputError("暂时无法读取分卷记录，请检查存储后重试。"); }
    }

    private SegmentAudio CommittedAudio(string id, long n) =>
        ReadCommitted<SegmentAudio>(Path.Combine(SegmentFolder(id, n), "result", "audio.json"),
            $"第 {n + 1} 段的已提交音频回执缺失或无效。");

    private FileStream OpenVerifiedAudio(string id, long n, SegmentAudio audio)
    {
        FileStream file;
        try { file = File.OpenRead(AudioPath(id, n)); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new AudioDamage($"第 {n + 1} 段的已提交音频缺失。"); }
        try
        {
            if (!Verified(file, audio)) throw new AudioDamage($"第 {n + 1} 段的已提交音频校验失败。");
            file.Position = 0;
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private void VerifyCommittedAudio(WorkState state, CancellationToken ct)
    {
        string id = state.View.Id;
        long next = 0;
        long previousChapter = 0;
        for (long n = 0; n < state.View.Volumes; n++)
        {
            ct.ThrowIfCancellationRequested();
            var volume = CommittedVolume(state, n);
            if (volume.First != next || volume.Chapter < previousChapter)
                throw new AudioDamage($"第 {n + 1} 卷的范围或章节顺序无效。");
            long length = 44;
            for (; next <= volume.Last; next++)
            {
                ct.ThrowIfCancellationRequested();
                var audio = CommittedAudio(id, next);
                if (audio.Rate != volume.SampleRate) throw new AudioDamage($"第 {n + 1} 卷的采样率不一致。");
                using var file = OpenVerifiedAudio(id, next, audio);
                length += file.Length - 44;
            }
            if (length != volume.Bytes) throw new AudioDamage($"第 {n + 1} 卷的字节数不匹配。");
            previousChapter = volume.Chapter;
        }
        if (next != state.NextVolumeStart) throw new AudioDamage("已提交分卷的覆盖范围无效。");
        VerifyUnsealedAudio(state, ct);
    }

    private void VerifyUnsealedAudio(WorkState state, CancellationToken ct)
    {
        string id = state.View.Id;
        long next = state.NextVolumeStart, completed = state.View.Completed;
        if (next < 0 || next > completed || completed > state.View.Total || completed - next > 16)
            throw new AudioDamage("已提交音频的覆盖范围无效。");
        for (; next < completed; next++)
        {
            ct.ThrowIfCancellationRequested();
            using var file = OpenVerifiedAudio(id, next, CommittedAudio(id, next));
        }
    }

    // All callers hold gate and pass the current state. Leave any in-flight
    // request alone so its response can still be published or recorded.
    private void MarkDamage(WorkState state, string message)
    {
        if (state.View.State == "damaged") return;
        TrySave(state with { Stop = "damaged", View = state.View with { State = "damaged", Error =
            state.InFlight && (state.View.State == "uncertain" || workerId != state.View.Id) ? message + UncertainDamage : message } });
        if (audioChecks.TryGetValue(state.View.Id, out var check)) check.Cancel.Cancel();
    }

    public async Task WriteChapter(string id, string owner, long chapter, Stream destination, CancellationToken ct)
    {
        // TAR writes one member at a time and needs no whole-chapter buffer.
        long n, end;
        lock (gate)
        {
            var state = Owned(id, owner);
            var view = state.View;
            if (chapter < 0 || chapter >= view.Chapters || view.State == "damaged")
                throw new InputError("章节不存在或无法读取。");
            long high = end = view.Volumes;
            n = 0;
            while (n < high)
            {
                long mid = n + (high - n) / 2;
                if (VolumeForRead(state, mid).Chapter < chapter)
                    n = mid + 1;
                else high = mid;
            }
        }
        await using var archive = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        for (; n < end; n++)
        {
            Stream source;
            lock (gate)
            {
                var state = Owned(id, owner);
                if (n >= state.View.Volumes || VolumeForRead(state, n).Chapter != chapter) break;
                source = OpenVolume(id, owner, n).File;
            }
            await using (source)
            {
                await archive.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"mimo-{n + 1:D6}.wav") { DataStream = source }, ct);
            }
        }
    }

    public (Stream File, long Bytes) OpenVolume(string id, string owner, long number)
    {
        lock (gate)
        {
            var state = Owned(id, owner);
            var view = state.View;
            if (number < 0 || number >= view.Volumes) throw new InputError("分卷不存在或无法读取。");
            var files = new List<(FileStream File, long Bytes)>();
            try
            {
                var volume = CommittedVolume(state, number);
                long length = 44;
                for (long n = volume.First; n <= volume.Last; n++)
                {
                    var audio = CommittedAudio(id, n);
                    if (audio.Rate != volume.SampleRate) throw new AudioDamage($"第 {number + 1} 卷的采样率不一致。");
                    var file = OpenVerifiedAudio(id, n, audio);
                    files.Add((file, file.Length - 44));
                    length += file.Length - 44;
                }
                if (length != volume.Bytes) throw new AudioDamage($"第 {number + 1} 卷的字节数不匹配。");
                return (new VolumeStream(files, volume), volume.Bytes);
            }
            catch (Exception error)
            {
                foreach (var (file, _) in files) file.Dispose();
                if (error is AudioDamage damage) MarkDamage(state, damage.Message);
                if (IsStorageReadError(error)) throw new InputError("暂时无法读取分卷，请检查存储后重试。");
                throw;
            }
        }
    }

    public Stream OpenSegment(string id, string owner, long number)
    {
        lock (gate)
        {
            var state = Owned(id, owner);
            if (number < 0 || number >= state.View.Completed) throw new InputError("分段音频尚未完成。");
            try { return OpenVerifiedAudio(id, number, CommittedAudio(id, number)); }
            catch (AudioDamage error) { MarkDamage(state, error.Message); throw; }
            catch (Exception error) when (IsStorageReadError(error))
            { throw new InputError("暂时无法读取分段音频，请检查存储后重试。"); }
        }
    }

    // Serve a normal, seekable WAV over the immutable PCM ranges. Range requests
    // can jump directly to the appropriate file without an aggregate temp file.
    private sealed class VolumeStream(List<(FileStream File, long Bytes)> files, WorkVolume volume) : Stream
    {
        private readonly byte[] header = Wave.Header(volume.SampleRate, volume.Bytes - 44);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => volume.Bytes;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination)
        {
            if (Position < 0) throw new IOException("Invalid stream position.");
            if (destination.IsEmpty || Position >= Length) return 0;
            if (Position < header.Length)
            {
                int count = Math.Min(destination.Length, header.Length - (int)Position);
                header.AsSpan((int)Position, count).CopyTo(destination);
                Position += count;
                return count;
            }
            long offset = Position - header.Length;
            foreach (var (file, bytes) in files)
            {
                if (offset >= bytes) { offset -= bytes; continue; }
                file.Position = 44 + offset;
                int count = file.Read(destination[..(int)Math.Min(destination.Length, bytes - offset)]);
                if (count == 0) throw new EndOfStreamException();
                Position += count;
                return count;
            }
            throw new EndOfStreamException();
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = checked(offset + (origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => Position,
                SeekOrigin.End => Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            }));
            if (position < 0) throw new IOException("Invalid stream position.");
            return Position = position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var (file, _) in files) file.Dispose();
            base.Dispose(disposing);
        }
    }
}
