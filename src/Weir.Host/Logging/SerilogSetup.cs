using System.Globalization;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Weir.Host.Options;

namespace Weir.Host.Logging;

/// <summary>
/// Builds the Serilog logger from <see cref="WeirLoggingOptions"/>: a rolling file sink (directory,
/// interval, size limit, retention and format all configurable) plus an optional console sink.
/// </summary>
internal static class SerilogSetup
{
    /// <summary>Applies the configured sinks and levels to a Serilog logger configuration.</summary>
    /// <param name="config">The logger configuration to populate.</param>
    /// <param name="options">The bound logging options.</param>
    /// <param name="contentRoot">The host content root, used to resolve a relative log directory.</param>
    public static void Apply(LoggerConfiguration config, WeirLoggingOptions options, string contentRoot)
    {
        config
            .MinimumLevel.Is(ParseLevel(options.MinimumLevel))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext();

        if (options.ConsoleEnabled)
        {
            config.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
        }

        if (!options.FileEnabled)
        {
            return;
        }

        var directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(contentRoot, options.Directory);
        var path = Path.Combine(directory, options.FileName);
        var interval = ParseInterval(options.RollingInterval);
        var retainTime = options.RetainedFileTimeLimitDays is int days && days > 0
            ? TimeSpan.FromDays(days)
            : (TimeSpan?)null;

        if (string.Equals(options.Format, "Json", StringComparison.OrdinalIgnoreCase))
        {
            config.WriteTo.File(
                new CompactJsonFormatter(),
                path,
                rollingInterval: interval,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: options.FileSizeLimitBytes.HasValue,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                retainedFileTimeLimit: retainTime);
        }
        else
        {
            config.WriteTo.File(
                path,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: interval,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: options.FileSizeLimitBytes.HasValue,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                retainedFileTimeLimit: retainTime);
        }
    }

    /// <summary>Parses a level name, falling back to Information when unrecognized.</summary>
    /// <param name="value">The configured level name.</param>
    /// <returns>The parsed <see cref="LogEventLevel"/>.</returns>
    private static LogEventLevel ParseLevel(string value) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level) ? level : LogEventLevel.Information;

    /// <summary>Parses a rolling-interval name, falling back to Day when unrecognized.</summary>
    /// <param name="value">The configured interval name.</param>
    /// <returns>The parsed <see cref="RollingInterval"/>.</returns>
    private static RollingInterval ParseInterval(string value) =>
        Enum.TryParse<RollingInterval>(value, ignoreCase: true, out var interval) ? interval : RollingInterval.Day;
}
