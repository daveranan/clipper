# Canvas Viewer Plan

> Implementation status (2026-08-24): core frontend work is complete in `src/App.tsx`, `src/index.css`, and `src/viewerGeometry.ts`; geometry tests are in `tests/viewerGeometry.test.ts`. Automated tests and the production build pass. The Tauri/WebView2 manual acceptance matrix still requires testing with real 3440x1440 and 4K clips.

## Status and product decision

Phase 1 (the first canvas surface) is already implemented in `src/App.tsx` and documented in `docs/R1.md`. The remaining work should treat that implementation as a baseline, not as the final rendering architecture.

The viewer optimizes for this order:

1. Smooth interaction and playback.
2. Correct frame, crop, and output geometry.
3. The highest useful visible quality.
4. Full-source-resolution backing buffers only when they materially improve the visible result.

QuickClipper does **not** need to keep and repaint a source-sized 4K canvas on every video frame. It needs to decode the native source and render the visible viewport sharply. A viewport-sized, device-pixel-aware canvas gives the same visible detail at normal viewing sizes with much less memory bandwidth. When the user zooms to Actual Size, the canvas renders the visible source pixels at 1:1 CSS scale; it still does not need an off-screen bitmap of the entire source.

The primary expected source is up to 3440x1440, with 3840x2160 supported as a normal case. Preview-cache generation is limited to the first 180 seconds because recordings are expected to be two to three minutes. Longer clips must still play and seek, but may fall back to the decoded video outside the cache range.

## Goal

Build a responsive, high-fidelity canvas viewer with:

- real-time video playback;
- immediate scrub feedback followed by a full-quality settled frame;
- cursor-anchored zoom and reliable panning;
- distinct Fit and Actual Size commands;
- predictable behavior when the app window or viewer is resized;
- pixel-accurate crop editing at every zoom level;
- output resize controls that cannot be silently ignored; and
- no effect from view navigation on exported crop, resize, trim, or cut values.

## Non-negotiable invariants

1. **View state is not edit state.** Zoom and pan never enter `EditSnapshot`, undo/redo, export requests, or saved project/edit settings.
2. **Source pixels are the editing coordinate system.** Crop values remain source-pixel values regardless of viewport size, device pixel ratio, zoom, pan, playback state, or preview source.
3. **The canvas and crop overlay use one transform.** There must be one tested source-to-viewport mapping; do not independently reproduce contain-fit math in multiple components.
4. **No stale frame may win a race.** A cache image, seek completion, or exact-frame extraction for an old time or clip must not replace the current frame.
5. **Interactive work wins over refinement.** Scrubbing, zooming, panning, resizing, and playback must remain responsive; full-quality refinement may happen after interaction settles.
6. **Export remains authoritative.** The viewer may use proxies, but export always uses the original source and the current crop/output values.

## Terminology and coordinate systems

- **Source space:** video pixels. Crop data lives here.
- **Viewport space:** CSS pixels inside `.viewer`.
- **Backing space:** physical canvas pixels. Its size is derived from viewport size and render DPR.
- **Scale:** CSS pixels per source pixel.
- **Fit scale:** `min(viewportWidth / sourceWidth, viewportHeight / sourceHeight)`.
- **Actual Size / 100%:** `scale = 1`; one source pixel maps to one CSS pixel. OS display scaling still determines physical monitor pixels.
- **Pan:** viewport-space translation of the source origin.
- **Fit:** use `fitScale` and center the source. Fit is often less than 100% for 3440x1440 or 4K media.
- **Reset View:** identical to Fit and centered. `Ctrl+0` invokes it.

This definition intentionally does not call a fitted 4K image “100%.” The zoom readout displays the effective scale, so a fitted large source may read `31%`; Actual Size always reads `100%`.

## Requirements

| ID | Requirement |
|---|---|
| R1 | A single visible canvas renders playback and paused frames; hidden media elements remain decode/image sources only. |
| R2 | Playback is synchronized to decoded video frames and does not continuously redraw when no new frame exists. |
| R3 | Scrubbing shows an immediate proxy frame, then replaces it with a current full-quality frame after interaction settles. |
| R4 | Wheel/trackpad zoom is cursor-anchored; toolbar and keyboard zoom are viewport-center anchored. |
| R5 | The toolbar provides Zoom Out, zoom percentage, Zoom In, Fit, and Actual Size. |
| R6 | `Ctrl+-`, `Ctrl+=`/`Ctrl++`, `Ctrl+0`, and `Ctrl+1` perform Zoom Out, Zoom In, Fit/Reset, and Actual Size. |
| R7 | Middle-button drag pans and suppresses Chromium auto-scroll without starting a crop edit. |
| R8 | Fit and Actual Size remain stable and centered across viewer resize; a custom view preserves the source point at the viewport center. |
| R9 | Crop movement and handles remain source-pixel accurate, with constant-size on-screen borders and hit targets. |
| R10 | Manual output width/height changes disable Auto 1280x720 and are reflected in the export request. |
| R11 | 3440x1440 and 3840x2160 sources play smoothly on the target machine without requiring a full-source canvas backing store. |
| R12 | Loading a new clip cancels old preview work and resets the view to Fit without altering the new clip's edits. |

