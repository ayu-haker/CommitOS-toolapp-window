using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DevOpsToolsInstaller.Services;

namespace DevOpsToolsInstaller.Views;

public sealed partial class SideloadPage : Page
{
    private string? _adbPath;
    private string? _adbSource;
    private List<AdbDevice> _devices = new();
    private string? _apkPath;
    private string? _installerPath;
    private bool _initialised;
    private bool _busy;

    public SideloadPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_initialised) return;
        _initialised = true;

        RefreshAdbStatus();
        UpdateInstallButton();
        UpdatePolicyHint();
        if (_adbPath is not null)
            _ = RefreshDevicesAsync();
    }

    // ── adb status ───────────────────────────────────────────────────────────

    private void RefreshAdbStatus()
    {
        _adbPath = SideloadService.FindAdb();

        if (_adbPath is not null)
        {
            _adbSource = DescribeSource(_adbPath);
            AdbStatusText.Text = "adb found";
            AdbPathText.Text = _adbSource;
            GetAdbButton.Visibility = Visibility.Collapsed;
            return;
        }

        AdbStatusText.Text = "adb not found";
        AdbPathText.Text = SideloadService.IsAdbOnPath()
            ? "adb is on your PATH but could not be located from this app. Open a terminal and run it from there."
            : "Install Android platform-tools to sideload APKs.";
        GetAdbButton.Visibility = Visibility.Visible;
        _devices = new();
        DevicePicker.ItemsSource = null;
        DeviceStateText.Text = "";
    }

    private static string DescribeSource(string adbPath)
    {
        if (adbPath.StartsWith(ArtifactService.ToolsRoot, StringComparison.OrdinalIgnoreCase))
            return $"{adbPath}  (installed by this app)";
        if (adbPath.Contains("platform-tools", StringComparison.OrdinalIgnoreCase))
            return $"{adbPath}  (Android SDK)";
        return adbPath;
    }

    private void GetAdb_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindowInstance?.NavigateToCatalogWithSearch("adb");
    }

    // ── devices ──────────────────────────────────────────────────────────────

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_adbPath is null)
        {
            RefreshAdbStatus();
            return;
        }

        DeviceStateText.Text = "Scanning for devices…";
        var (devices, error) = await SideloadService.GetDevicesAsync(_adbPath);

        if (error is not null)
        {
            _devices = new();
            DevicePicker.ItemsSource = null;
            DeviceStateText.Text = error;
            UpdateInstallButton();
            return;
        }

        _devices = devices.ToList();
        DevicePicker.ItemsSource = _devices;

        var online = _devices.Count(d => d.IsOnline);
        if (_devices.Count == 0)
        {
            DeviceStateText.Text = "No devices found. Plug in USB and enable USB debugging on the device.";
        }
        else
        {
            DeviceStateText.Text =
                $"{_devices.Count} device(s) found, {online} ready.";

            // Preselect the first device that can actually accept an install
            var firstReady = _devices.FirstOrDefault(d => d.IsOnline);
            if (firstReady is not null)
                DevicePicker.SelectedItem = firstReady;
        }

        UpdateInstallButton();
    }

    private void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInstallButton();
    }

    private AdbDevice? SelectedDevice =>
        DevicePicker.SelectedItem as AdbDevice;

    // ── APK ──────────────────────────────────────────────────────────────────

    private async void PickApk_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(".apk");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        _apkPath = file.Path;
        ApkNameText.Text = file.Name;

        try
        {
            var info = new FileInfo(_apkPath);
            ApkMetaText.Text = $"{FormatSize(info.Length)}   {info.FullName}";
        }
        catch
        {
            ApkMetaText.Text = _apkPath;
        }

        UpdateInstallButton();
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        if (_adbPath is null || _apkPath is null) return;
        var device = SelectedDevice;
        if (device is null) return;

        if (_busy) return;
        _busy = true;
        InstallApkButton.IsEnabled = false;
        ShowOutput(ApkOutputBorder, ApkOutputText, $"Installing on {device.Serial}…");
        StatusText.Text = "";

        try
        {
            var result = await SideloadService.InstallApkAsync(
                _adbPath,
                device.Serial,
                _apkPath,
                allowDowngrade: AllowDowngradeCheck.IsChecked == true,
                grantPermissions: GrantPermissionsCheck.IsChecked == true);

            ShowOutput(ApkOutputBorder, ApkOutputText, result.Output.Length > 0 ? result.Output : result.Message);
            StatusText.Text = result.Success
                ? $"{Path.GetFileName(_apkPath)} installed on {device.DisplayName}."
                : $"Install failed: {result.Message}";
        }
        catch (Exception ex)
        {
            ShowOutput(ApkOutputBorder, ApkOutputText, ex.Message);
            StatusText.Text = $"Install failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            UpdateInstallButton();
        }
    }

    private void UpdateInstallButton()
    {
        var device = SelectedDevice;
        InstallApkButton.IsEnabled =
            !_busy && _adbPath is not null && _apkPath is not null && device is { IsOnline: true };

        if (device is { IsOnline: false })
            DeviceStateText.Text = $"{device.DisplayName} — {device.StateBadge}. " +
                (device.State == "unauthorized"
                    ? "Accept the 'Allow USB debugging' prompt on the device."
                    : "This device cannot accept installs right now.");
    }

    // ── local installer ──────────────────────────────────────────────────────

    private async void PickInstaller_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".msi");
        picker.FileTypeFilter.Add(".msix");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        _installerPath = file.Path;
        InstallerNameText.Text = file.Name;
        InstallerPathText.Text = file.Path;

        try
        {
            var info = new FileInfo(file.Path);
            InstallerSizeText.Text = FormatSize(info.Length);

            // Hashing a multi-hundred-megabyte installer on the UI thread would
            // freeze the window, so it runs on the thread pool.
            try
            {
                var hash = await Task.Run(() => SideloadService.ComputeSha256(file.Path));
                InstallerHashText.Text = "SHA-256  " +
                    string.Join(' ', Enumerable.Range(0, 64 / 8)
                        .Select(i => hash.Substring(i * 8, 8)));
            }
            catch (Exception ex)
            {
                InstallerHashText.Text = "SHA-256  (could not hash: " + ex.Message + ")";
            }

            var signature = AuthenticodeService.VerifyFile(file.Path);
            SignatureBadgeText.Text = signature.StatusBadge;
            SignatureBadgeBorder.Visibility = Visibility.Visible;
            ApplySignatureColours(signature);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not inspect that file: {ex.Message}";
        }

        UpdateRunButton();
    }

    private void ApplySignatureColours(AuthenticodeResult signature)
    {
        SignatureBadgeBorder.Background = signature.Status switch
        {
            SignatureStatus.Valid => new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen),
            SignatureStatus.NotSigned or SignatureStatus.Untrusted or SignatureStatus.InvalidHash =>
                new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            _ => new SolidColorBrush(Microsoft.UI.Colors.DarkOrange)
        };
        SignatureBadgeText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private void UpdateRunButton()
    {
        var blocked = _installerPath is not null
            && SettingsService.SignaturePolicy == SignaturePolicy.BlockUnsigned
            && !IsTrusted(_installerPath);

        RunInstallerButton.IsEnabled = _installerPath is not null && !blocked;

        UpdatePolicyHint();
    }

    private static bool IsTrusted(string path)
    {
        var s = AuthenticodeService.VerifyFile(path);
        return s.IsValid || s.Status == SignatureStatus.Unknown;
    }

    private void RunInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_installerPath is null) return;

        var result = SideloadService.RunLocalInstaller(_installerPath, out var signature);
        ShowOutput(InstallerOutputBorder, InstallerOutputText, result.Message);
        StatusText.Text = result.Success
            ? $"{Path.GetFileName(_installerPath)} launched ({signature.StatusBadge})."
            : $"Not launched: {result.Message}";
    }

    private void UpdatePolicyHint()
    {
        PolicyHintText.Text = SettingsService.SignaturePolicy == SignaturePolicy.BlockUnsigned
            ? "Security policy: files without a trusted signature are blocked from launching."
            : "Security policy: unsigned files launch with a warning (change this in Settings > Security).";
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void ShowOutput(Border border, TextBlock text, string message)
    {
        text.Text = message;
        border.Visibility = Visibility.Visible;
    }

    private static string FormatSize(long bytes)
    {
        double size = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}
