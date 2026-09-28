using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DevOpsToolsInstaller.Services;

/// <summary>A device reported by <c>adb devices -l</c>.</summary>
public sealed record AdbDevice(
    string Serial,
    string State,
    string Model,
    string Product,
    string Device)
{
    /// <summary>True when the device is authorised and ready to accept commands.</summary>
    public bool IsOnline => State == "device";

    public string DisplayName
    {
        get
        {
            var model = Model.Replace('_', ' ').Trim();
            return string.IsNullOrWhiteSpace(model) ? Serial : $"{model} ({Serial})";
        }
    }

    public string StateBadge => State switch
    {
        "device" => "Ready",
        "unauthorized" => "Not authorised",
        "offline" => "Offline",
        "recovery" => "Recovery",
        _ => State
    };
}

public sealed record SideloadResult(bool Success, string Message, string Output = "");

/// <summary>
/// Backs the Sideload page: locates adb, talks to attached Android devices, and
/// launches a local installer after applying the user's signature policy.
/// </summary>
public static class SideloadService
{
    private const int DeviceListTimeoutSeconds = 20;
    private const int InstallTimeoutSeconds = 300;

    // ── adb discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first usable adb.exe, or null when none is installed.
    /// The catalog-installed copy under Tools\adb\platform-tools is preferred
    /// over Tools\bin because adb.exe only works when AdbWinApi.dll and
    /// AdbWinUsbApi.dll sit next to it, and Tools\bin only receives *.exe files.
    /// </summary>
    public static string? FindAdb()
    {
        foreach (var candidate in AdbCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string> AdbCandidates()
    {
        var adb = "adb.exe";

        // Installed from this app's catalog: Tools\adb\platform-tools\adb.exe
        yield return Path.Combine(ArtifactService.ToolsRoot, "adb", "platform-tools", adb);
        // A flat or nested layout, depending on how the archive was unpacked
        yield return Path.Combine(ArtifactService.ToolsRoot, "adb", adb);
        yield return Path.Combine(ArtifactService.ToolsRoot, "platform-tools", adb);
        yield return Path.Combine(ArtifactService.BinFolder, adb);

        // A standard Android SDK install
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Android", "Sdk", "platform-tools", adb);

        foreach (var varName in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT", "ANDROID_SDK_HOME" })
        {
            var root = Environment.GetEnvironmentVariable(varName);
            if (!string.IsNullOrWhiteSpace(root))
                yield return Path.Combine(root, "platform-tools", adb);
        }
    }

    /// <summary>True when adb is on the user PATH even though we found no file.</summary>
    public static bool IsAdbOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), "adb.exe")));
    }

    // ── devices ──────────────────────────────────────────────────────────────

    public static async Task<(IReadOnlyList<AdbDevice> Devices, string? Error)> GetDevicesAsync(
        string adbPath, CancellationToken ct = default)
    {
        var run = await RunAsync(adbPath, "devices -l", DeviceListTimeoutSeconds, ct);
        if (!run.ExitCode.Equals(0))
            return (Array.Empty<AdbDevice>(), run.Output.Trim());

        var devices = new List<AdbDevice>();
        foreach (var raw in run.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Skip the header and the "* daemon started *" chatter
            if (line.StartsWith("List of devices")) continue;
            if (line.StartsWith("*")) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string Value(string key)
            {
                foreach (var p in parts)
                    if (p.StartsWith(key + ":", StringComparison.Ordinal))
                        return p[(key.Length + 1)..];
                return "";
            }

            devices.Add(new AdbDevice(
                Serial: parts[0],
                State: parts[1],
                Model: Value("model"),
                Product: Value("product"),
                Device: Value("device")));
        }

        return (devices, null);
    }

    // ── APK install ──────────────────────────────────────────────────────────

    /// <summary>
    /// Installs an APK onto the given device. <paramref name="allowDowngrade"/>
    /// maps to <c>-d</c>, which is what you need to replace an app with an
    /// older build; <paramref name="grantPermissions"/> maps to <c>-g</c>.
    /// </summary>
    public static async Task<SideloadResult> InstallApkAsync(
        string adbPath,
        string serial,
        string apkPath,
        bool allowDowngrade = false,
        bool grantPermissions = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(apkPath))
            return new SideloadResult(false, $"APK not found: {apkPath}");

        var args = new StringBuilder($"-s \"{serial}\" install -r");
        if (allowDowngrade) args.Append(" -d");
        if (grantPermissions) args.Append(" -g");
        args.Append($" \"{apkPath}\"");

        ActivityLogService.Info("Sideload", $"Installing {Path.GetFileName(apkPath)} on {serial}");

        var run = await RunAsync(adbPath, args.ToString(), InstallTimeoutSeconds, ct);
        var output = run.Output.Trim();

        // adb prints a literal "Success" on its own line when the install lands.
        var success = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(l => l.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase));

        if (success)
        {
            ActivityLogService.Success("Sideload", $"Installed {Path.GetFileName(apkPath)} on {serial}");
            return new SideloadResult(true, "APK installed successfully.", output);
        }

        // Surface the INSTALL_FAILED_* reason; adb puts it in brackets.
        var reason = output.Contains("Failure [", StringComparison.OrdinalIgnoreCase)
            ? output[(output.IndexOf("Failure [", StringComparison.OrdinalIgnoreCase))..]
                .Split('\n')[0].Trim()
            : output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();

        ActivityLogService.Error("Sideload", reason ?? "APK install failed");
        return new SideloadResult(false, reason ?? "APK install failed.", output);
    }

    /// <summary>Uninstalls a package, used to clear a device before re-installing.</summary>
    public static async Task<SideloadResult> UninstallPackageAsync(
        string adbPath, string serial, string packageName, CancellationToken ct = default)
    {
        var run = await RunAsync(
            adbPath, $"-s \"{serial}\" uninstall {packageName}", InstallTimeoutSeconds, ct);
        var output = run.Output.Trim();
        var success = output.Contains("Success", StringComparison.OrdinalIgnoreCase);
        return new SideloadResult(
            success,
            success ? $"Uninstalled {packageName}." : $"Could not uninstall {packageName}.",
            output);
    }

    // ── local installers ─────────────────────────────────────────────────────

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Verifies a local .exe/.msi against the user's signature policy and then
    /// launches it. Returns a failure without launching when the policy blocks it.
    /// </summary>
    public static SideloadResult RunLocalInstaller(string filePath, out AuthenticodeResult signature)
    {
        if (!File.Exists(filePath))
        {
            signature = default!;
            return new SideloadResult(false, $"File not found: {filePath}");
        }

        signature = AuthenticodeService.VerifyFile(filePath);
        var isTrustworthy = signature.IsValid || signature.Status == SignatureStatus.Unknown;

        if (!isTrustworthy && SettingsService.SignaturePolicy == SignaturePolicy.BlockUnsigned)
        {
            ActivityLogService.Error("Sideload", $"Blocked by signature policy: {signature.StatusBadge}");
            return new SideloadResult(
                false,
                "Not launched: the signature policy in Settings > Security blocks files " +
                $"without a verified trusted signature ({signature.StatusBadge}).");
        }

        try
        {
            // UseShellExecute lets Windows apply its own per-file-type handling
            // (msiexec for .msi) and raise the vendor UAC prompt when needed.
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });

            ActivityLogService.Info("Sideload", $"Launched {Path.GetFileName(filePath)} ({signature.StatusBadge})");
            return new SideloadResult(true, $"Launched {Path.GetFileName(filePath)}.", signature.StatusBadge);
        }
        catch (Exception ex)
        {
            ActivityLogService.Error("Sideload", $"Could not launch {Path.GetFileName(filePath)}: {ex.Message}");
            return new SideloadResult(false, $"Could not launch: {ex.Message}");
        }
    }

    // ── process plumbing ─────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return (-1, $"Failed to start {Path.GetFileName(fileName)}: {ex.Message}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(cts.Token);

        var exited = await Task.Run(() => proc.WaitForExit(timeoutSeconds * 1000), cts.Token);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, $"{Path.GetFileName(fileName)} timed out after {timeoutSeconds}s.");
        }

        var sb = new StringBuilder();
        sb.Append(await stdout);
        var err = await stderr;
        if (!string.IsNullOrWhiteSpace(err)) sb.Append(err);

        return (proc.ExitCode, sb.ToString());
    }
}