## User experience contract

### Toolbar

Add a compact control group to `.viewer-toolbar`:

`[−] [ 31% ▾ ] [+] [Fit] [1:1]`

- **− / +:** multiply scale by `1 / 1.25` or `1.25`, anchored at viewport center.
- **Percentage:** live effective scale rounded to a whole percent below 200%, then to a sensible compact value. Clicking it may expose presets: 25%, 50%, 100%, 200%, 400%, Fit.
- **Fit:** fit and center; this is also Reset View.
- **1:1:** Actual Size (100%) and center.
- Disable all controls when no video dimensions are known.
- Tooltips must include shortcuts and unambiguous labels: “Fit / Reset View (Ctrl+0)” and “Actual Size (Ctrl+1).”

### Pointer and keyboard behavior

- Wheel anywhere over `.viewer` zooms about the pointer. Use a native non-passive `wheel` listener and call `preventDefault()`.
- Normalize wheel deltas and use a smooth exponential factor, then clamp the factor per event so unusual mouse drivers cannot jump from minimum to maximum zoom.
- Middle-pointer down starts pan, captures the pointer, prevents default, and changes the cursor to `grabbing`.
- Crop drag starts only for the primary button. A middle click on a crop handle or crop interior must bubble to viewer pan.
- Keyboard shortcuts are ignored while focus is in an input, textarea, select, contenteditable element, or modal/settings surface.
- Prevent WebView2 page zoom for handled `Ctrl`/`Cmd` zoom shortcuts.
- `Ctrl+0` always means Fit/Reset, not “set an internal multiplier to 1.”
- `Ctrl+1` always means Actual Size.

### View modes

Track `viewerView.mode` as `'fit' | 'actual' | 'custom'` plus `scale`, `offsetX`, and `offsetY`.

- New clip: Fit.
- Fit command: `mode = 'fit'`, recompute scale, center.
- Actual Size command: `mode = 'actual'`, `scale = 1`, center.
- Any zoom or pan gesture: `mode = 'custom'`.
- Viewer resize in Fit: recompute fit and center.
- Viewer resize in Actual: keep scale 1 and center.
- Viewer resize in Custom: preserve the source coordinate that was under the old viewport center and place it under the new viewport center.

Do not reset a custom view merely because a toolbar, inspector, or timeline resize changes `.viewer` dimensions.

### Zoom math

The source-to-viewport mapping is:

```text
viewportX = offsetX + sourceX * scale
viewportY = offsetY + sourceY * scale
```

For a zoom anchor `p` in viewport space:

```text
sourceAtAnchor = (p - offset) / oldScale
newOffset = p - sourceAtAnchor * newScale
```

Use the same helper for wheel, keyboard, buttons, and preset selection.

Dynamic limits must always include Fit and Actual Size:

```text
minScale = min(0.01, fitScale)
maxScale = max(32, fitScale)
```

This normally exposes 1%-3200% while still allowing an unusually tiny source to Fit. Do not allow `NaN`, infinity, zero-sized viewports, or unknown media dimensions into transform state.

## Rendering architecture

### DOM structure

Use a viewport-sized canvas. Do not transform a native-sized canvas element and an independent full-size crop layer.

```text
.viewer                         viewport; overflow hidden
├─ video.viewer-video           hidden decode/audio source
├─ img.viewer-still             hidden proxy/exact-frame source
├─ canvas.viewer-canvas         fills viewport; backing uses render DPR
├─ CropOverlay                  viewport-space sibling, not CSS-scaled
└─ .empty-viewer                shown only with no drawable source
```

The render function receives the shared source-to-viewport transform and draws the source into the viewport canvas. `CropOverlay` receives that same transform and maps its source-pixel crop rect into viewport space. Keeping the overlay outside a CSS-scaled scene makes its 2px border and 10-12px handles remain usable at 1% and 3200%.

### Canvas backing size

