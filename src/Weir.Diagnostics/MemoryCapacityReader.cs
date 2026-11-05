using System.Globalization;
using System.Runtime.InteropServices;
using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>Reads OS capacity independently of GC heap limits and caches only stable cgroup file paths.</summary>
internal sealed class MemoryCapacityReader
{
    /// <summary>Visible Linux controller files; their contents are read again for each observation.</summary>
    private readonly Lazy<LimitFiles> _linuxFiles = new(() => DiscoverLimits(ReadFile));

    /// <summary>Reads physical capacity constrained by visible cgroup or Windows job limits.</summary>
    /// <returns>A capacity observation, or unknown when the OS or its limits cannot be inspected safely.</returns>
    internal MemoryCapacity Read()
    {
        if (OperatingSystem.IsWindows()) return ReadWindows();
        if (OperatingSystem.IsLinux()) return ReadLinux(ReadFile, _linuxFiles.Value);
        return default;
    }

    /// <summary>Reads a Linux observation with injectable kernel files for deterministic tests.</summary>
    /// <param name="read">The kernel-file reader.</param>
    /// <returns>Physical memory constrained by every visible ancestor limit.</returns>
    internal static MemoryCapacity ReadLinux(Func<string, FileObservation> read) => ReadLinux(read, DiscoverLimits(read));

