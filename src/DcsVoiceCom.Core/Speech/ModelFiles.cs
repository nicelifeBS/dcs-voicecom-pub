using System.IO.Compression;
using System.Security.Cryptography;

namespace DcsVoiceCom.Speech;

/// <summary>The Whistle weights and the needle engine that runs them.</summary>
public sealed record ModelFiles(string Weights, string Engine);

/// <summary>
/// Finds the Whistle model and the needle engine, and downloads them when missing. Model folders use the
/// cactus-needle cache layout: whistle\&lt;version&gt;\whistle.cact and v3\&lt;version&gt;\libneedle.dll (.so, .dylib).
/// </summary>
public static class ModelLocator
{
    private const int MaxLevels = 8;

    public static string EngineFileName =>
        OperatingSystem.IsWindows() ? "libneedle.dll" : OperatingSystem.IsMacOS() ? "libneedle.dylib" : "libneedle.so";

    /// <summary>Where the app downloads the model: %LOCALAPPDATA%\DcsVoiceCom\model.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DcsVoiceCom", "model");

    /// <summary>
    /// The configured model folder, or else the first folder holding both files among: the model\cactus-needle
    /// bundled next to the app, the app's download folder, a model\cactus-needle above the app (a checkout),
    /// WhistleAttack's model\cactus-needle beside the checkout, and the cactus-needle download cache. Null when none
    /// has them. Files in found folders only count when their SHA-256 is one of <paramref name="trustedSha256"/>
    /// (default: the pinned downloads), so a library planted in one of those folders is never loaded.
    /// </summary>
    public static string? ResolveDirectory(string? configured, string startDirectory, string? home = null, string? downloads = null,
        IReadOnlySet<string>? trustedSha256 = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        trustedSha256 ??= ModelDownloader.PinnedSha256;
        return Candidates(startDirectory, home, downloads ?? DefaultDirectory).FirstOrDefault(d => Find(d, trustedSha256) is not null);
    }

    /// <summary>
    /// The newest weights and engine in <paramref name="folder"/>, or null if either is missing. With
    /// <paramref name="trustedSha256"/>, files whose SHA-256 is not in it are skipped.
    /// </summary>
    public static ModelFiles? Find(string folder, IReadOnlySet<string>? trustedSha256 = null)
    {
        var weights = Newest(Path.Combine(folder, "whistle"), "whistle.cact", trustedSha256);
        var engine = Newest(Path.Combine(folder, "v3"), EngineFileName, trustedSha256);
        return weights is null || engine is null ? null : new ModelFiles(weights, engine);
    }

    private static string? Newest(string root, string name, IReadOnlySet<string>? trustedSha256)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(v => Version.TryParse(Path.GetFileName(v), out var version) ? version : new Version())
                .Select(v => Path.Combine(v, name))
                .FirstOrDefault(f => File.Exists(f) && (trustedSha256 is null || trustedSha256.Contains(Sha256(f))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static IEnumerable<string> Candidates(string startDirectory, string? home, string downloads)
    {
        var first = true;
        foreach (var dir in SelfAndAncestors(startDirectory))
        {
            yield return Path.Combine(dir, "model", "cactus-needle");
            if (first) yield return downloads; // after the bundled copy, before a checkout's
            first = false;
            foreach (var sibling in SafeDirectories(dir))
            {
                if (Path.GetFileName(sibling).Contains("whistleattack", StringComparison.OrdinalIgnoreCase))
                    yield return Path.Combine(sibling, "model", "cactus-needle");
            }
        }
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".cache", "cactus-needle");
    }

    private static IEnumerable<string> SelfAndAncestors(string directory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(directory));
        for (var i = 0; dir is not null && i < MaxLevels; i++, dir = dir.Parent) yield return dir.FullName;
    }

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Downloads the pinned Whistle weights and needle engine from Hugging Face (Cactus-Compute, Apache-2.0) and checks
/// their SHA-256. The engine ships inside cactus-needle's Python wheel; only the library is taken from it.
/// </summary>
public static class ModelDownloader
{
    public const string WhistleVersion = "2.0.0";
    public const string EngineVersion = "3.3.3";

