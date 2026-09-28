using System.Reflection;

namespace DevOpsToolsInstaller.Services;

/// <summary>
/// Materializes bundled assets (tool catalog, curated stacks and tool logos)
/// next to the executable when they are missing.
///
/// A single-file publish merges every content file into the .exe, so the
/// <c>Assets\</c> folder does not exist on disk. The catalog and logo loaders
/// both read from <see cref="AppContext.BaseDirectory"/>, so without this step
/// a single-file build starts with an empty catalog and no logos — and stays
/// empty whenever the signed remote catalog cannot be fetched.
///
/// Extraction is idempotent: existing files are never rewritten, so upgrades
/// and user edits are preserved and repeat launches cost a single directory
/// check per asset.
/// </summary>
public static class AssetExtractor
{
    /// <summary>Folder the loaders expect assets in, and the in-resource separator.</summary>
    private const string AssetRoot = "Assets";
    private const string Separator = "__";

    /// <summary>
    /// Resource names begin with the asset root joined by <see cref="Separator"/>,
    /// e.g. <c>Assets__logos__k6.svg</c>. Only the separator is a marker; the
    /// root itself is a real path segment that has to be restored on extraction.
    /// </summary>
    private const string Prefix = AssetRoot + Separator;

    /// <summary>
    /// Writes every embedded <c>Assets__*</c> resource to disk if it is not
    /// already present. Failures are swallowed — a missing logo must never
    /// stop the app from launching.
    /// </summary>
    public static void EnsureAssetsExtracted()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var root = AppContext.BaseDirectory;

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(Prefix, StringComparison.Ordinal))
                    continue;

                var subPath = resourceName[Prefix.Length..]
                    .Replace(Separator, Path.DirectorySeparatorChar.ToString());

                if (string.IsNullOrEmpty(subPath))
                    continue;

                var target = Path.Combine(root, AssetRoot, subPath);
                if (File.Exists(target))
                    continue;

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using var source = assembly.GetManifestResourceStream(resourceName);
                if (source is null)
                    continue;

                using var destination = File.Create(target);
                source.CopyTo(destination);
            }
        }
        catch
        {
            // Never let asset extraction prevent the app from starting.
        }
    }
}