    /// <summary>Reads the current physical capacity and cached controller locations.</summary>
    /// <param name="read">The kernel-file reader.</param>
    /// <param name="files">Controller locations discovered at initialization.</param>
    /// <returns>A capacity observation; unreadable limits produce unknown rather than host RAM.</returns>
    private static MemoryCapacity ReadLinux(Func<string, FileObservation> read, LimitFiles files)
    {
        var info = read("/proc/meminfo");
        if (!info.Success || info.Text is null || !files.Known) return default;
        long? physical = null;
        foreach (var line in info.Text.Split('\n'))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[2] == "kB" && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kib) && kib > 0 && kib <= long.MaxValue / 1024)
                physical = kib * 1024;
            break;
        }
        if (physical is null) return default;
        var total = physical.Value;
        var source = MemoryCapacitySource.PhysicalMemory;
        foreach (var path in files.Paths)
        {
            var file = read(path);
            if (file.Missing) continue; // The v2 root, or a disabled controller, has no limit file.
            if (!file.Success || file.Text is null) return default;
            var value = file.Text.Trim();
            if (value == "max") continue;
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)) return default;
            if (limit <= (ulong)total) { total = (long)limit; source = MemoryCapacitySource.EnvironmentLimit; }
        }
        return new MemoryCapacity(total, source);
    }

    /// <summary>Resolves v1/v2 memberships against mount roots, including visible parent limits.</summary>
    /// <param name="read">The kernel-file reader.</param>
    /// <returns>Bounded paths, or an unknown discovery when membership cannot be resolved.</returns>
    private static LimitFiles DiscoverLimits(Func<string, FileObservation> read)
    {
        var groups = read("/proc/self/cgroup");
        var mounts = read("/proc/self/mountinfo");
        if (!groups.Success || groups.Text is null || !mounts.Success || mounts.Text is null) return new(false, []);
        var memberships = groups.Text.Split('\n').Select(l => l.Split(':', 3)).Where(p => p.Length == 3).ToArray();
        var memory = memberships.FirstOrDefault(p => p[1].Split(',').Contains("memory", StringComparer.Ordinal));
        var unified = memory is null;
        memory ??= memberships.FirstOrDefault(p => p[0] == "0" && p[1].Length == 0);
        if (memory is null) return new(true, []);
        var group = Decode(memory[2]);
        if (!SafePath(group)) return new(false, []);
        foreach (var mount in mounts.Text.Split('\n'))
        {
            var halves = mount.Split(" - ", 2, StringSplitOptions.None);
            if (halves.Length != 2) continue;
            var before = halves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var after = halves[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (before.Length < 6 || after.Length < 3 || after[0] != (unified ? "cgroup2" : "cgroup")) continue;
            if (!unified && !after[2].Split(',').Contains("memory", StringComparer.Ordinal)) continue;
            var root = Decode(before[3]).TrimEnd('/');
            var point = Decode(before[4]).TrimEnd('/');
            if (!SafePath(root.Length == 0 ? "/" : root) || !SafePath(point)) return new(false, []);
            string relative;
            if (group == "/" || group == root) relative = string.Empty;
            else if (root.Length == 0) relative = group;
            else if (group.StartsWith(root + "/", StringComparison.Ordinal)) relative = group[root.Length..];
            else continue;
            var current = point + relative;
            List<string> paths = [];
            while (true)
            {
                if (paths.Count == 128) return new(false, []);
                paths.Add(current + (unified ? "/memory.max" : "/memory.limit_in_bytes"));
                if (current == point) break;
                var slash = current.LastIndexOf('/');
                if (slash < point.Length) return new(false, []);
                current = current[..slash];
            }
            return new(true, paths.ToArray());
        }
        return new(false, []);
    }

    /// <summary>Refuses path traversal and malformed pseudo-filesystem paths.</summary>
    /// <param name="path">Decoded absolute Linux path.</param>
    /// <returns>Whether the path is a bounded absolute path without traversal.</returns>
    private static bool SafePath(string path) => path.StartsWith('/') && path.Length <= 4096 &&
        !path.Split('/').Any(p => p is "." or "..") && !path.Contains('\0');

    /// <summary>Decodes the four escapes used in proc mountinfo paths.</summary>
    /// <param name="path">Encoded path.</param>
    /// <returns>The decoded path, without interpreting arbitrary escapes.</returns>
    private static string Decode(string path) => path.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    /// <summary>Reads trusted kernel metadata; a missing controller differs from denied access.</summary>
    /// <param name="path">A discovered kernel path.</param>
    /// <returns>Contents or a classified unavailable observation.</returns>
    private static FileObservation ReadFile(string path)
    {
        try { var text = File.ReadAllText(path); return new(text.Length <= 131072, false, text.Length <= 131072 ? text : null); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(true, true, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Reads physical RAM and the current Windows job's commit bounds when present.</summary>
    /// <returns>OS capacity constrained by the visible job, or unknown on a failed query.</returns>
    private static MemoryCapacity ReadWindows()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 || status.TotalPhysical > long.MaxValue) return default;
        var total = status.TotalPhysical;
        var source = MemoryCapacitySource.PhysicalMemory;
        if (!IsProcessInJob(new IntPtr(-1), IntPtr.Zero, out var inJob)) return default;
        if (inJob)
        {
            if (!QueryInformationJobObject(IntPtr.Zero, 9, out var job, (uint)Marshal.SizeOf<JobLimits>(), IntPtr.Zero)) return default;
            if ((job.Basic.Flags & 0x100) != 0 && job.ProcessMemory <= total) { total = job.ProcessMemory; source = MemoryCapacitySource.EnvironmentLimit; }
            if ((job.Basic.Flags & 0x200) != 0 && job.JobMemory <= total) { total = job.JobMemory; source = MemoryCapacitySource.EnvironmentLimit; }
        }
        return new MemoryCapacity((long)total, source);
    }

    /// <summary>One kernel-file read, distinguishing a missing controller from a failed read.</summary>
    /// <param name="Success">Whether reading or classifying the file succeeded.</param>
    /// <param name="Missing">Whether the file is absent.</param>
    /// <param name="Text">Contents for a successful existing file.</param>
    internal readonly record struct FileObservation(bool Success, bool Missing, string? Text);

    /// <summary>Resolved bounded controller paths.</summary>
    /// <param name="Known">Whether discovery succeeded.</param>
    /// <param name="Paths">All visible ancestor limit files.</param>
    private sealed record LimitFiles(bool Known, string[] Paths);

    /// <summary>Native physical-memory status layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        /// <summary>Native structure size.</summary>
        public uint Length;
        /// <summary>OS memory utilization percentage.</summary>
        public uint Load;
        /// <summary>Physical capacity.</summary>
        public ulong TotalPhysical;
        /// <summary>Currently available physical bytes.</summary>
        public ulong AvailablePhysical;
        /// <summary>Commit capacity.</summary>
        public ulong TotalPageFile;
        /// <summary>Available commit bytes.</summary>
        public ulong AvailablePageFile;
        /// <summary>User address-space capacity.</summary>
        public ulong TotalVirtual;
        /// <summary>Available user address space.</summary>
        public ulong AvailableVirtual;
        /// <summary>Reserved native field.</summary>
        public ulong AvailableExtendedVirtual;
    }

    /// <summary>Native basic job limits, preserving architecture-dependent pointer alignment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        /// <summary>Per-process CPU time bound.</summary>
        public long ProcessTime;
        /// <summary>Job CPU time bound.</summary>
        public long JobTime;
        /// <summary>Enabled native limits.</summary>
        public uint Flags;
        /// <summary>Minimum working set.</summary>
        public nuint MinimumWorkingSet;
        /// <summary>Maximum working set.</summary>
        public nuint MaximumWorkingSet;
        /// <summary>Active process count bound.</summary>
        public uint ActiveProcesses;
        /// <summary>Processor affinity.</summary>
        public nuint Affinity;
        /// <summary>Priority class.</summary>
        public uint PriorityClass;
        /// <summary>Scheduling class.</summary>
        public uint SchedulingClass;
    }

    /// <summary>Native extended job limit layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        /// <summary>Basic limits.</summary>
        public BasicJobLimits Basic;
        /// <summary>Read operation count.</summary>
        public ulong Reads;
        /// <summary>Write operation count.</summary>
        public ulong Writes;
        /// <summary>Other operation count.</summary>
        public ulong Other;
        /// <summary>Read byte count.</summary>
        public ulong ReadBytes;
        /// <summary>Write byte count.</summary>
        public ulong WriteBytes;
        /// <summary>Other byte count.</summary>
        public ulong OtherBytes;
        /// <summary>Per-process commit limit.</summary>
        public nuint ProcessMemory;
        /// <summary>Job-wide commit limit.</summary>
        public nuint JobMemory;
        /// <summary>Peak process commitment.</summary>
        public nuint PeakProcessMemory;
        /// <summary>Peak job commitment.</summary>
        public nuint PeakJobMemory;
    }

    /// <summary>Reads Windows physical capacity without WMI or a process-wide scan.</summary>
    /// <param name="status">Initialized native status structure, filled by the operating system.</param>
    /// <returns>Whether the query succeeded.</returns>
    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    /// <summary>Checks whether the process belongs to a Windows job.</summary>
    /// <param name="process">The current-process pseudo-handle.</param>
    /// <param name="job">Null to inspect membership in any job.</param>
    /// <param name="result">Whether the process belongs to a job.</param>
    /// <returns>Whether the membership query succeeded.</returns>
    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    /// <summary>Reads visible current-job memory bounds without opening another handle.</summary>
    /// <param name="job">Null to inspect the calling process's current job.</param>
    /// <param name="kind">The native extended-limit information class.</param>
    /// <param name="info">Limits returned by the operating system.</param>
    /// <param name="length">The native output structure size.</param>
    /// <param name="returnedLength">Optional output length pointer; null when unused.</param>
    /// <returns>Whether the current-job query succeeded.</returns>
    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int kind, out JobLimits info, uint length, IntPtr returnedLength);
}
