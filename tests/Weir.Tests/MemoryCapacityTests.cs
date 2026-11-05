using Weir.Contracts;
using Weir.Diagnostics;
using Xunit;

namespace Weir.Tests;

/// <summary>Verifies physical capacity, Linux hierarchy limits and unknown observations.</summary>
public sealed class MemoryCapacityTests
{
    /// <summary>Visible ancestor limits win over the leaf and physical RAM.</summary>
    [Fact]
    public void LinuxV2_Uses_Smallest_Visible_Ancestor_And_Rereads_Limits()
    {
        var files = Files("0::/parent/leaf", "1 0 0:1 / /sys/fs/cgroup rw - cgroup2 cgroup rw");
        files["/sys/fs/cgroup/parent/leaf/memory.max"] = "1073741824";
        files["/sys/fs/cgroup/parent/memory.max"] = "536870912";
        files["/sys/fs/cgroup/memory.max"] = "max";
        Assert.Equal(new MemoryCapacity(536870912, MemoryCapacitySource.EnvironmentLimit), Read(files));
        files["/sys/fs/cgroup/parent/memory.max"] = "max";
        Assert.Equal(1073741824, Read(files).TotalBytes);
    }

    /// <summary>A mounted namespace root is removed from the membership before joining the mount point.</summary>
    [Theory]
    [InlineData("/docker/id")]
    [InlineData("/")]
    public void LinuxV1_Resolves_Docker_MountRoot_And_Ignores_Unrelated_Controllers(string membership)
    {
        var files = Files("2:cpu:/elsewhere\n5:memory:" + membership,
            "1 0 0:1 / /sys/fs/cgroup/cpu rw - cgroup cgroup rw,cpu\n2 0 0:2 /docker/id /sys/fs/cgroup/memory rw - cgroup cgroup rw,memory");
        files["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = "268435456";
        Assert.Equal(new MemoryCapacity(268435456, MemoryCapacitySource.EnvironmentLimit), Read(files));
    }

    /// <summary>Unlimited controller values preserve physical capacity rather than an enormous sentinel.</summary>
    [Theory]
    [InlineData("max")]
    [InlineData("9223372036854771712")]
    [InlineData("18446744073709551615")]
    public void Unlimited_Or_Larger_Limit_Uses_Physical_Ram(string value)
    {
        var files = Files("0::/", "1 0 0:1 / /sys/fs/cgroup rw - cgroup2 cgroup rw");
        files["/sys/fs/cgroup/memory.max"] = value;
        Assert.Equal(new MemoryCapacity(8589934592, MemoryCapacitySource.PhysicalMemory), Read(files));
    }

    /// <summary>A missing v2 root limit is normal, while a denied read must not expose host capacity.</summary>
    [Fact]
    public void Missing_Root_Is_Unlimited_But_Unreadable_Limit_Is_Unknown()
    {
        var files = Files("0::/", "1 0 0:1 / /sys/fs/cgroup rw - cgroup2 cgroup rw");
        Assert.Equal(MemoryCapacitySource.PhysicalMemory, Read(files).Source);
        var unknown = MemoryCapacityReader.ReadLinux(path => path.EndsWith("memory.max", StringComparison.Ordinal)
            ? default : Observation(files, path));
        Assert.Null(unknown.TotalBytes);
        Assert.Equal(MemoryCapacitySource.Unknown, unknown.Source);
    }

    /// <summary>Malformed limits and unresolved membership produce gaps rather than an invented capacity.</summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    public void Malformed_Limit_Is_Unknown(string value)
    {
        var files = Files("0::/", "1 0 0:1 / /sys/fs/cgroup rw - cgroup2 cgroup rw");
        files["/sys/fs/cgroup/memory.max"] = value;
        Assert.Null(Read(files).TotalBytes);
        files["/proc/self/cgroup"] = "0::/../outside";
        Assert.Null(Read(files).TotalBytes);
    }

    /// <summary>Mountinfo escapes are decoded without interpreting path traversal.</summary>
    [Fact]
    public void Escaped_Mount_And_Unified_Child_Are_Resolved()
    {
        var files = Files("0::/child", "1 0 0:1 / /sys/fs/cgroup\\040memory rw - cgroup2 cgroup rw");
        files["/sys/fs/cgroup memory/child/memory.max"] = "536870912";
        Assert.Equal(536870912, Read(files).TotalBytes);
    }

    /// <summary>Physical-byte conversion cannot overflow on corrupt input.</summary>
    [Fact]
    public void Unknown_Or_Overflowed_Physical_Ram_Is_Not_Zero()
    {
        var files = Files("0::/", "1 0 0:1 / /sys/fs/cgroup rw - cgroup2 cgroup rw");
        files["/proc/meminfo"] = "MemTotal: 9223372036854775807 kB";
        Assert.Null(Read(files).TotalBytes);
        files.Remove("/proc/meminfo");
        Assert.Null(Read(files).TotalBytes);
    }

    /// <summary>The three-argument point constructor remains usable and older observations have unknown capacity.</summary>
    [Fact]
    public void Capacity_History_Preserves_Unknown_Gaps_And_Legacy_Construction()
    {
        var metrics = new MemoryMetrics();
        metrics.RecordSample(42, new ResponseCacheStatus(), 0, 0, new MemoryCapacity(1024, MemoryCapacitySource.EnvironmentLimit));
        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1024, snapshot.Points[^1].TotalMemoryBytes);
        Assert.Equal(MemoryCapacitySource.EnvironmentLimit, snapshot.CapacitySource);
        metrics.RecordSample(43, new ResponseCacheStatus(), 0, 0);
        Assert.Null(metrics.GetSnapshot().Points[^1].TotalMemoryBytes);
        var legacy = new MemoryPoint(DateTimeOffset.UnixEpoch, 1, 0);
        var (timestamp, rss, cache) = legacy;
        Assert.Equal(DateTimeOffset.UnixEpoch, timestamp);
        Assert.Equal(1, rss);
        Assert.Equal(0, cache);
        Assert.Null(legacy.TotalMemoryBytes);
    }

