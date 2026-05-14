# QuickClipper

Native Windows tray app for quick screen snippets.

Run:

```powershell
dotnet run --project .\QuickClipper\QuickClipper.csproj
```

Use `Ctrl+Shift+R` to select a screen region, then press it again to stop recording. After recording, trim the clip, adjust crop/resize values, export to the configured folder, and use `Copy File` to place the exported file on the clipboard.

Requires `ffmpeg.exe`. Either put FFmpeg on `PATH` or set the path in the app settings.

Release/update flow:

```powershell
git tag v0.1.0
git push origin master --tags
```

GitHub Actions builds the tag, packages it with Velopack, and publishes a GitHub Release with `QuickClipperSetup.exe` plus update feed files. In QuickClipper settings, set `GitHub repo URL` to your repo URL, for example `https://github.com/OWNER/REPO`; installed Velopack builds check that release feed on startup and from the tray `Check for Updates` item.

The workflow only creates releases from `v*` tags. The first install must come from the GitHub Release setup exe; auto-update is not active when running from `dotnet run` or `bin`.
