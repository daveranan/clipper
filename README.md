# QuickClipper

QuickClipper is now a Tauri + React desktop clip editor. The old WPF app has been removed from the repo; the Tauri app at the repository root is the default build, release, and update target.

## Local development

```powershell
npm ci
npm run tauri dev
```

## Build

```powershell
npm run build
npm run tauri build
```

Installer output:

```text
src-tauri\target\release\bundle\nsis\QuickClipper_0.1.0_x64-setup.exe
src-tauri\target\release\bundle\msi\QuickClipper_0.1.0_x64_en-US.msi
```

Desktop helper shortcut:

```text
C:\Users\David\Desktop\Build Install Launch QuickClipper.lnk
```

The shortcut runs `scripts\BuildInstallLaunch.ps1`, builds the Tauri app, writes `latest.json`, opens the bundle folder, runs the installer, and launches QuickClipper.

## GitHub releases and updates

The release workflow builds signed Tauri installers and publishes updater artifacts to GitHub Releases. Configure these GitHub secrets:

```powershell
TAURI_SIGNING_PRIVATE_KEY
TAURI_SIGNING_PRIVATE_KEY_PASSWORD
```

Create a tag like `v0.1.0` or run the `Release` workflow manually. The workflow uploads NSIS/MSI installers, signatures, and `latest.json`.

Existing Tauri installs can auto-update from:

```text
https://github.com/daveranan/clipper/releases/latest/download/latest.json
```

Existing WPF/Velopack installs will not auto-update from Tauri's `latest.json`. Those users need a one-time WPF/Velopack bridge release that launches the new Tauri installer, or they need to install the new Tauri build manually once.
