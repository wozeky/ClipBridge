# Black chrome reference implementation

## Reference analysis

Reference supplied by the project owner: 1600 × 1200, 30 FPS, 10.133 seconds.
Inspected full-resolution 4.5 s plus a contact sheet at 0–8 s before implementation.
The panel positions stay fixed. Most perceived motion comes from the reflective sheet
behind and through the translucent panels; the reference does not demonstrate actual
mouse hover or navigation transitions. Those interactions therefore use restrained
160–200 ms transitions instead of claiming measured timings.

Coordinates below are in the 1200 × 900 design plane (75% of source dimensions).
The design plane is uniformly scaled and centered; its panels are not stretched into
an unrelated dashboard at wide aspect ratios.

| Element | x, y | width × height | Radius |
| --- | --- | --- | --- |
| Identity and utility controls | 178, 295 | 402 × 48 | 24 |
| Source selector capsule | 178, 362 | 402 × 55 | 28 |
| Duration panel | 178, 433 | 402 × 174 | 38 |
| Capture panel | 620, 305 | 196 × 196 | 36 |
| Sharing panel | 827, 305 | 196 × 196 | 36 |
| Navigation capsule | 632, 518 | 382 × 82 | 40 |

Reference lighting moves horizontally around 0–2 s, forms a pronounced diagonal
fold at 3–6 s, then returns towards the lower center at 7–9 s. The implementation
uses a slow periodic, specularly shaded sheet; the exact fluid topology differs.
Near-black background, white metallic rims, sparse bright green signals, small
Segoe UI typography and generous outer negative space are kept. Home is the
reference composition; archive/settings use the same materials in a larger overlay
because the supplied video has no corresponding application screens.

## Actual application integration

- C#/WPF retained. No WebView, browser runtime, OBS or capture-engine rewrite.
- Existing source selection, refresh, replay durations, record start/stop, F8 save,
  import, upload/retry, clipboard, diagnostics, recovery, settings and tray handlers.
- Native custom templates for buttons, switches, text inputs and source/provider
  dropdowns; selected duration is emphasized in a horizontal strip.
- The sculpted F8 control saves a clip; the chrome puck uploads the selected clip.
  Unavailable operations are disabled. No dummy recordings or share links.
- Title bar uses WPF WindowChrome, retaining resize/drag; explicit minimize,
  maximize/restore and close-to-tray buttons remain keyboard accessible.

## Material and motion

`tools/generate_chrome.py` deterministically shades an original twisted height field
with reflected studio strips and a dark environment. It produces the bundled
960 × 720, 30 FPS, 10 s full-range BT.709 H.264 loops for home and overlay, with matching still posters.
Generation requires Python, numpy, Pillow and FFmpeg; normal .NET builds use the
committed assets and do not require these tools. No frames, audio or smart-home
content from the uploaded reference are included in the application.

Windows/WPF decodes the local video, with hardware use depending on Windows codecs
and driver support. This is a pre-rendered animated material, not a real-time fluid
simulation or a claim to recover the original 3D scene. The generator bakes optical enlargement and local blur at each measured panel region
into the animated material. `ChromeGlass` adds the dark coating, inset light and
metallic rim. Surface reflections consequently change with the background. The
home and overlay materials share the same underlying frames; navigation preserves
the current playback position. This replaces live WPF VisualBrush + BlurEffect
sampling, which measured very high CPU on the hosted display (about 91% in a short
2.2-second diagnostic sample). No blur shader or repeated video rasterization is
required per panel at runtime. If panel geometry changes, regenerate both materials.

Decorative playback and the green light pulse pause when hidden, minimized,
inactive, capture is busy, motion is disabled, or Windows disables client-area
animations. Playback stays paused on WPF software-rendering tier 0. Failed/missing
video decoding leaves the matching still material and functional controls. The
capture and upload pipeline do not depend on decorative media playback. The new
motion preference is persisted alongside existing settings; absent values in
older settings default to enabled.

The poster/loop is intentionally approximate: the source's mesh, environment map,
font and optical setup are unavailable. Pixel-identical reflections and exact 3D
objects cannot be reconstructed from this single compressed video. The recreated
metal F8 key and upload puck map the reference objects to real RollClip actions.

## Verification

Local cross-build and the existing 15 core tests are followed by Windows CI:

- WPF startup, real home/settings/library views, compact and wide aspect ratios.
- Unavailable actions disabled and existing diagnostics clipboard export.
- Full decoding of the bundled animated material.
- Required hosted media probe checks decoded pixels change and pause stops playback;
  a failed pixel-change or pause assertion fails CI.
- Existing synthetic replay/MP4 and optional hosted native capture checks.

`--smoke-test` and `--material-smoke` isolate their state in the runner temporary
folder. Native WPF screenshots and the media probe report are CI artifacts.
A successful hosted material probe is not a real-game FPS, RTX 4070, NVENC or audio
synchronization measurement. No fixed CPU/RAM or zero-impact performance claim is
made without a representative hardware benchmark.
