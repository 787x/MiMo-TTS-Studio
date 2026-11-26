using System.Security.Cryptography;
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
            var view = Owned(id, owner).View;
            if (offset < 0 || offset > view.Volumes || chapter < 0) throw new InputError("分页位置无效。");
            if (view.State == "damaged") throw new InputError("作品存储已损坏。");
            WorkVolume Volume(long n) => Read<WorkVolume>(VolumePath(id, n)) ?? throw new InputError("分卷记录缺失。");
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

    // Previously published volumes are verified when read. Only an unfinished
    // volume can contribute data to the next commit and needs early validation.
    private void CheckResumeTail(WorkState state)
    {
        string id = state.View.Id;
        long next = state.NextVolumeStart, completed = state.View.Completed;
        if (next < 0 || completed < next || completed > state.View.Total || completed - next > 16)
            throw new InputError("未完成分卷的范围无效。");
        if (state.View.Volumes > 0 &&
            (Read<WorkVolume>(VolumePath(id, state.View.Volumes - 1)) is not { } last || last.Last != next - 1))
            throw new InputError("最后一个已提交的分卷索引不完整。");
        for (long n = next; n < completed; n++)
            if (Receipt(id, n) is not { } audio || !Verified(AudioPath(id, n), audio))
                throw new InputError($"第 {n + 1} 段的已提交音频缺失或损坏。");
    }

    private void MarkHistoricalDamage(WorkState state)
    {
        if (state.View.State is "succeeded" or "canceled")
            TrySave(state with { View = state.View with { State = "damaged", Error = "已保存的分卷或音频校验失败。" } });
    }

    public async Task WriteChapter(string id, string owner, long chapter, Stream destination, CancellationToken ct)
    {
        // TAR writes one member at a time and needs no whole-chapter buffer.
        long n, end;
        lock (gate)
        {
            var view = Owned(id, owner).View;
            if (chapter < 0 || chapter >= view.Chapters || view.State == "damaged")
                throw new InputError("章节不存在或无法读取。");
            long high = end = view.Volumes;
            n = 0;
            while (n < high)
            {
                long mid = n + (high - n) / 2;
                if ((Read<WorkVolume>(VolumePath(id, mid)) ?? throw new InputError("分卷记录缺失。")).Chapter < chapter)
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
                var view = Owned(id, owner).View;
                if (n >= view.Volumes || Read<WorkVolume>(VolumePath(id, n))?.Chapter != chapter) break;
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
            if (view.State == "damaged" || number < 0 || number >= view.Volumes) throw new InputError("分卷不存在或无法读取。");
            var files = new List<(FileStream File, long Bytes)>();
            try
            {
                var volume = Read<WorkVolume>(VolumePath(id, number)) ?? throw new InputError("分卷记录缺失。");
                if (volume.Number != number || volume.First < 0 || volume.Last < volume.First || volume.Last >= view.Completed ||
                    volume.Last - volume.First >= 16 || volume.Bytes is <= 44 or > Wave.MaxBytes)
                    throw new InputError("分卷记录无效。");
                long length = 44;
                for (long n = volume.First; n <= volume.Last; n++)
                {
                    var audio = Receipt(id, n) ?? throw new InputError("分段音频记录缺失。");
                    if (audio.Rate != volume.SampleRate) throw new InputError("分卷采样率不一致。");
                    var file = File.OpenRead(AudioPath(id, n));
                    files.Add((file, audio.Bytes - 44));
                    if (file.Length != audio.Bytes || !Wave.NormalizedHeader(file, audio.Rate, audio.Bytes))
                        throw new InputError("分段音频不完整。");
                    file.Position = 0;
                    if (!Convert.ToHexString(SHA256.HashData(file)).Equals(audio.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InputError("分段音频校验失败。");
                    length += audio.Bytes - 44;
                }
                if (length != volume.Bytes) throw new InputError("分卷长度不匹配。");
                return (new VolumeStream(files, volume), volume.Bytes);
            }
            catch (Exception e) when (e is InputError or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                foreach (var (file, _) in files) file.Dispose();
                MarkHistoricalDamage(state);
                throw new InputError("已保存的分卷或音频校验失败。");
            }
        }
    }

    public Stream OpenSegment(string id, string owner, long number)
    {
        lock (gate)
        {
            var state = Owned(id, owner);
            if (state.View.State == "damaged" || number < 0 || number >= state.View.Completed)
                throw new InputError("分段音频尚未完成或已损坏。");
            try
            {
                var receipt = Receipt(id, number) ?? throw new InputError("分段音频记录缺失。");
                var file = File.OpenRead(AudioPath(id, number));
                try
                {
                    if (!Verified(file, receipt)) throw new InputError("分段音频损坏。");
                    file.Position = 0;
                    return file;
                }
                catch { file.Dispose(); throw; }
            }
            catch (Exception e) when (e is InputError or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                MarkHistoricalDamage(state);
                throw new InputError("已保存的分段音频校验失败。");
            }
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
