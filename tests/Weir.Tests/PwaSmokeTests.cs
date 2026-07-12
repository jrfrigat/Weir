using System.Text.Json;
using Xunit;

namespace Weir.Tests;

// Smoke tests for the admin PWA's installability assets: the web manifest, its declared icons, the
// service worker and the index wiring. These guard against a build that ships a non-installable or
// broken PWA (missing manifest, missing icons, unlinked service worker).
public class PwaSmokeTests
{
    /// <summary>Locates the admin PWA's wwwroot by walking up to the repo root (the solution file).</summary>
    /// <returns>The absolute path to <c>src/Weir.Admin/wwwroot</c>.</returns>
    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Weir.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var wwwroot = Path.Combine(dir!.FullName, "src", "Weir.Admin", "wwwroot");
        Assert.True(Directory.Exists(wwwroot), $"Admin wwwroot not found at {wwwroot}.");
        return wwwroot;
    }

    [Fact]
    public void Manifest_Declares_Name_And_Installable_Icons()
    {
        var manifestPath = Path.Combine(WwwRoot(), "manifest.webmanifest");
        Assert.True(File.Exists(manifestPath), "manifest.webmanifest is missing.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("short_name").GetString()));
        Assert.Equal("standalone", root.GetProperty("display").GetString());

        var icons = root.GetProperty("icons");
        Assert.True(icons.GetArrayLength() >= 2, "The manifest must declare at least a 192 and a 512 icon to be installable.");

        var sizes = icons.EnumerateArray().Select(i => i.GetProperty("sizes").GetString()).ToList();
        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
        Assert.Contains(icons.EnumerateArray(), i => i.GetProperty("purpose").GetString() == "maskable");
    }

    [Fact]
    public void Declared_Icon_Files_Exist()
    {
        var wwwroot = WwwRoot();
        var manifestPath = Path.Combine(wwwroot, "manifest.webmanifest");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));

        foreach (var icon in document.RootElement.GetProperty("icons").EnumerateArray())
        {
            var src = icon.GetProperty("src").GetString()!;
            var file = Path.Combine(wwwroot, src.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"Manifest icon {src} does not exist on disk.");
        }

        Assert.True(File.Exists(Path.Combine(wwwroot, "icons", "apple-touch-icon.png")), "apple-touch-icon.png is missing.");
    }

    [Fact]
    public void Service_Worker_And_Index_Wiring_Present()
    {
        var wwwroot = WwwRoot();
        Assert.True(File.Exists(Path.Combine(wwwroot, "service-worker.js")), "service-worker.js is missing.");
        Assert.True(File.Exists(Path.Combine(wwwroot, "service-worker.published.js")), "service-worker.published.js is missing.");

        var index = File.ReadAllText(Path.Combine(wwwroot, "index.html"));
        Assert.Contains("manifest.webmanifest", index, StringComparison.Ordinal);
        Assert.Contains("apple-touch-icon", index, StringComparison.Ordinal);
    }
}
