using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Studio;

public sealed record ExportInput(string DirectoryId, string FileName);
public sealed record ExportTarget(string Id, string Path, bool Writable, string? Reason);
public sealed record ExportReceipt(string FileName, string Directory, long Bytes);

// Queries the live platform authority; startup environment paths are deliberately
// not a fallback: they cannot prove that an authorization has not been revoked.
public sealed class FnOsDirectories(HttpClient client, Func<string?> token)
{
    public static FnOsDirectories Create()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/trim_open_gateway_apiscope.socket"), ct); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }};
        return new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) }, () => Environment.GetEnvironmentVariable("TRIM_API_TOKEN"));
    }
    private async Task<JsonElement> Call(string method, object data, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8)); ct = budget.Token;
        if (token() is not { Length: > 0 } credential) throw new InputError("当前 fnOS 未提供目录开放 API。NAS 导出需要 fnOS 1.2.0401 或以上；请升级系统并重启应用。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/v1/trimapp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Content = JsonContent.Create(new { reqId = Guid.NewGuid().ToString("N"), req = method, appName = "mimotts", data });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InputError("无法核实 fnOS 当前目录授权，请检查应用权限并重启应用后刷新。");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[8192]; int n;
        while ((n = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + n > 256 * 1024) throw new InputError("fnOS 目录授权响应过大，无法安全核实。");
            body.Write(buffer, 0, n);
        }
        using var json = JsonDocument.Parse(body.ToArray());
        if (json.RootElement.GetProperty("code").GetInt32() != 0) throw new InputError("fnOS 拒绝查询目录权限，请确认应用已升级并重启后刷新。");
        return json.RootElement.GetProperty("data").Clone();
    }
    public async Task<string[]> Paths(CancellationToken ct)
    {
        var data = await Call("trim.file.getSharedAccessibleFolders", new { }, ct);
        return data.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    }
    public async Task<bool> Writable(string owner, string path, CancellationToken ct)
    {
        if (!uint.TryParse(owner, out uint uid)) return false;
        var data = await Call("trim.file.checkUserACL", new { uid, path }, ct);
        return data.EnumerateArray().Any(p => p.GetProperty("path").GetString() == path && p.GetProperty("writable").GetBoolean());
    }
}

