# QuickClipper

Native Windows tray app for quick screen snippets.

Run:

```powershell
dotnet run --project .\QuickClipper\QuickClipper.csproj
```

Use `Win+Shift+R` to select a screen region, then press it again to stop recording. Use `Win+Shift+4` to reset the current recording. After recording, trim the clip, adjust crop/resize values, export to the configured folder, and use `Copy File` to place the exported file on the clipboard.

Hotkeys can be changed in the Options section of the app settings.

Requires `ffmpeg.exe`. Either put FFmpeg on `PATH` or set the path in the app settings.

Release/update flow:

```powershell
git push origin master
git tag v0.1.0
git push origin master --tags
```

GitHub Actions builds `master` as a validation check. Tags named `v*` package the app with Velopack and publish a GitHub Release with `QuickClipperSetup.exe` plus update feed files.

The release build embeds `https://github.com/daveranan/clipper` as the update source. The first install must come from the GitHub Release setup exe; auto-update is not active when running from `dotnet run` or `bin`.
