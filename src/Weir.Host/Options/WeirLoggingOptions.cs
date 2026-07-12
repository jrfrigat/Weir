namespace Weir.Host.Options;

/// <summary>
/// File-logging settings, bound from <c>Weir:Logging</c>. They configure the Serilog file sink: where
/// logs are written, how they roll, how long they are kept, and their format. These are applied at
/// startup (the logger is built before the DI container), so changing them requires a restart; they
/// are surfaced read-only in the admin panel so operators can see where logs land and how long they
/// are retained.
/// </summary>
public sealed class WeirLoggingOptions
{
    /// <summary>Whether to write logs to a rolling file. On by default.</summary>
    public bool FileEnabled { get; set; } = true;

    /// <summary>Whether to also write logs to the console. On by default.</summary>
    public bool ConsoleEnabled { get; set; } = true;

    /// <summary>Directory the log files are written to (created if missing). Relative to the content root.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>
    /// Base file name; the rolling interval and, when a file reaches the size limit, a sequence number
    /// are inserted before the extension (for example <c>weir-20260101.log</c>).
    /// </summary>
    public string FileName { get; set; } = "weir-.log";

    /// <summary>Rolling interval: <c>Infinite</c>, <c>Year</c>, <c>Month</c>, <c>Day</c>, <c>Hour</c> or <c>Minute</c>.</summary>
    public string RollingInterval { get; set; } = "Day";

    /// <summary>Roll to a new file once the current one reaches this many bytes. Null disables the size roll.</summary>
    public long? FileSizeLimitBytes { get; set; } = 52_428_800;

    /// <summary>Maximum number of retained rolling files (older ones are deleted). Null keeps them all.</summary>
    public int? RetainedFileCountLimit { get; set; } = 31;

    /// <summary>Maximum age of retained files in days (older ones are deleted). Null disables the time limit.</summary>
    public int? RetainedFileTimeLimitDays { get; set; }

    /// <summary>Output format: <c>Text</c> (human-readable) or <c>Json</c> (compact JSON, one object per line).</summary>
    public string Format { get; set; } = "Text";

    /// <summary>Minimum level written: <c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c> or <c>Fatal</c>.</summary>
    public string MinimumLevel { get; set; } = "Information";
}
