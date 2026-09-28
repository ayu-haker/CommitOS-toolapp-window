using System.Reflection;
using System.Text;
using System.Text.Json;
using DevOpsToolsInstaller.Models;

namespace DevOpsToolsInstaller.Services;

public sealed class CatalogService
{
    // This fork publishes its own signed catalog from the "main" branch of
    // ayu-haker/CommitOS-toolapp-window. Upstream is intentionally not listed:
    // its catalog is signed with a different key, so it could never pass the
    // pinned-key check and would only add pointless network round-trips.
    private static readonly string[] RemoteCatalogUrls =
    {
        "https://raw.githubusercontent.com/ayu-haker/CommitOS-toolapp-window/main/catalog/catalog.json"
    };

    private static readonly string[] RemoteBundlesUrls =
    {
        "https://raw.githubusercontent.com/ayu-haker/CommitOS-toolapp-window/main/catalog/bundles.json"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http;

    static CatalogService()
    {
        Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "DevOpsToolsInstaller/2.8.0 (Windows NT 10.0; Win64; x64)");
    }

    /// <summary>
    /// Loads the tool catalog. Uses the remote GitHub copy only when its
    /// signature verifies against the pinned public key; otherwise falls back
    /// to the embedded Assets/catalog.json (fail closed against tampering).
    /// </summary>
    public async Task<List<ToolDefinition>> LoadCatalogAsync(CancellationToken ct = default)
    {
        // 1. Try each remote location (signature-verified)
        foreach (var url in RemoteCatalogUrls)
        {
            string json;
            try
            {
                json = await Http.GetStringAsync(url, ct);
            }
            catch
            {
                continue;
            }

            if (!await VerifyRemoteSignatureAsync(url, json, ct))
            {
                ActivityLogService.Warn(
                    "Catalog", $"Remote catalog at {url} is unsigned or the signature is invalid — skipping.");
                continue;
            }

            var tools = JsonSerializer.Deserialize<List<ToolDefinition>>(json, JsonOptions);
            if (tools is { Count: > 0 })
                return tools;
        }

        ActivityLogService.Info(
            "Catalog", "No signed remote catalog available — using the built-in catalog.");

        // 2. Embedded fallback (baked into the app at build time)
        return LoadEmbeddedCatalog();
    }

    /// <summary>
    /// Loads curated tool bundles. Same fail-closed signature policy as the catalog.
    /// </summary>
    public async Task<List<ToolBundle>> LoadBundlesAsync(CancellationToken ct = default)
    {
        // 1. Try each remote location (signature-verified)
        foreach (var url in RemoteBundlesUrls)
        {
            string json;
            try
            {
                json = await Http.GetStringAsync(url, ct);
            }
            catch
            {
                continue;
            }

            if (!await VerifyRemoteSignatureAsync(url, json, ct))
            {
                ActivityLogService.Warn(
                    "Catalog", $"Remote bundles at {url} are unsigned or the signature is invalid — skipping.");
                continue;
            }

            var bundles = JsonSerializer.Deserialize<List<ToolBundle>>(json, JsonOptions);
            if (bundles is { Count: > 0 })
                return bundles;
        }

        ActivityLogService.Info(
            "Catalog", "No signed remote stacks available — using the built-in stacks.");

        // 2. Embedded fallback (baked into the app at build time)
        return LoadEmbeddedBundles();
    }

    /// <summary>
    /// Fetches the base64 ECDSA signature (&lt;url&gt;.sig) for a remote catalog
    /// file and verifies it against the pinned public key over the exact bytes
    /// served for the file.
    /// </summary>
    private static async Task<bool> VerifyRemoteSignatureAsync(
        string fileUrl, string content, CancellationToken ct)
    {
        try
        {
            var signature = await Http.GetStringAsync(fileUrl + ".sig", ct);
            return CatalogSignatureService.Verify(Encoding.UTF8.GetBytes(content), signature.Trim());
        }
        catch
        {
            // Missing signature file (404) or fetch failure counts as unsigned.
            return false;
        }
    }

    /// <summary>
    /// Loads the catalog that was copied into the output directory at build time.
    /// Uses AppContext.BaseDirectory so single-file deployments can locate the file.
    /// </summary>
    private static List<ToolDefinition> LoadEmbeddedCatalog()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Assets", "catalog.json");

        if (!File.Exists(catalogPath))
            return new List<ToolDefinition>();

        var json = File.ReadAllText(catalogPath);
        return JsonSerializer.Deserialize<List<ToolDefinition>>(json, JsonOptions)
               ?? new List<ToolDefinition>();
    }

    /// <summary>
    /// Loads the curated bundles copied into the output directory at build time.
    /// </summary>
    private static List<ToolBundle> LoadEmbeddedBundles()
    {
        var bundlesPath = Path.Combine(AppContext.BaseDirectory, "Assets", "bundles.json");

        if (!File.Exists(bundlesPath))
            return new List<ToolBundle>();

        var json = File.ReadAllText(bundlesPath);
        return JsonSerializer.Deserialize<List<ToolBundle>>(json, JsonOptions)
               ?? new List<ToolBundle>();
    }

    /// <summary>
    /// Groups tools by category in a stable order.
    /// </summary>
    public static IEnumerable<IGrouping<string, ToolDefinition>> GroupByCategory(
        IEnumerable<ToolDefinition> tools)
    {
        return tools
            .GroupBy(t => t.Category)
            .OrderBy(g => g.Key);
    }
}