    private const string WeightsUrl = "https://huggingface.co/Cactus-Compute/whistle/resolve/main/whistle.cact";
    private const string WeightsSha256 = "b6e02f048568ac5d01a2042556c658061e699acbc0aa2a1439f52f3d461dffeb";

    private sealed record EngineWheel(string Tag, string WheelSha256, string Member, string LibrarySha256);

    // The library inside the win_amd64 wheel is byte-identical to WhistleAttack's libneedle.dll 3.3.3.
    private static readonly EngineWheel WindowsX64 = new("win_amd64",
        "b6447b2be7426b7ff3b06360b15a90a8b7870e3fa2275023e20541d5e8d81bdb", "needle/libneedle3.dll",
        "1263c6003dd88b9fcb1904a55985be66858639849309280b42f7764ee5d874d0");
    private static readonly EngineWheel LinuxX64 = new("manylinux2014_x86_64",
        "b909897e11cf4da9566df6370f8a63564693fbd6edeaac0b8678b71865d4dc24", "needle/libneedle3.so",
        "87476ff70e80334d7c766dbf236b72f42876072df22014c6ec8a9b087b82b260");

    /// <summary>SHA-256 of the pinned weights and engine libraries: the only files a found model folder may hold.</summary>
    public static readonly IReadOnlySet<string> PinnedSha256 =
        new HashSet<string> { WeightsSha256, WindowsX64.LibrarySha256, LinuxX64.LibrarySha256 };

    /// <summary>
    /// Puts the model into <paramref name="folder"/> (cache layout) unless it is already there, and returns its files.
    /// </summary>
    public static async Task<ModelFiles> DownloadAsync(string folder, HttpClient http, Action<string>? log = null, CancellationToken ct = default)
    {
        var wheel = PlatformWheel() ?? throw new PlatformNotSupportedException(
            $"No pinned needle engine for {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}.");
        var weights = Path.Combine(folder, "whistle", WhistleVersion, "whistle.cact");
        var engine = Path.Combine(folder, "v3", EngineVersion, ModelLocator.EngineFileName);

        if (!await HasHashAsync(weights, WeightsSha256, ct).ConfigureAwait(false))
        {
            log?.Invoke($"Downloading the Whistle model ({WhistleVersion}) to {weights}");
            await SaveAsync(await GetAsync(http, WeightsUrl, WeightsSha256, ct).ConfigureAwait(false), weights, ct).ConfigureAwait(false);
        }
        if (!await HasHashAsync(engine, wheel.LibrarySha256, ct).ConfigureAwait(false))
        {
            var url = $"https://huggingface.co/Cactus-Compute/needle3/resolve/main/python/cactus_needle-{EngineVersion}-py3-none-{wheel.Tag}.whl";
            log?.Invoke($"Downloading the needle engine ({EngineVersion}) to {engine}");
            var archive = await GetAsync(http, url, wheel.WheelSha256, ct).ConfigureAwait(false);
            using var zip = new ZipArchive(new MemoryStream(archive));
            var entry = zip.GetEntry(wheel.Member) ?? throw new InvalidDataException($"{url} has no {wheel.Member}");
            using var library = new MemoryStream();
            await using (var stream = entry.Open()) await stream.CopyToAsync(library, ct).ConfigureAwait(false);
            CheckHash(library.ToArray(), wheel.LibrarySha256, wheel.Member);
            await SaveAsync(library.ToArray(), engine, ct).ConfigureAwait(false);
        }
        return new ModelFiles(weights, engine);
    }

    private static EngineWheel? PlatformWheel() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 when OperatingSystem.IsWindows() => WindowsX64,
        System.Runtime.InteropServices.Architecture.X64 when OperatingSystem.IsLinux() => LinuxX64,
        _ => null,
    };

    private static async Task<byte[]> GetAsync(HttpClient http, string url, string sha256, CancellationToken ct)
    {
        var data = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        CheckHash(data, sha256, url);
        return data;
    }

    private static void CheckHash(byte[] data, string sha256, string what)
    {
        var actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (actual != sha256) throw new InvalidDataException($"{what}: SHA-256 {actual}, expected {sha256}");
    }

    private static async Task<bool> HasHashAsync(string path, string sha256, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant() == sha256;
    }

    private static async Task SaveAsync(byte[] data, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
