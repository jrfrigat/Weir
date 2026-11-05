using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>Reads the bounded background memory history without sampling on the request path.</summary>
public interface IMemoryMetrics
{
    /// <summary>Returns the latest per-instance observation history and cache metadata.</summary>
    /// <returns>A bounded snapshot with nullable unknown values.</returns>
    MemorySnapshot GetSnapshot();
}

/// <summary>Reads only this server process's resident working set.</summary>
public interface IProcessMemoryReader
{
    /// <summary>Reads resident process bytes without forcing garbage collection.</summary>
    /// <returns>Working-set bytes, or null when the operating system cannot provide them.</returns>
    long? ReadWorkingSetBytes();

    /// <summary>Reads physical memory constrained by visible container/job limits, independently of GC limits.</summary>
    /// <returns>The environment capacity, or an unknown observation for unsupported custom readers.</returns>
    MemoryCapacity ReadCapacity() => default;
}
