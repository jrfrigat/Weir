using System.Buffers.Text;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>Reads this process's resident working set without enumerating processes or threads.</summary>
public sealed class ProcessMemoryReader : IProcessMemoryReader, IDisposable
{
    /// <summary>The physical/container-capacity probe, with bounded cached cgroup paths.</summary>
    private readonly MemoryCapacityReader _capacity = new();
    /// <summary>Reusable Linux procfs descriptor; positional reads never share a mutable stream position.</summary>
    private readonly SafeFileHandle? _linuxStatm;
    /// <summary>Whether the reader has been stopped.</summary>
    private int _disposed;

    /// <summary>Creates an RSS reader and opens one reusable descriptor on Linux.</summary>
    public ProcessMemoryReader()
    {
        if (OperatingSystem.IsLinux())
        {
            try { _linuxStatm = File.OpenHandle("/proc/self/statm", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <inheritdoc />
    public long? ReadWorkingSetBytes()
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        try
        {
            if (OperatingSystem.IsLinux()) return ReadLinuxWorkingSet();
            var bytes = Environment.WorkingSet;
            return bytes > 0 ? bytes : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or IOException or ObjectDisposedException) { return null; }
    }

    /// <summary>Reads the resident-page field without allocating procfs text or process snapshots.</summary>
    /// <returns>Resident pages converted to bytes, or unknown for an unavailable or malformed observation.</returns>
    private long? ReadLinuxWorkingSet()
    {
        if (_linuxStatm is null) return null;
        Span<byte> buffer = stackalloc byte[128];
        var count = RandomAccess.Read(_linuxStatm, buffer, 0);
        var text = buffer[..count];
        var separator = text.IndexOf((byte)' ');
        if (separator < 0) return null;
        text = text[(separator + 1)..];
        if (!Utf8Parser.TryParse(text, out long pages, out var consumed) || pages <= 0 ||
            consumed == text.Length || text[consumed] != (byte)' ' || pages > long.MaxValue / Environment.SystemPageSize) return null;
        return pages * Environment.SystemPageSize;
    }

    /// <inheritdoc />
    public MemoryCapacity ReadCapacity() => Volatile.Read(ref _disposed) == 0 ? _capacity.Read() : default;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _linuxStatm?.Dispose();
    }
}
