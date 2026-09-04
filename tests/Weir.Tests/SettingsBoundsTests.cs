using System.Reflection;
using Weir.Contracts;
using Xunit;

namespace Weir.Tests;

// The bounds table is the one place the admin form and the admin API agree about what a settings value
// may be, so what it must not do is drift away from the settings record it describes.
public class SettingsBoundsTests
{
    [Fact]
    public void Defaults_Are_In_Range()
    {
        Assert.Null(SettingsBounds.FirstViolation(new WeirSystemSettings()));
    }

    [Fact]
    public void Negative_Is_Rejected()
    {
        var violation = SettingsBounds.FirstViolation(new WeirSystemSettings { MaxRows = -1 });
        Assert.Equal(nameof(WeirSystemSettings.MaxRows), violation?.Setting);
    }

    [Fact]
    public void MaxValue_Is_Rejected()
    {
        // The case the bounds exist for: int.MaxValue passes a "must not be negative" check and then
        // overflows the moment anything multiplies or converts it.
        var violation = SettingsBounds.FirstViolation(new WeirSystemSettings { RequestTimeoutSeconds = int.MaxValue });
        Assert.Equal(nameof(WeirSystemSettings.RequestTimeoutSeconds), violation?.Setting);
    }

    [Fact]
    public void Zero_Is_Accepted_Everywhere()
    {
        // Zero means "unlimited" or "disabled" on every one of these settings - a supported configuration,
        // not an accident, so no bound may exclude it.
        Assert.All(SettingsBounds.All, b => Assert.Equal(0, b.Min));
    }

    [Fact]
    public void Every_Numeric_Setting_Has_A_Bound()
    {
        // A new numeric setting that nobody bounds is unvalidated on both sides at once, and nothing else
        // would notice: the form renders it and the API accepts whatever it is sent.
        var numeric = typeof(WeirSystemSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(int) || p.PropertyType == typeof(long))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var bounded = SettingsBounds.All.Select(b => b.Setting).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(numeric.OrderBy(n => n, StringComparer.Ordinal), bounded.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_Bound_Reads_Its_Own_Setting()
    {
        // A copy-paste in the table - two bounds sharing one accessor - would silently leave a setting
        // unchecked. Pushing each one past its own maximum must name that setting and no other.
        foreach (var bound in SettingsBounds.All)
        {
            var property = typeof(WeirSystemSettings).GetProperty(bound.Setting);
            Assert.NotNull(property);

            var over = bound.Max + 1;
            var settings = new WeirSystemSettings();
            // Box to the property's own type. The cast to object has to be inside the branch: without it
            // the conditional settles on long as its common type and boxes an Int64 either way, which the
            // int setter refuses.
            object value = property!.PropertyType == typeof(int) ? (object)(int)over : over;
            property.SetValue(settings, value);

            Assert.Equal(bound.Setting, SettingsBounds.FirstViolation(settings)?.Setting);
        }
    }
}
