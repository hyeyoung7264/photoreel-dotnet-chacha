# Third-party components and assets

The application executes C# and native libraries locally. No Python runtime is required. Model weights and native tools are fetched separately and are excluded from the source repository.

| Component | Pinned version / source | License |
| --- | --- | --- |
| Microsoft.ML.OnnxRuntime | NuGet 1.30.0 | MIT; package LICENSE and ThirdPartyNotices |
| OpenCvSharp4 / runtime.win | NuGet 4.13.0.20260627 | Apache-2.0; native OpenCV notices also apply |
| Magick.NET-Q8-AnyCPU | NuGet 14.17.2 | Apache-2.0; native ImageMagick and codec notices also apply |
| CLIP ViT-B/32 vision, text and tokenizer | [Xenova ONNX](https://huggingface.co/Xenova/clip-vit-base-patch32/tree/d15189d7028b43f1d3e65039190477f6af591c2a), derived from [OpenAI CLIP](https://github.com/openai/CLIP) | MIT; `licenses/CLIP-MIT.txt` |
| Depth Anything V2 Small | [ONNX Community export](https://huggingface.co/onnx-community/depth-anything-v2-small/tree/4472b7362082ad9968fee890ca0f1e5aca36b93d) | Apache-2.0; `licenses/Depth-Anything-Apache-2.0.txt` |
| YuNet 2023mar | [OpenCV Zoo](https://github.com/opencv/opencv_zoo/tree/47534e27c9851bb1128ccc0102f1145e27f23f98/models/face_detection_yunet) | MIT; `licenses/YuNet-MIT.txt` |
| Haar frontal face cascade | OpenCV 4.13.0 data | Intel Open Source Computer Vision license, embedded in XML |
| Noto Sans KR | [Google Fonts](https://github.com/google/fonts/tree/9710da1eacb3be272583c3224dcb70f9da6eadbb/ofl/notosanskr) | OFL-1.1; `licenses/NotoSansKR-OFL.txt` |
| FFmpeg / libx264 | [Gyan 7.1 essentials build](https://github.com/GyanD/codexffmpeg/releases/tag/7.1) | GPL-3.0-or-later; downloaded separately, invoked as a process |

`assets.manifest.json` records immutable model URLs, revisions and SHA-256 checksums. Existing cached ONNX files were reused. Vision/text/depth hashes match the publisher's LFS metadata; tokenizer, YuNet, cascade and font hashes match files fetched from the pinned official revisions.

Procedural accompaniment uses no third-party recordings. This describes its inputs, not a legal determination of rights in a generated score. .NET seeded noise differs from the former NumPy PCG64 waveform.

FFmpeg is not committed or redistributed in this source repository. If redistributing a packaged executable bundle, preserve the licenses and satisfy the source and notice obligations of its GPL build and all native dependencies. [FFmpeg legal guidance](https://ffmpeg.org/legal.html) and [Gyan build/source information](https://www.gyan.dev/ffmpeg/builds/) describe the upstream distribution. A locally generated publish directory is for local execution, and is excluded from Git.

Development-only tests additionally use xUnit, Microsoft.NET.Test.Sdk, Playwright (Apache-2.0) and the official `@huggingface/tokenizers` JavaScript package (Apache-2.0) for independent tokenization comparison. Browser test tooling is not an application dependency.