On viewer resize:

```text
renderDpr = min(window.devicePixelRatio || 1, 2)
backingWidth = round(viewportCssWidth * renderDpr)
backingHeight = round(viewportCssHeight * renderDpr)
```

Then proportionally reduce `renderDpr` if the backing surface would exceed the preview pixel budget. Start with an 8.3-megapixel budget (4K-equivalent) and measure on the target machine. This cap applies to the **visible canvas**, not to source decoding or export resolution.

Why this policy:

- 3440x1440 is about 5.0 million pixels and 3840x2160 is about 8.3 million pixels.
- One 4K RGBA bitmap is about 31.6 MiB. One 8K bitmap is about 126.6 MiB, not 33 MiB, and the decoder/compositor may hold additional copies.
- A typical embedded viewer is much smaller than the source. Repainting a full native canvas at 30/60 fps wastes memory bandwidth without improving visible quality.
- A viewport backing at device-pixel density provides sharp textural detail and avoids blurry high-DPI output.

Set `context.imageSmoothingEnabled = true` and `context.imageSmoothingQuality = 'high'` for downscaling. Do not use CSS `image-rendering: pixelated` for video.

### Draw only the visible result

Each paint must:

1. Reset the context transform.
2. Clear the full backing surface.
3. Apply render DPR.
4. Apply the source-to-viewport translation and scale.
5. Draw the current video/image source.

Canvas clipping naturally prevents pixels outside the viewport from becoming visible. An optimization may calculate a source sub-rectangle for `drawImage`, but only after correctness is proven; the browser can clip the first version.

### Playback loop

Prefer `HTMLVideoElement.requestVideoFrameCallback` when available. It schedules one canvas paint per presented decoded frame and avoids duplicate `requestAnimationFrame` paints. Fall back to rAF only when the API is unavailable.

- Start the frame callback loop when playback begins.
- Stop/cancel it when playback pauses, the clip changes, or the component unmounts.
- Zoom/pan/resize may request an extra repaint of the most recent frame.
- Do not call React state setters for every presented frame. Keep current transform and draw inputs in refs where needed.
- The existing playback clock may continue updating UI time independently; canvas painting follows decoded frames.

### Paused and scrubbing quality ladder

Use a two-stage policy:

1. **Immediate interaction frame:** while timeline dragging or C-scrubbing, show the nearest 1280-wide cache image as soon as it loads. Keep the last valid frame while the next proxy loads; never flash the background.
2. **Settled full-quality frame:** seek the hidden native playback video in parallel. When interaction stops and the seek settles, paint its current native decoded frame. If exact-frame extraction is requested, replace it only when the extraction token still matches the current clip and target time.

The settled-frame tolerance must be tied to frame duration rather than a fixed 100 ms:

```text
tolerance = max(1 / effectiveFps, 0.010)
```

If exact FPS is unavailable, use the configured preview/export FPS as a fallback. Never accept a settled video frame merely because it is within 0.1 seconds; that can be several frames wrong.

### Stale-result protection

Maintain a monotonically increasing `clipEpoch` and `frameRequestId`.

- Increment both when loading a clip.
- Increment `frameRequestId` for every requested preview time.
- Capture `{clipEpoch, frameRequestId, targetTime}` in image loads, video seek completion, and exact-frame promises.
- Paint or commit only if all captured values still match current refs.

This rule covers rapid scrub direction changes, a clip switch during ffmpeg extraction, and a late image `onLoad`.

### Preview-cache scope

- Keep the current cache width of 1280 for fast interaction unless measurement proves it visibly inadequate.
- Cache at no more than 12 fps and no more than 180 seconds.
- Cache generation must stay off the UI thread and may complete progressively if the backend supports it later.
- Outside the cached time range, seek the hidden video and keep the last valid frame until it settles; do not show an unrelated last cache frame.
- Clip duration does not increase canvas memory, but it does increase cache generation time and disk use. The 180-second cap is therefore a cache/performance decision, not a playback limit.

## Crop and output resize correctness

### Crop overlay

Replace `imageDisplayBounds`/duplicated contain math with shared pure helpers:

- `sourceToViewport(point, view)`
- `viewportToSource(point, view)`
- `sourceRectToViewport(crop, view)`
- `zoomAt(view, viewportPoint, nextScale)`
- `fitView(viewportSize, sourceSize)`

Crop pointer movement converts both the starting and current client points through `viewportToSource`, then computes the source-pixel delta. This is more robust than dividing rounded client deltas by several independently calculated scales.

