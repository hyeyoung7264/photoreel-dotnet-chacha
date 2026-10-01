# photoreel-dotnet

사진 여러 장과 자유 문장 콘셉트로 짧은 MP4를 만드는 Windows C#/.NET 앱입니다. 기존 Photoreel의 웹 화면, API와 프로젝트 JSON/NPY 형식을 유지합니다. 사진 분석과 영상 제작은 이 컴퓨터 안에서 실행됩니다. **빌드·설치·실행에 Python을 사용하지 않습니다.**

현재 동작하는 기능은 사진 업로드/삭제, EXIF 방향·촬영 시각, CLIP 분류·유사도, YuNet 얼굴 검출, 콘셉트 규칙, 중복 제거·순서·길이·카메라·전환·색보정·제목, 합성 음악, 원본 편집과 깊이 기반 parallax, 프로젝트 복원·기록·진행·취소·오류·MP4 다운로드 및 CLI입니다. generate/stylize는 원본과 같이 사용할 수 없는 자리입니다.

## 설치와 실행

개발 빌드는 Windows x64, .NET SDK 10.0.401 이상(10.0 패치 계열), PowerShell을 사용합니다.

```powershell
# 처음 한 번: 공식 모델·폰트와 FFmpeg 확보 (약 800MB; 기존 캐시 재사용)
.\scripts\setup-assets.ps1

dotnet restore dotnet/Photoreel/Photoreel.csproj --configfile NuGet.Config --packages .packages --locked-mode
dotnet run --project dotnet/Photoreel --no-restore -- --serve
```

브라우저에서 http://127.0.0.1:8765 를 엽니다. 포트가 이미 사용 중이면 `PHOTOREEL_PORT` 환경변수로 다른 포트를 지정하세요. 기본 연결은 localhost이며, 별도 외부 서비스가 필요하지 않습니다.

```powershell
# .NET 설치 없이 실행할 Windows x64 배포 폴더 생성
.\scripts\publish.ps1
.\publish\Photoreel.exe --serve
```

생성된 배포 폴더에는 모델과 네이티브 라이브러리가 있으므로 크기가 큽니다. 소스 저장소에는 모델 weights, FFmpeg, 개인 사진과 출력 영상을 포함하지 않습니다. 첫 설치의 자산 다운로드 이후 분석·렌더에 네트워크를 사용하지 않습니다. [자산과 라이선스](THIRD-PARTY-NOTICES.md)를 확인하세요.

## CLI와 기존 프로젝트

```powershell
.\publish\Photoreel.exe 'C:\Photos\Trip' --concept '잔잔하고 따뜻한 여행 기록' --title '여행' --aspect 9:16 --seconds 20 --engine parallax --out out\trip.mp4
.\publish\Photoreel.exe 'C:\Photos\Trip' --concept '담백한 기록' --no-music --plan-only
.\publish\Photoreel.exe --import-project 'C:\OldData\projects\abcdef123456'
```

옵션: `--style clean|calm|upbeat|cinematic|nostalgic`, `--aspect 9:16|4:5|1:1|16:9`, `--engine edit|parallax`, `--no-music`, `--plan-only`.

가져오기는 원본 폴더를 바꾸지 않고 사본을 생성하며, 대상 ID가 이미 있으면 덮어쓰지 않습니다. 검증 실패 시 부분 프로젝트는 남기지 않습니다. JSON과 little-endian float32/float16 NPY 캐시는 그대로 읽습니다. 직접 기존 데이터 폴더를 연결할 수도 있지만, 보존이 필요한 프로젝트는 가져오기로 사본을 만드세요.

환경변수: `PHOTOREEL_ROOT`, `PHOTOREEL_DATA`(기본 data-dotnet), `PHOTOREEL_ASSETS`(assets_cache), `PHOTOREEL_FFMPEG`(native/ffmpeg.exe), `PHOTOREEL_HOST`, `PHOTOREEL_PORT`. `PHOTOREEL_NO_MODELS=1`은 오프라인 대체 특징 테스트용입니다. 분류 모델을 준비하지 못하면 UI가 대체 특징 사용을 알립니다. 깊이 모델이 없으면 parallax를 실행하지 않습니다.

## 검증

```powershell
dotnet restore dotnet/Photoreel.Tests/Photoreel.Tests.csproj --configfile NuGet.Config --packages .packages --locked-mode
dotnet test dotnet/Photoreel.Tests --no-restore --filter 'Category=Unit|Category=Media|Category=API'
dotnet test dotnet/Photoreel.Tests --no-restore --filter Category=RealModels
dotnet run --project dotnet/Photoreel -- --probe
```

브라우저 검증에는 별도 개발 도구 Node/Playwright와 Edge를 사용합니다. 앱 실행에는 필요하지 않습니다. `scripts/browser-checks.cjs`는 업로드·실제 CLIP·편집·재생·다운로드·복원·저장 실패 차단·콘셉트 갱신·취소를 검증합니다. Reference/PrivatePhotos는 공개하지 않은 로컬 기준 데이터와 허용된 사진 사본이 있어야 하는 선택적 평가입니다.

[전환 범위와 차이](docs/MIGRATION.md), [검증 결과와 한계](docs/VALIDATION.md)를 참고하세요. 실제 사진의 영상 미감이나 음악 감상 품질, 모든 장치/코덱과의 호환성까지 보장하지 않습니다.

브라우저 검증 도구 준비: .\scripts\setup-test-tools.ps1 → --probe로 합성 fixture 생성 → .\scripts\clean-publish-check.ps1. Reference/PrivatePhotos를 제외한 재현 가능한 기본 테스트는 위 category 필터로 실행합니다.
