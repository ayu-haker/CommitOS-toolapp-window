param(
    [string]$Tag = "v2.9.0",
    [string]$AssetsDir = "release_assets",
    [string]$OutputFile = "release_notes.md"
)

$ErrorActionPreference = "Stop"

function Get-AssetHash {
    param([string]$Pattern)
    $f = Get-ChildItem -Path $AssetsDir -Filter $Pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($f) { return (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLower() }
    return "N/A"
}

$setupFile = Get-ChildItem -Path $AssetsDir -Filter "*_x64_Setup.exe" -File -ErrorAction SilentlyContinue | Select-Object -First 1
$zipFile   = Get-ChildItem -Path $AssetsDir -Filter "*_x64_portable.zip" -File -ErrorAction SilentlyContinue | Select-Object -First 1

$setupHash = if ($setupFile) { (Get-FileHash -Path $setupFile.FullName -Algorithm SHA256).Hash.ToLower() } else { "N/A" }
$zipHash   = if ($zipFile)   { (Get-FileHash -Path $zipFile.FullName   -Algorithm SHA256).Hash.ToLower() } else { "N/A" }

$setupName = if ($setupFile) { $setupFile.Name } else { "DevOpsToolsInstaller_<version>_x64_Setup.exe" }
$zipName   = if ($zipFile)   { $zipFile.Name }   else { "DevOpsToolsInstaller_<version>_x64_portable.zip" }

if (Test-Path $AssetsDir) {
    $sums = @()
    foreach ($f in (Get-ChildItem -Path $AssetsDir -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ne "SHA256SUMS.txt" -and $_.Extension -in ".exe", ".zip", ".msi" })) {
        $h = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLower()
        $sums += "$h  $($f.Name)"
    }
    if ($sums.Count -gt 0) {
        # LF, no BOM: the app hashes catalog files byte-for-byte, and this file is
        # published next to them.
        [System.IO.File]::WriteAllText("$AssetsDir\SHA256SUMS.txt", ($sums -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
        Write-Host "Wrote SHA256SUMS.txt ($($sums.Count) entries)"
    }
}

$template = @'
# DevOps Tools Installer __TAG__

__TAG__ of **DevOps Tools Installer** - the fork maintained by [@ayu-haker](https://github.com/ayu-haker).

---

## What's new in this release

### Sideload: APKs and local installers
A new **Sideload** page in the nav, next to Downloads:

- **Android (ADB)**: install an `.apk` onto a connected device, with optional
  downgrade (`-d`) and grant-all-permissions (`-g`) flags.
- **Device picker**: lists attached devices and their state (`device` /
  `offline` / `unauthorized`) from `adb devices`.
- **Local installers**: pick any `.exe` or `.msi` from disk. The app computes its
  SHA-256 and checks its Authenticode signature before launching, and the
  signature verdict respects your Settings policy (`WarnUnsigned` /
  `BlockUnsigned`).
- **adb discovery**: prefers the `adb` the app installed itself, falls back to
  one already on `PATH`, and links straight to the catalog entry if it is
  missing.

### Catalog signing moved to this fork
The catalog (`catalog.json`, `bundles.json`) and bundles are now signed and
served from this repository, so remote catalog updates verify against a key
pinned in the app rather than against the upstream publisher's key.

### New catalog entries
`adb` (Android Debug Bridge 37.0.1) and `scrcpy` 4.1, in a new
**Mobile & Android** category and a new `android-sideload-kit` bundle.

### This fork's identity
About and Settings now point at this repository and its maintainer. In-app
update checks resolve against this fork instead of upstream.

---

## Verification & checksums

| Asset | Type | SHA256 |
| :--- | :--- | :--- |
| **`__SETUP_NAME__`** | Inno Setup installer | `__SETUP_HASH__` |
| **`__ZIP_NAME__`** | Portable folder build (zip) | `__ZIP_HASH__` |

The same checksums are in `SHA256SUMS.txt`.

---

## Quick start

- **Option A (Setup Wizard)** - download and run `__SETUP_NAME__`. Installs to
  `C:\Program Files\DevOpsToolsInstaller` with shortcuts and PATH integration.
- **Option B (Portable)** - download `__ZIP_NAME__`, extract it anywhere, and run
  `DevOpsToolsInstaller.exe`. No installation, no admin rights.
- **Option C (WinGet)** - the package ID `NotHarshhaa.DevOpsToolsInstaller` in the
  WinGet community repository still resolves to **upstream**, so it does not yet
  ship this fork's features. Download the installer above instead.

### x64 only
WinUI 3 apps cannot be single-file published, so this project ships a folder
build wrapped in an installer or a zip. There is no standalone `.exe` and no
`.msi` for this release, and no arm64 build yet.

---

## Not verified on real hardware

APK installation has **not** been exercised against a physical Android device:
there was no device available during the build. Device discovery, enumeration,
and adb's error output *were* tested against real adb 37.0.1, so the code path
up to the install command is verified, but a successful `adb install` is not.

The local `.exe` / `.msi` flow was verified for hashing and signature evaluation
(SHA-256 grouping, and `Status: Valid` for a Microsoft-signed binary). The
file-picker round trip was not driven end to end in the UI.

Please report anything that misbehaves on real hardware.

---

## Credits

Originally created by [Harshhaa](https://github.com/NotHarshhaa). This fork
builds on that work under Apache-2.0.
'@

$content = $template.Replace("__TAG__", $Tag)
$content = $content.Replace("__SETUP_NAME__", $setupName)
$content = $content.Replace("__SETUP_HASH__", $setupHash)
$content = $content.Replace("__ZIP_NAME__", $zipName)
$content = $content.Replace("__ZIP_HASH__", $zipHash)

if ([System.IO.Path]::IsPathRooted($OutputFile)) {
    $resolved = $OutputFile
} else {
    $resolved = Join-Path (Get-Location).Path $OutputFile
}
$parent = [System.IO.Path]::GetDirectoryName($resolved)
if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
[System.IO.File]::WriteAllText($resolved, $content, [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated release notes at $resolved"
