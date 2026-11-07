using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace DcsVoiceCom.App;

/// <summary>
/// The help pages and their images, built into the exe from docs/help (see the .csproj). Resource names are
/// "help/&lt;path&gt;" with forward slashes.
/// </summary>
public static class HelpContent
{
    private const string Prefix = "help/";
    private static readonly Assembly s_assembly = typeof(HelpContent).Assembly;
    private static readonly Dictionary<string, BitmapImage?> s_images = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string Path, string Resource)> Resources() =>
        s_assembly.GetManifestResourceNames()
            .Select(r => (Path: r.Replace('\\', '/'), Resource: r))
            .Where(r => r.Path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.Path[Prefix.Length..], r.Resource));

    /// <summary>The Markdown pages: file name and text.</summary>
    public static IEnumerable<(string FileName, string Text)> Pages()
    {
        foreach (var (path, resource) in Resources())
        {
            if (path.Contains('/') || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = s_assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (path, reader.ReadToEnd());
        }
    }

    /// <summary>An image a page refers to ("images/main-window.png"), or null if it is not built in.</summary>
    public static BitmapImage? Image(string href)
    {
        var path = href.Replace('\\', '/').TrimStart('.', '/');
        lock (s_images)
        {
            if (s_images.TryGetValue(path, out var cached)) return cached;
            var resource = Resources().FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).Resource;
            BitmapImage? image = null;
            if (resource is not null)
            {
                using var stream = s_assembly.GetManifestResourceStream(resource)!;
                image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
            }
            s_images[path] = image;
            return image;
        }
    }

    /// <summary>The app version: "1.4.0", or "0.1.0-dev" for a local build (no commit hash).</summary>
    public static string Version
    {
        get
        {
            var version = s_assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? s_assembly.GetName().Version?.ToString() ?? "";
            var plus = version.IndexOf('+');
            return plus < 0 ? version : version[..plus];
        }
    }
}
