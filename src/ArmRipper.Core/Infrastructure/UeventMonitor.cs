using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

/// <summary>
/// A parsed kernel uevent for a block device.
/// </summary>
internal sealed record UeventMessage
{
    /// <summary>e.g. "change", "add", "remove"</summary>
    public required string Action { get; init; }

    /// <summary>e.g. "/devices/pci.../block/sr0"</summary>
    public required string DevPath { get; init; }

    /// <summary>e.g. "sr0" — extracted from DEVNAME or DEVPATH.</summary>
    public string? DevName { get; init; }

    /// <summary>e.g. "block"</summary>
    public string? Subsystem { get; init; }

    /// <summary>True when DISK_MEDIA_CHANGE=1 is present.</summary>
    public bool IsMediaChange { get; init; }

    /// <summary>
    /// All key-value pairs parsed from the uevent buffer.
    /// Useful for diagnostic logging.
    /// </summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}

/// <summary>
/// Listens for kernel uevents via <c>AF_NETLINK</c> / <c>NETLINK_KOBJECT_UEVENT</c>
/// using raw P/Invoke (the .NET <c>Socket</c> class does not support this address
/// family). Only supported on Linux. On unsupported platforms <see cref="TryStart"/>
/// returns <c>false</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class UeventMonitor : IDisposable
{
    // ── Linux constants ──────────────────────────────────────
    private const int AF_NETLINK = 16;
    private const int SOCK_RAW = 3;
    private const int NETLINK_KOBJECT_UEVENT = 15;
    private const int BUFFER_SIZE = 8192;
    private const int POLL_TIMEOUT_MS = 1000; // poll() interval for cancellation responsiveness

    // ── libc P/Invoke ────────────────────────────────────────
    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int bind(int sockfd, byte[] addr, int addrlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, nint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int recv(int sockfd, byte[] buf, int len, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;  // POLLIN = 1
        public short revents;
    }

    // ── State ────────────────────────────────────────────────
    private readonly ILogger _logger;
    private int _fd = -1;
    private bool _disposed;

    public UeventMonitor(ILogger logger)
    {
        _logger = logger;
    }

    // ── Lifecycle ────────────────────────────────────────────

    /// <summary>
    /// Attempt to create and bind the netlink socket. Returns <c>true</c> on success.
    /// Call once before <see cref="ListenAsync"/>.
    /// </summary>
    public bool TryStart()
    {
        // Create AF_NETLINK socket
        _fd = socket(AF_NETLINK, SOCK_RAW, NETLINK_KOBJECT_UEVENT);
        if (_fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            _logger.LogWarning(
                "UeventMonitor: socket(AF_NETLINK, RAW, KOBJECT_UEVENT) failed: errno={Errno} ({Message}). " +
                "Falling back to polling. This is expected in restricted containers or on non-Linux.",
                err, Marshal.GetLastPInvokeErrorMessage());
            return false;
        }

        // sockaddr_nl: family(2) + pad(2) + pid(4) + groups(4) = 12 bytes
        var addr = new byte[12];
        addr[0] = (byte)(AF_NETLINK & 0xFF);
        addr[1] = (byte)((AF_NETLINK >> 8) & 0xFF);
        // nl_pad (bytes 2-3) already zero
        // nl_pid (bytes 4-7) = 0 (any) already zero
        addr[8] = 0xFF; addr[9] = 0xFF; addr[10] = 0xFF; addr[11] = 0xFF; // all groups

        if (bind(_fd, addr, addr.Length) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            _logger.LogWarning(
                "UeventMonitor: bind() failed: errno={Errno} ({Message}). Falling back to polling.",
                err, Marshal.GetLastPInvokeErrorMessage());
            close(_fd);
            _fd = -1;
            return false;
        }

        _logger.LogInformation("UeventMonitor: netlink socket bound successfully (fd={Fd})", _fd);
        return true;
    }

    /// <summary>
    /// Yields parsed uevent messages as they arrive from the kernel.
    /// Must call <see cref="TryStart"/> first and check it returned <c>true</c>.
    /// </summary>
    public async IAsyncEnumerable<UeventMessage> ListenAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_fd < 0)
            throw new InvalidOperationException("Call TryStart() before ListenAsync()");

        var buffer = new byte[BUFFER_SIZE];
        var pfd = new PollFd[1];
        pfd[0].fd = _fd;
        pfd[0].events = 1; // POLLIN

        while (!ct.IsCancellationRequested)
        {
            // poll() with timeout — lets us respond to cancellation promptly
            var ret = poll(pfd, pfd.Length, POLL_TIMEOUT_MS);

            if (ct.IsCancellationRequested)
                break;

            if (ret < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                _logger.LogWarning("UeventMonitor: poll() error errno={Errno}, stopping", err);
                break;
            }

            if (ret == 0)
                continue; // timeout, loop back and check cancellation

            if ((pfd[0].revents & 1) == 0) // POLLIN
                continue;

            // Data available
            var received = recv(_fd, buffer, buffer.Length, 0);
            if (received <= 0)
                continue;

            var msg = ParseBuffer(buffer, received);
            if (msg is not null)
                yield return msg;
        }
    }

