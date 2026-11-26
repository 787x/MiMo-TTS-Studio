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
        long length = file.Length;
        if (uploaded > length) throw new ArgumentException();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            int limit = (int)Math.Min(buffer.Length, length - offset);
            // Align to the server cursor even if another client used different
            // chunk sizes. Verify the retained prefix by replaying it.
            if (offset < uploaded) limit = (int)Math.Min(limit, uploaded - offset);
            await file.ReadExactlyAsync(buffer.AsMemory(0, limit), ct);
            var bytes = buffer.AsSpan(0, limit);
            digest.AppendData(bytes);
            var reply = await Call("upload_work_chunk", Wire.Element(new
            {
                id, offset, data = Convert.ToBase64String(bytes),
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            }));
            if (!reply.Success) return reply;
            offset += limit;
        }
        if (file.Length != length) throw new ArgumentException();
        return AgentReply.Ok(new { id, bytes = offset, sha256 = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() });
    }
}
