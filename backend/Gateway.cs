using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Connections.Features;

namespace Studio;

public sealed record GatewayPeer(bool Trusted);
public static partial class Gateway
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials { public int Pid; public uint Uid; public uint Gid; }
    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockopt(int socket, int level, int name, out Credentials credentials, ref uint length);
    public static bool IsRootPeer(Socket? socket)
    {
        if (!OperatingSystem.IsLinux() || socket?.AddressFamily != AddressFamily.Unix) return false;
        uint size = 12;
        return getsockopt((int)socket.Handle, 1, 17, out var credentials, ref size) == 0 && size == 12 && credentials.Uid == 0;
    }
    public static bool Admin(HttpContext context)
    {
        var headers = context.Request.Headers;
        return context.Features.Get<GatewayPeer>()?.Trusted == true &&
            headers["X-Trim-Isadmin"].Count == 1 && headers["X-Trim-Isadmin"] == "true" &&
            headers["X-Trim-Userid"].Count == 1 && uint.TryParse(headers["X-Trim-Userid"], out _) &&
            headers["X-Trim-Username"].Count == 1 && !string.IsNullOrWhiteSpace(headers["X-Trim-Username"]);
    }
    public static bool SafeOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin;
        if (origin.Count != 1 || !Uri.TryCreate(origin, UriKind.Absolute, out var u) ||
            u.Scheme is not ("http" or "https") || u.AbsolutePath != "/" || u.Query != "" || u.Fragment != "" || u.UserInfo != "") return false;
        if (u.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)) return true;
        // fnOS nginx uses `Host $host`, dropping :5666 (verified on device).
        // Require the browser Referer to confirm the complete Origin in this case.
        // The independent unpredictable CSRF token remains mandatory for every write.
        return request.Host.Port == null && u.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
            request.Headers.Referer.Count == 1 && Uri.TryCreate(request.Headers.Referer, UriKind.Absolute, out var referer) &&
            referer.GetLeftPart(UriPartial.Authority).Equals(u.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }
}
