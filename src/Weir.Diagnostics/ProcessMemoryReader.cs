using System.ComponentModel;
using System.Diagnostics;
using Weir.Abstractions;

namespace Weir.Diagnostics;

/// <summary>Reuses one process handle and reads the operating system's resident working set.</summary>
public sealed class ProcessMemoryReader : IProcessMemoryReader, IDisposable
{
    /// <summary>The current process, retained only for the reader's lifetime.</summary>
    private readonly Process _process;
    /// <summary>Whether the process handle has been released.</summary>
    private int _disposed;

    /// <summary>Creates a reader for the current server process.</summary>
    public ProcessMemoryReader() => _process = Process.GetCurrentProcess();

    /// <inheritdoc />
    public long? ReadWorkingSetBytes()
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        try { _process.Refresh(); return _process.WorkingSet64; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    /// <inheritdoc />
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _process.Dispose(); }
}
