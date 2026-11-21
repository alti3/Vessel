using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Vessel.Infrastructure.Processes.Pty;

/// <summary>
///     Minimal Linux pseudo-terminal primitives (libc): open a PTY master, unlock its slave, read/write the master, and
///     set the window size (<c>TIOCSWINSZ</c>, which makes the kernel deliver <c>SIGWINCH</c> to the foreground process
///     group). No process is forked here: children are started through <c>IProcessRunner</c> and open the slave by path.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class NativePty
{
    private const int ORdwr = 0x2;
    private const int ONoctty = 0x100;
    private const int OCloexec = 0x80000;
    private const nuint Tiocswinsz = 0x5414;
    private const int Eintr = 4;
    private const short PollIn = 0x1;

    public static bool IsSupported =>
        OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>Opens a new PTY pair and returns the master descriptor and the slave device path.</summary>
    public static (int Master, string SlavePath) Open(int columns, int rows)
    {
        var master = posix_openpt(ORdwr | ONoctty | OCloexec);
        if (master < 0) throw Error("posix_openpt");
        try
        {
            if (grantpt(master) != 0) throw Error("grantpt");
            if (unlockpt(master) != 0) throw Error("unlockpt");
            var buffer = new byte[128];
            if (ptsname_r(master, buffer, (nuint)buffer.Length) != 0) throw Error("ptsname_r");
            var length = Array.IndexOf(buffer, (byte)0);
            var path = Encoding.ASCII.GetString(buffer, 0, length < 0 ? buffer.Length : length);
            if (!path.StartsWith("/dev/pts/", StringComparison.Ordinal))
                throw new InvalidOperationException("The pseudo-terminal slave path is unexpected.");
            Resize(master, columns, rows);
            return (master, path);
        }
        catch
        {
            _ = close(master);
            throw;
        }
    }

    /// <summary>
    ///     Opens the slave side in this process (no controlling terminal). Holding it keeps the master readable until the
    ///     session is closed, so the end of a session is decided by the child process exiting, not by a hang-up race.
    /// </summary>
    public static int OpenSlave(string slavePath)
    {
        var fd = open(slavePath, ORdwr | ONoctty | OCloexec);
        if (fd < 0) throw Error("open(slave)");
        return fd;
    }

    public static void Resize(int master, int columns, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Columns = (ushort)columns };
        if (ioctl(master, Tiocswinsz, ref size) != 0) throw Error("ioctl(TIOCSWINSZ)");
    }

    /// <summary>
    ///     Waits up to <paramref name="timeoutMilliseconds" /> for output, then reads. Returns -1 on timeout (nothing to
    ///     read yet), 0 when the slave side is closed (EIO/hang-up) and the number of bytes read otherwise.
    /// </summary>
    public static int Read(int master, byte[] buffer, int timeoutMilliseconds)
    {
        while (true)
        {
            var descriptor = new PollFd { Fd = master, Events = PollIn };
            var ready = poll(ref descriptor, 1, timeoutMilliseconds);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == Eintr) continue;
                return 0;
            }

            if (ready == 0) return -1;
            var read = (int)read_fd(master, buffer, buffer.Length);
            if (read > 0) return read;
            if (read < 0 && Marshal.GetLastPInvokeError() == Eintr) continue;
            return 0;
        }
    }

    public static void Write(int master, byte[] data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var chunk = offset == 0 ? data : data[offset..];
            var written = (int)write_fd(master, chunk, chunk.Length);
            if (written < 0)
            {
                if (Marshal.GetLastPInvokeError() == Eintr) continue;
                throw Error("write");
            }

            offset += written;
        }
    }

    public static void Close(int fd)
    {
        if (fd >= 0) _ = close(fd);
    }

    private static InvalidOperationException Error(string call)
    {
        return new InvalidOperationException($"{call} failed with errno {Marshal.GetLastPInvokeError()}.");
    }

#pragma warning disable SYSLIB1054, CA5392, CA1401
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int fd, [Out] byte[] buffer, nuint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref WinSize size);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint read_fd(int fd, [Out] byte[] buffer, nint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint write_fd(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd descriptors, nuint count, int timeout);
#pragma warning restore SYSLIB1054, CA5392, CA1401

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }
}