    // ── Disposal ─────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_fd >= 0)
            {
                close(_fd);
                _fd = -1;
            }
        }
    }

    // ── Parsing ──────────────────────────────────────────────

    /// <summary>
    /// Parse a raw netlink uevent buffer.
    /// Format: <c>action@/dev/path\0KEY=VALUE\0...\0</c>
    /// (the first token can also be absent — we just scan for key=value pairs).
    /// </summary>
    private static UeventMessage? ParseBuffer(byte[] buffer, int length)
    {
        var action = "";
        var devPath = "";
        string? devName = null;
        string? subsystem = null;
        var isMediaChange = false;
        var properties = new Dictionary<string, string>();

        // Split on null bytes
        var start = 0;
        for (var i = 0; i <= length; i++)
        {
            if (i < length && buffer[i] != 0)
                continue;

            if (i == start) // double null = end of message
                break;

            // Extract the segment
            var segLen = i - start;
            if (segLen <= 1) { start = i + 1; continue; }

            var seg = System.Text.Encoding.ASCII.GetString(buffer, start, segLen);

            // First non-empty segment is sometimes "action@/devpath"
            if (action == "")
            {
                var atIdx = seg.IndexOf('@');
                if (atIdx > 0 && seg.Contains('/'))
                {
                    action = seg[..atIdx];
                    devPath = seg[(atIdx + 1)..];
                    start = i + 1;
                    continue;
                }
            }

            // Parse key=value pairs
            var eqIdx = seg.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = seg[..eqIdx];
                var value = seg[(eqIdx + 1)..];

                properties[key] = value;

                switch (key)
                {
                    case "ACTION" when action == "":
                        action = value;
                        break;
                    case "DEVPATH" when devPath == "":
                        devPath = value;
                        break;
                    case "DEVNAME":
                        devName = Path.GetFileName(value.AsSpan()).ToString();
                        break;
                    case "SUBSYSTEM":
                        subsystem = value;
                        break;
                    case "DISK_MEDIA_CHANGE" when value == "1":
                        isMediaChange = true;
                        break;
                }
            }

            start = i + 1;
        }

        // We only care about block subsystem events
        if (subsystem != "block")
            return null;

        // Extract devName from DEVPATH if DEVNAME wasn't present
        devName ??= ExtractSrName(devPath);

        return new UeventMessage
        {
            Action = action,
            DevPath = devPath,
            DevName = devName,
            Subsystem = subsystem,
            IsMediaChange = isMediaChange,
            Properties = properties,
        };
    }

    /// <summary>Extract "srN" from a DEVPATH like ".../block/sr0".</summary>
    private static string? ExtractSrName(string devPath)
    {
        var span = devPath.AsSpan();
        var lastSlash = span.LastIndexOf('/');
        if (lastSlash < 0) return null;
        var name = span[(lastSlash + 1)..];
        return name.StartsWith("sr") ? name.ToString() : null;
    }

}
