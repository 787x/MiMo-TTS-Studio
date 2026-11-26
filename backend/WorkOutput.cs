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

    private void CheckVolumes(WorkState state)
    {
        string id = state.View.Id;
        long next = 0;
        long previousChapter = 0;
        for (long n = 0; n < state.View.Volumes; n++)
        {
            stopping.ThrowIfCancellationRequested();
            var volume = Read<WorkVolume>(VolumePath(id, n));
            if (volume == null || volume.Number != n || volume.First != next || volume.Last < volume.First ||
                volume.Last >= state.View.Completed || volume.Last - volume.First >= 16 || volume.Chapter < previousChapter ||
                volume.Bytes != VolumeBytes(id, volume.First, volume.Last))
                throw new InputError($"第 {n + 1} 卷索引不完整。");
            previousChapter = volume.Chapter;
            next = volume.Last + 1;
        }
        if (next != state.NextVolumeStart || next > state.View.Completed) throw new InputError("分卷范围不完整。");
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
            var view = Owned(id, owner).View;
            if (view.State == "damaged" || number < 0 || number >= view.Volumes) throw new InputError("分卷不存在或无法读取。");
            var volume = Read<WorkVolume>(VolumePath(id, number)) ?? throw new InputError("分卷记录缺失。");
            if (volume.First < 0 || volume.Last < volume.First || volume.Last >= view.Completed || volume.Last - volume.First >= 16 ||
                volume.Bytes is <= 44 or > Wave.MaxBytes)
                throw new InputError("分卷记录无效。");
            var files = new List<(FileStream File, long Bytes)>();
            try
            {
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
            catch
            {
                foreach (var (file, _) in files) file.Dispose();
                throw;
            }
        }
    }

    public Stream OpenSegment(string id, string owner, long number)
    {
        lock (gate)
        {
            var view = Owned(id, owner).View;
            if (number < 0 || number >= view.Completed) throw new InputError("分段音频尚未完成。");
            var receipt = Receipt(id, number) ?? throw new InputError("分段音频记录缺失。");
            if (!Verified(AudioPath(id, number), receipt)) throw new InputError("分段音频损坏。");
            return File.OpenRead(AudioPath(id, number));
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