- Round/clamp only when committing crop values; retain enough precision during the gesture to avoid sticky one-pixel movement at low zoom.
- Keep source bounds, minimum 8x8 crop, and even output-compatible values.
- Handle `pointercancel` and `lostpointercapture` exactly like pointer-up so edit history cannot remain open.
- Starting a crop drag calls `onBeginEdit` once; completion/cancel calls `onEndEdit` once.

### Output resize semantics

Crop selects source content. Output W/H scales that cropped content during export. Viewer zoom changes neither.

The current code has a dangerous interaction: after Auto 1280x720 is enabled, manually changing W or H does not clear `autoFit720`, so the Rust export path can ignore the entered manual dimensions. Fix this as part of the canvas work because it is required for the “resize still works” invariant.

- Editing either manual W or H sets `autoFit720 = false` in the same edit transaction.
- Clamp dimensions to valid positive even values and an encoder-supported maximum.
- Auto 1280x720 sets the 16:9 crop, output 1280x720, and `autoFit720 = true` as one undoable edit.
- Undo/redo restores W, H, crop, and `autoFit720` together.
- Display an output summary such as `Crop 2560x1440 -> Output 1280x720`.
- If manual output aspect differs from crop aspect, label that the export will stretch. A later aspect-lock feature may prevent distortion, but silently changing crop or dimensions is out of scope.
- Export verification must inspect the produced file dimensions, not only the request object.

The main editing viewer continues to show the source plus crop overlay. A WYSIWYG “Output Preview” mode would require rendering only the crop and simulating output aspect/scale; treat that as a separate feature rather than mixing it into view zoom.

## Implementation phases

### Phase 0 - Lock behavior with pure geometry

- Add the view types and shared coordinate helpers.
- Add tests for Fit, Actual Size, cursor-anchored zoom, center preservation on resize, source/viewport round trips, crop rect mapping, and scale limits.
- Record baseline playback behavior for 1920x1080, 3440x1440, and 3840x2160 clips.

Exit criteria: geometry tests cover fractional viewport sizes and portrait/landscape sources; no DOM changes yet.

### Phase 1 - Refactor the canvas to viewport rendering

- Keep the existing hidden video/image sources and source-priority behavior from `docs/R1.md`.
- Make the visible canvas fill `.viewer` and size its backing store by viewport x render DPR.
- Draw through the shared transform; start in Fit mode.
- Remove the source-sized canvas-buffer assumption and its 16384-per-side allocation rule.

Exit criteria: the current viewer looks the same in Fit, uses bounded viewport memory, and survives repeated window resize without stretching or blank frames.

### Phase 2 - Frame scheduling and preview refinement

- Use `requestVideoFrameCallback` with rAF fallback.
- Implement cache-immediate/native-settled behavior.
- Add clip/time request tokens and frame-duration-based seek validation.
- Retain the last valid frame during transitions.

Exit criteria: playback, frame stepping, timeline seeking, C-scrub, and exact-frame display stay current; no old frame appears after rapid seeks or clip changes.

### Phase 3 - Zoom, pan, and resize behavior

- Add view state, native non-passive wheel handling, middle-pointer capture, dynamic limits, and resize-mode behavior.
- Ensure all listeners/callbacks clean up on unmount.
- Repaint from refs during gestures so pointer movement does not require a full React render per event.

Exit criteria: cursor anchor is stable within 0.5 CSS px; middle-pan never edits crop; Fit/Actual/Custom respond to viewer resize as specified.

### Phase 4 - Toolbar and keyboard UX

- Add Zoom Out, percentage/presets, Zoom In, Fit, and 1:1 controls.
- Extend the existing key handler with editable-target guards and WebView2 default prevention.
- Add disabled, hover, focus-visible, and accessible-label states.

Exit criteria: mouse, keyboard, and toolbar paths call the same view helpers and produce identical transforms.

### Phase 5 - Crop and output resize hardening

- Move CropOverlay to viewport mapping with constant-size handles.
- Add primary-button, pointer-cancel, and lost-capture handling.
- Disable Auto 1280x720 on manual W/H edits and verify actual exported dimensions.

Exit criteria: crop and resize acceptance matrix passes and export values are unchanged by navigation.

### Phase 6 - Performance tuning

- Measure rather than infer. Capture frame cadence, long tasks, canvas allocation size, seek-to-proxy latency, and seek-to-native latency.
- Tune render DPR cap and pixel budget on the target 3440x1440 workstation.
- Only add source-subrectangle drawing or interaction-time DPR reduction if measurements show a need.

Exit criteria: meets the budgets below without reducing correctness.

