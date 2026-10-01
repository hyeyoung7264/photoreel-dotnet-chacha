# C#/.NET migration

The source baseline is `hyeyoung7264/photoreel` at `c02e6eed0d488a5d8fd8dac5adcbb1842bbb09e8`. Its executable code is the same as the earlier `db85e855dfed6a0e1d363beab3fbba083030b604` analysis baseline; the later commit updates a work record. Original source/data/services were preserved. This implementation uses ASP.NET Core, C# and native ONNX Runtime, OpenCV, ImageMagick and FFmpeg, with no Python build/install/runtime bridge.

## Preserved contracts and functions

| Area | C# implementation | Compatibility |
| --- | --- | --- |
| RGB decode, EXIF orientation/time, proxy/thumbnail | Images, Analyzer | JPEG/PNG/WebP/BMP/TIFF/HEIC/HEIF; orientations 1–8 tested |
| CLIP scene/light/scale classification and embeddings | VisionModels, ClipTokenizer, Bicubic | RGB bicubic resize/crop 224; original mean/std; NCHW float32; L2; tokenizer.json byte-BPE, NFC/lowercase, special tokens, 77 limit, int64 IDs/mask |
| Face detector and fallback | VisionModels | YuNet 2023mar, Haar fallback; face filtering and face-safe framing |
| Depth and parallax | VisionModels, PhotoClip | Long side 518, dimensions divisible by 14, 2/98 percentiles; face depth flattening, reflect edges; float16 cache |
| Concept and storyboard | Concept, Planner, Storyboard, Camera | Original rules, styles, emphasis, dedup thresholds, grouping/order, beat timing, motions/crop/framing and reasons |
| Rendering | Renderer, PhotoClip, VideoFileClip | Grades, transitions, title, grain/vignette, fades, music, H264/AAC MP4; local video clip extension retained |
| Storage | Project, Npy | Original JSON names/shape and directory structure; C-order little-endian f32/f16 NPY; copying import |
| Web/API | Server, original HTML | Same endpoints, fields, editor, playback, downloads/Range and history; localhost default port 8765 |
| Execution | Cli, Jobs | Original CLI options, progress/errors/repeated renders plus cancellation and durable job recovery |

Generation/stylization remain unavailable placeholders. No new image/video generation service is connected. Original Korean concept negation limitations are retained: for example, `인물 빼고` can still be interpreted as emphasis. This is documented rather than replaced with a new planning design.

## Corrections and explicit differences

- Rendering after a changed concept/settings creates a current plan. HTTP 400 or another failed plan save blocks POST render.
- Cancellation stops queued or running work; render state survives restart and unfinished jobs are marked interrupted. Photo/plan mutations are blocked during rendering.
- Null required fields are HTTP 400, duplicate/unknown photo IDs are rejected, plan writes are atomic and serialized. Failed imports leave no partial target; existing target IDs are never overwritten.
- Local video clip decoding streams actual decoded RGB frames through a 64KiB buffer into a temporary disk cache, instead of allocating the requested scene length in memory. The cache is limited to 8GiB and each frame to 64MiB; frame retrieval reads one frame at a time. Short source clips can fill longer scenes and ordinary full-resolution scenes can exceed the former 512MiB allocation limit. The cache is deleted on disposal and on decoding/limit failures; no video generation service is connected.
- The original music score, moods, sample rate (44.1kHz stereo PCM), envelopes and normalization remain. Seeded Gaussian noise uses C# Random/Box–Muller rather than NumPy PCG64. Music and film grain are deterministic within this implementation but are not byte-identical to Python. Neither encoded video bytes nor title rasterization are claimed identical across native library versions.
- Decoder/resampling/font numerical differences are measured separately from planner parity. Stored fallback-mode synthetic inputs produce an exactly equal full storyboard JSON. This evidence does not establish CLIP accuracy or inference parity against the former Python runtime; actual C# models are tested separately.
- Asset setup is an explicit PowerShell step with pinned URLs and SHA-256 verification; no implicit model download occurs in an upload request. Existing model caches are reused. Build and self-contained publish use pinned NuGet lock files.

See [validation](VALIDATION.md) for actual checks, results and remaining limitations, and [third-party notices](../THIRD-PARTY-NOTICES.md) for asset provenance.

Default data root is now stable: source builds use the repository root and standalone publishes use the executable directory, independent of model installation. An early pre-release that saved projects under bin/Debug/net10.0/data-dotnet must use --import-project to copy those projects, or set PHOTOREEL_DATA to that previous directory; files are not silently moved or deleted.
