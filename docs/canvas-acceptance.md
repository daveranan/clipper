# Canvas Acceptance Status

Last checked: 2026-09-02

Run the repeatable portion with:

```text
npm run test:acceptance
npm run build
npm run lint
```

## Automated and passing

- Scrubbing chooses the nearest cached preview frame and rejects out-of-cache indexes.
- A settled native video frame must be within one effective frame duration of the requested time.
- Exact-frame completions are rejected after a clip, request, or target-time change.
- Frame stepping advances by one timeline frame, crosses removed source ranges correctly, and clamps at the first and last frame.
- Crop movement maps to the same source-pixel delta at 25%, 100%, 200%, and 400% zoom.
- Crop movement and all four handles remain bounded, at least 8x8, and use the even coordinates/dimensions required by export.
- Manual output dimensions with Auto disabled produce the requested FFmpeg scale filter.
- Auto 1280x720 produces the expected crop/scale filter.
- FFmpeg artifacts for manual and automatic resize are probed with FFprobe and match 854x480 and 1280x720 respectively.

## Still requires Tauri/WebView2 hands-on testing

- Timeline drag and C-scrub show a relevant cache frame immediately and settle to the native/exact frame without a flash.
- Rapid back-and-forth seeking and switching clips during extraction never display an abandoned frame.
- Previous/next frame controls visibly land on the adjacent frame for representative 30 and 60 fps clips.
- Crop body and all handles feel correct at 25%, 100%, 200%, and 400%; middle-drag pans without changing crop; pointer cancellation closes the edit cleanly.
- A complete export initiated from the UI retains crop/resize values after zoom, pan, Fit, Actual Size, undo/redo, and navigation.
- Editing W or H in the UI visibly disables Auto 1280x720 before that complete export.
