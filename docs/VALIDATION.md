# Validation report

This report describes actual local execution on Windows x64 with .NET SDK 10.0.401 and bundled runtime 10.0.12. It is not a guarantee of perfect output or universal compatibility. No Python interpreter was executed, no external sample photos were downloaded, and no private photo was sent to an external AI service.

## Executed checks

| Check | Result and evidence scope |
| --- | --- |
| C# build | Application builds with zero compiler/analyzer warnings; NuGet versions and Windows RID are locked |
| Unit/media/API/reference/real-model checks | Local TRX reports; includes camera bounds/face safety, concepts, dedup/order/duration, all grades/transitions, local MP4 clip replacement, JSON/NPY, import failure/retry, upload/edit/render/Range/download, invalid inputs, duplicate start, queued/running cancel, encoder failure/retry and restart recovery |
| Image formats/orientation | Generated JPEG/PNG/WebP/BMP/TIFF/HEIC/HEIF decode; EXIF orientations 1–8 check actual colored corner positions, dimensions and timestamp |
| Actual model inference | CLIP vision + text produces 512-dimensional normalized embedding; actual YuNet detector executes; actual Depth Anything output is 518×392 with finite percentile-normalized 0–1 values; separate independent review also checked portrait and square depth inputs |
| Tokenization | 29/29 against official independent JavaScript tokenizer, including label prompts, Unicode/NFC/case/numbers, special tokens and length truncation |
| Stored Python baseline comparison | Complete storyboard JSON exactly equals stored fallback-mode reference; same ten bitmaps match sharpness/brightness/warmth, embedding cosine ≥0.9999999; maximum saliency difference 0.00001. Python was not rerun; baseline had CLIP disabled |
| Windows camera-photo evaluation | 19 permitted date-named originals analyzed from copies; 3 same-date groups totaling 8 photos exported to 4 aspect ratios × edit/parallax (24 outputs) plus a 1080×1920/30fps output. Every output passed full FFmpeg decode. Positive YuNet detections occurred; this is not annotated face-detection accuracy evaluation |
| Private-data preservation | All 19 original SHA-256 hashes unchanged after testing. Copies, EXIF, filenames/source paths, proxies, thumbnails, reports with IDs and all videos remain excluded from Git |
| Self-contained publish | Windows x64 executable includes .NET/ASP.NET Core 10.0.12; setup verifies models, font, Haar and FFmpeg hashes; published tree contains zero Python files |
| Clean PATH/browser | PATH contains only Windows System32; neither Python nor dotnet is discoverable. Published executable passes actual Edge upload with CLIP, inclusion/exclusion/reorder/motion edits, playback, download/Range, reload, HTTP400-save blocking, changed-concept replan and cancellation with zero JavaScript errors |
| Independent-review regressions | Style changes retain current title/aspect; all editor inputs lock during final save/start; data root does not depend on assets_cache; import failure leaves no target; first project response after forced process kill/restart includes interrupted history and preserved photos/plan |

## Reproduction

Use the README commands for the unit/media/API suite, real-model category and `--probe`. To reproduce browser checks, install Node and Edge, run `scripts/setup-test-tools.ps1`, run the probe to create synthetic browser fixtures, then start an isolated server and execute `node scripts/browser-checks.cjs`. `scripts/clean-publish-check.ps1` runs the published executable with a clean PATH, performs that browser flow, kills only its test server, restarts it and checks the first restored snapshot. It stops its test server when finished.

Reference/PrivatePhotos categories require the explicitly permitted local evidence corpus and are opt-in: private inputs and reference execution logs are not distributed. Full local raw evidence is retained separately for independent review; only this nonidentifying report is prepared for the source repository.

## Limits and observed differences

- Local photo groups were chosen from camera timestamps and local analysis. Travel destinations/content were not verified by external AI or manually annotated. Their aesthetic quality, preservation of recognizable people and accompaniment listening quality remain a human review task. Positive face detections do not establish precision/recall.
- HEIC/HEIF tests use generated native-codec fixtures; real-camera HEIC files were not present among the selected Windows photographs. Broader camera/color-profile/codec coverage remains unverified.
- One observed full-size 5.4-second export took about 26 seconds. Performance depends on photos, codec, output size and engine; reduced-resolution aspect coverage is separate from the full-size test. Only one full-size aspect was exported in the private corpus.
- The self-contained application was exercised on this Windows installation with an isolated PATH; an entirely fresh Windows VM and Linux/macOS packages were not tested. The selected native runtime package targets Windows.
- Music/noise and film grain use .NET seeded Random/Box–Muller rather than NumPy PCG64. Encoded videos, title rasterization and all noise samples are not byte-identical to Python.
- Concept rules preserve original negation limitations, and depth boundaries can stretch pixels. generate/stylize remain unavailable placeholders.
- Independent review identified five reproducible defects and a missing clip extension/installation gap; these were corrected with regression checks. A focused independent review of the final changes is required before remote publication.

Final combined local suite: **196 passed, 0 failed, 0 skipped**. The short-lock atomic JSON regression was separately rechecked after removing its test-only blocking-wait analyzer warning. Raw evidence remains local; [sanitized summary](validation-summary.json) contains no personal paths, photo names, EXIF or project IDs.