## Performance and quality budgets

These are initial targets for the primary development machine and should be recorded in the verification notes:

| Scenario | Target |
|---|---|
| 3440x1440 source playback | No sustained visible frame dropping caused by canvas painting. |
| 3840x2160 source playback | Smooth at source cadence when the decoder can sustain it; canvas adds no continuous duplicate paints. |
| Zoom/pan input | Visual response in the next animation frame; no React rerender required per pointer event. |
| Cached scrub response | First relevant proxy frame visible within 100 ms when cached/on disk. |
| Settled preview | Native decoded or exact frame replaces proxy as soon as seek/extraction completes; target under 300 ms on local media. |
| Idle paused viewer | Zero continuous animation loop and negligible CPU use. |
| Visible canvas allocation | At most 8.3 million backing pixels by default. |
| Frame correctness | Settled frame within one effective frame duration of requested time. |

If native 4K playback misses cadence, reduce interaction/playback render DPR before reducing source decode quality. Restore full render DPR for a paused settled frame after roughly 150 ms without view interaction. This adaptive step is optional and should be enabled only when profiling demonstrates a benefit.

## Acceptance matrix

Run `npm run build` and `npm run lint`, then test in the Tauri/WebView2 app.

### Sources

- 1920x1080 landscape.
- 3440x1440 ultrawide (primary case).
- 3840x2160 4K.
- Portrait source.
- A clip longer than 180 seconds to verify cache fallback.

### View navigation

- Initial load is Fit and centered; readout shows effective percentage.
- Fit after arbitrary pan/zoom returns to fit and center.
- Actual Size reads 100% and centers.
- `Ctrl+0` matches Fit; `Ctrl+1` matches Actual Size.
- Wheel zoom preserves the source pixel under the cursor.
- Button/keyboard zoom preserves the source pixel under viewport center.
- Middle-pan works over canvas, crop body, and handles without changing crop.
- Fit, Actual, and Custom behave correctly after window resize, inspector resize, and timeline resize.
- Extreme wheel input, zero-size transition, and repeated resize never produce blank/NaN transforms.

### Preview and playback

- Play/pause and audio remain synchronized.
- Canvas paints once per decoded frame, not continuously above source cadence.
- Timeline drag and C-scrub respond immediately with cache frames.
- Releasing a scrub resolves to the requested full-quality frame.
- Frame stepping lands on the correct adjacent frame.
- Rapid back-and-forth scrubbing never displays a late frame from the abandoned time.
- Switching clips during a seek/extraction never shows the previous clip.
- Paused idle CPU returns near baseline.

### Crop and resize

- Move and all four handles at Fit, 25%, 100%, 200%, and 400%.
- Inspector crop values match source pixels and stay within bounds.
- Handle size stays usable at every zoom.
- Viewer navigation creates no undo entry and changes no edit value.
- Manual W/H after Auto 1280x720 disables Auto and exports the entered even dimensions.
- Undo/redo across Auto and manual resize restores consistent crop/W/H/Auto state.
- Exported files are probed to confirm expected width and height.
- 1280x720 Auto still crops to 16:9 and exports exactly 1280x720.

## Known risks and mitigations

- **WebView2 page zoom:** intercept only the viewer shortcuts and call `preventDefault()`.
- **Passive wheel listeners:** attach directly to `.viewer` with `{ passive: false }` and remove the same listener on cleanup.
- **Media source hidden with `display: none`:** keep the video mounted, tiny/offscreen, opacity zero, and pointer-inert so Chromium continues decoding frames.
- **High-DPI memory growth:** cap render DPR at 2 and total backing pixels at the measured budget.
- **Proxy softness during interaction:** acceptable for responsiveness; automatically replace it with a full-quality settled frame.
- **Exact-frame latency:** retain the last relevant frame and protect completion with request tokens.
- **Crop mismatch from duplicated math:** eliminate independent contain calculations; use shared transforms.
- **Rotated/non-square-pixel media:** verify that `VideoInfo`, decoded display dimensions, crop coordinates, and ffmpeg export share the same orientation/basis. If they differ, normalize metadata before enabling crop rather than guessing.
- **Manual resize ignored by Auto mode:** explicitly disable Auto when either manual dimension changes.

## Out of scope

- Changes to export codec quality or source recording quality.
- Timeline zoom behavior.
- Recording-region selector behavior.
- A dedicated output/WYSIWYG preview mode.
- Persisting viewer zoom/pan across clips or launches.
- Supporting cache generation beyond 180 seconds as a performance goal.
