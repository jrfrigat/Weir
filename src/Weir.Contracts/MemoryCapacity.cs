namespace Weir.Contracts;

/// <summary>Identifies the observed physical capacity or a smaller operating-system environment limit.</summary>
public enum MemoryCapacitySource
{
    /// <summary>The capacity could not be determined.</summary>
    Unknown,
    /// <summary>Physical memory reported by the operating system.</summary>
    PhysicalMemory,
    /// <summary>A visible container or job memory limit no larger than physical memory.</summary>
    EnvironmentLimit
}

/// <summary>Memory capacity available to the environment, independent of GC heap configuration.</summary>
/// <param name="TotalBytes">Physical capacity constrained by visible environment limits, or null when unknown.</param>
/// <param name="Source">Whether physical memory or an environment limit determines the capacity.</param>
public readonly record struct MemoryCapacity(long? TotalBytes, MemoryCapacitySource Source);