    /// <summary>The supported operating system supplies a positive capacity and disposal makes it unavailable.</summary>
    [Fact]
    public void Native_Capacity_Is_Available_And_Reader_Disposal_Is_Safe()
    {
        using var reader = new ProcessMemoryReader();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) Assert.True(reader.ReadCapacity().TotalBytes > 0);
        reader.Dispose();
        Assert.Null(reader.ReadCapacity().TotalBytes);
    }

    /// <summary>Repeated RSS observations do not allocate a process/thread snapshot per call.</summary>
    [Fact]
    public void Rss_Sampling_DoesNotAllocate_Per_Observation()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var reader = new ProcessMemoryReader();
        _ = reader.ReadWorkingSetBytes();
        _ = reader.ReadWorkingSetBytes();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long? latest = null;
        for (var i = 0; i < 1000; i++) latest = reader.ReadWorkingSetBytes();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(latest > 0);
        Assert.True(allocated < 32768, $"RSS observations allocated {allocated} bytes.");
    }

    /// <summary>Builds deterministic pseudo-files for an 8 GiB physical machine.</summary>
    /// <param name="groups">Cgroup membership contents.</param>
    /// <param name="mounts">Mountinfo contents.</param>
    /// <returns>The fake kernel files.</returns>
    private static Dictionary<string, string> Files(string groups, string mounts) => new(StringComparer.Ordinal)
    {
        ["/proc/meminfo"] = "MemTotal: 8388608 kB\nMemAvailable: 1234 kB\n",
        ["/proc/self/cgroup"] = groups, ["/proc/self/mountinfo"] = mounts
    };

    /// <summary>Reads an observation from deterministic pseudo-files.</summary>
    /// <param name="files">The fake kernel files.</param>
    /// <returns>The observed capacity.</returns>
    private static MemoryCapacity Read(Dictionary<string, string> files) => MemoryCapacityReader.ReadLinux(path => Observation(files, path));

    /// <summary>Distinguishes existing and missing kernel files.</summary>
    /// <param name="files">The fake kernel files.</param>
    /// <param name="path">The requested path.</param>
    /// <returns>A successful read or confirmed missing file.</returns>
    private static MemoryCapacityReader.FileObservation Observation(Dictionary<string, string> files, string path) =>
        files.TryGetValue(path, out var text) ? new(true, false, text) : new(true, true, null);
}