public sealed class NasExport(Storage storage, FnOsDirectories platform)
{
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly byte[] idKey = RandomNumberGenerator.GetBytes(32);
    private string Id(string path) => Convert.ToHexString(HMACSHA256.HashData(idKey, Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
    private bool Allowed(string path)
    {
        // Only exact roots returned by fnOS are selectable. No subdirectory or
        // client path API exists. Exclude application/system internals even if
        // mistakenly present in a platform response.
        if (!path.StartsWith('/') || path.Length > 4096 || path.Any(char.IsControl)) return false;
        var parts = path.Split('/');
        if (parts.Skip(1).Any(p => p is "" or "." or ".." || p.StartsWith('@'))) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(parts.ElementAtOrDefault(1) ?? "", "^vol[0-9]+$")) return false;
        if (parts.Length < 4) return false;
        return !new[] { storage.Config, storage.Data, storage.Samples, storage.Temp }.Any(p =>
            path == Path.GetFullPath(p) || path.StartsWith(Path.GetFullPath(p).TrimEnd('/') + "/", StringComparison.Ordinal) ||
            Path.GetFullPath(p).StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal));
    }
    public async Task<ExportTarget[]> Targets(string owner, CancellationToken ct)
    {
        var result = new List<ExportTarget>();
        foreach (string path in (await platform.Paths(ct)).Where(Allowed))
        {
            string? reason = null;
            if (!await platform.Writable(owner, path, ct)) reason = "当前用户无写权限或目录已失效。";
            else
            {
                try { using var dir = ExportDirectory.Open(path); using var probe = dir.Temporary(); }
                catch (InputError e) { reason = e.Message; }
            }
            result.Add(new(Id(path), path, reason == null, reason));
        }
        return result.ToArray();
    }
    public static string FileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.StartsWith('.') || name.Contains("..") ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || name.EndsWith('.') ||
            Encoding.UTF8.GetByteCount(name) > 180 || name.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new InputError("请输入单个安全文件名，不含路径、连续点、控制字符或特殊符号（最多 180 UTF-8 字节）。");
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? name[..^4] + ".wav" : name + ".wav";
    }
    private async Task RequireCurrent(string owner, string path, CancellationToken ct)
    {
        if (!(await platform.Paths(ct)).Contains(path, StringComparer.Ordinal) || !await platform.Writable(owner, path, ct) ||
            !(await platform.Paths(ct)).Contains(path, StringComparer.Ordinal))
            throw new InputError("目录授权已撤销或没有写权限，请重新授权并刷新。");
    }
    public async Task<ExportReceipt> Copy(string assetId, string owner, ExportInput input, CancellationToken ct)
    {
        string name = FileName(input.FileName);
        if (!await slot.WaitAsync(0, ct)) throw new InputError("已有 NAS 导出正在进行，请稍后重试。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2)); ct = timeout.Token;
        try
        {
            var asset = storage.Get(assetId, owner) ?? throw new InputError("音频不存在或无权访问。");
            string path = (await platform.Paths(ct)).Where(Allowed).FirstOrDefault(p => Id(p) == input.DirectoryId)
                ?? throw new InputError("目标目录未授权或已失效，请刷新目录。");
            await RequireCurrent(owner, path, ct);
            using var dir = ExportDirectory.Open(path);
            using var source = storage.Open(assetId, owner) ?? throw new InputError("音频不存在或无权访问。");
            if (asset.Bytes < 44 || asset.Bytes > Wave.MaxBytes || source.Length != asset.Bytes) throw new InputError("源音频不完整，无法导出。");
            using var target = dir.Temporary();
            var buffer = new byte[64 * 1024]; long total = 0; int n;
            while ((n = await source.ReadAsync(buffer, ct)) != 0)
            {
                total += n;
                if (total > asset.Bytes) throw new InputError("源音频长度变化，导出已取消。");
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            if (total != asset.Bytes || target.Length != total) throw new InputError("音频未完整复制，导出已取消。");
            target.Flush(true);
            await RequireCurrent(owner, path, ct);
            dir.RequireSame(path);
            ct.ThrowIfCancellationRequested();
            dir.Flush(); // Fail before publication if directory sync is unsupported.
            dir.Publish(target, name); // Commit point; cancellation after this point cannot undo publication.
            try { dir.Flush(); }
            catch (InputError) { throw new InputError("WAV 已完整提交，但目录同步失败。请先在目标文件夹核实文件，避免重复导出。"); }
            return new(name, path, total);
        }
        catch (OperationCanceledException) { throw new InputError("导出已中止或超时；未提交的临时音频已释放，请刷新目录后重试。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or KeyNotFoundException)
        { throw new InputError("NAS 导出失败：请检查目录授权、可写状态、可用空间与 fnOS 开放 API，然后刷新重试。"); }
        finally { slot.Release(); }
    }
}

// Linux x86-64 only, matching the FPK platform. No path-based output writes.
// O_TMPFILE is anonymous and automatically reclaimed on crash/kill; unsupported
// filesystems fail closed. linkat publishes only complete data, without replace.
internal sealed class ExportDirectory(SafeFileHandle handle) : IDisposable
{
    private readonly SafeFileHandle handle = handle;
    private const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseExec = 0x80000;
    private const int TmpFile = 0x410000, ReadWrite = 2;
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Stat { [FieldOffset(0)] public ulong Dev; [FieldOffset(8)] public ulong Inode; }
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int openat(SafeFileHandle dir, string name, int flags, uint mode = 0);
    [DllImport("libc", SetLastError = true)] private static extern int fstat(SafeFileHandle fd, out Stat stat);
    [DllImport("libc", SetLastError = true)] private static extern int linkat(int fromDir, string from, SafeFileHandle toDir, string to, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(SafeFileHandle fd);
    private static SafeFileHandle Checked(int fd)
    {
        if (fd < 0) Fail();
        return new SafeFileHandle((IntPtr)fd, true);
    }
    private static void Fail()
    {
        int code = Marshal.GetLastPInvokeError();
        throw new InputError(code switch {
            17 => "目标已有同名文件，请修改文件名；已有文件未覆盖。",
            28 or 122 => "目标存储空间或配额不足，请释放空间后重试。",
            13 or 1 or 30 => "目标目录只读或没有写权限，请在 fnOS 应用设置中授权读写。",
            2 or 20 or 40 => "目标不存在、已被替换或包含软链接，请重新授权真实目录。",
            95 or 22 or 38 or 18 => "目标文件系统不支持安全无覆盖提交，请选择受支持的本地 NAS 文件系统。",
            _ => "目标目录 I/O 失败，未能安全完成导出。"
        });
    }
    public static ExportDirectory Open(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InputError("NAS 导出仅支持 Linux x86-64。");
        const int PathOnly = 0x200000;
        var current = Checked(open("/", PathOnly | DirectoryFlag | NoFollow | CloseExec));
        try
        {
            var parts = path.Split('/').Skip(1).ToArray();
            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index];
                if (part is "" or "." or "..") throw new InputError("授权目录格式无效。");
                // fnOS grants traverse on ancestors, not directory listing.
                // O_PATH avoids demanding read permission outside the granted root.
                var next = Checked(openat(current, part, DirectoryFlag | NoFollow | CloseExec | (index == parts.Length - 1 ? 0 : PathOnly)));
                current.Dispose(); current = next;
            }
            return new(current);
        }
        catch { current.Dispose(); throw; }
    }
    public FileStream Temporary() => new(Checked(openat(handle, ".", TmpFile | ReadWrite | CloseExec, 0x1A4)), FileAccess.ReadWrite);
    private static bool Same(SafeFileHandle a, SafeFileHandle b)
    {
        if (fstat(a, out var first) != 0) Fail();
        if (fstat(b, out var second) != 0) Fail();
        return first.Dev == second.Dev && first.Inode == second.Inode;
    }
    public void RequireSame(string path)
    {
        using var reopened = Open(path);
        if (!Same(handle, reopened.handle)) throw new InputError("授权目录在导出期间被替换，请刷新后重试。");
    }
    public void Publish(FileStream file, string name)
    {
        if (linkat(-100, "/proc/self/fd/" + file.SafeFileHandle.DangerousGetHandle().ToInt64(), handle, name, 0x400) != 0) Fail();
    }
    public void Flush() { if (fsync(handle) != 0) Fail(); }
    public void Dispose() => handle.Dispose();
}
