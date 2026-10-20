using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Studio.Agent;
public static class LinuxIdentity
{
    [StructLayout(LayoutKind.Sequential)] struct Credentials { public int Pid; public uint Uid, Gid; }
    [DllImport("libc", SetLastError=true)] static extern int getsockopt(int fd, int level, int option, out Credentials credentials, ref uint size);
    [DllImport("libc")] public static extern uint getuid();
    [DllImport("libc", SetLastError=true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError=true)] static extern int fstat(int fd, out Stat stat);
    [StructLayout(LayoutKind.Sequential)] struct Stat
    {
        public ulong Device, Inode, Links; public uint Mode, Uid, Gid, Padding;
        public ulong Rdev; public long Size, BlockSize, Blocks, Asec, Ansec, Msec, Mnsec, Csec, Cnsec, Reserved1, Reserved2, Reserved3;
    }
    public static uint? Peer(Socket socket)
    {
        if (!OperatingSystem.IsLinux() || socket.AddressFamily != AddressFamily.Unix) return null;
        uint n=12; return getsockopt((int)socket.Handle,1,17,out var c,ref n)==0 && n==12 ? c.Uid : null;
    }
    public static FileStream OpenPrivate(string path, long max)
    {
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException();
        // Linux x86-64, regular file, current real UID, no symlink or group/other access.
        int fd=open(path,0x20000|0x80000|0x800);
        if(fd<0)throw new UnauthorizedAccessException();
        var handle=new SafeFileHandle((IntPtr)fd,true);
        if(fstat(fd,out var s)!=0 || (s.Mode&0xF000)!=0x8000 || s.Uid!=getuid() || (s.Mode&0x3F)!=0 || s.Size<1 || s.Size>max)
        {handle.Dispose();throw new UnauthorizedAccessException();}
        return new FileStream(handle,FileAccess.Read);
    }
}
