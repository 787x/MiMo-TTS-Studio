using System.Security.Cryptography;
using System.Text.Json;
using Studio.Agent;

namespace MiMoStudio.Agent;

internal static class WorkUpload
{
    public static async Task<AgentReply> Send(AgentClient client, JsonElement args, string path, TimeSpan timeout, CancellationToken ct)
    {
        async Task<AgentReply> Call(string tool, JsonElement arguments)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(timeout);
            return await client.Call(tool, arguments, request.Token);
        }
        var status = await Call("upload_work_file", args);
        if (!status.Success) return status;
        if (status.Data.GetProperty("state").GetString() != "uploading") throw new ArgumentException();
        long uploaded = status.Data.GetProperty("bytes").GetInt64();
        string id = args.GetProperty("id").GetString()!;
        using var file = LinuxIdentity.OpenPrivate(path, long.MaxValue);
        var revision = LinuxIdentity.FileRevision(file);
        long length = revision.Length;
        if (uploaded > length) throw new ArgumentException();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
        async Task ReadThrough(long end)
        {
            while (file.Position < end)
            {
                int count = (int)Math.Min(buffer.Length, end - file.Position);
                await file.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                digest.AppendData(buffer.AsSpan(0, count));
            }
        }
        await ReadThrough(uploaded);
        string prefixSha256 = Hex(digest.GetCurrentHash());
        await ReadThrough(length);
        string sha256 = Hex(digest.GetHashAndReset());
        if (LinuxIdentity.FileRevision(file) != revision) throw new ArgumentException();
        var bound = await Call("bind_work_upload", Wire.Element(new { id, bytes = length, sha256, offset = uploaded, prefixSha256 }));
        if (!bound.Success) return bound;
        file.Position = uploaded;
        long offset = uploaded;
        while (offset < length)
        {
            int count = (int)Math.Min(buffer.Length, length - offset);
            await file.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
            var bytes = buffer.AsSpan(0, count);
            var reply = await Call("upload_work_chunk", Wire.Element(new
            {
                id, offset, data = Convert.ToBase64String(bytes),
                sha256 = Hex(SHA256.HashData(bytes))
            }));
            if (!reply.Success) return reply;
            offset += count;
            if (reply.Data.GetProperty("bytes").GetInt64() != offset) throw new ArgumentException();
        }
        if (LinuxIdentity.FileRevision(file) != revision) throw new ArgumentException();
        return AgentReply.Ok(new { id, bytes = length, sha256 });
    }
}
