using System.Diagnostics;
using ImageMagick;
using ImageMagick.Drawing;
using OpenCvSharp;

namespace Photoreel;

public static class Engines
{
    public static readonly string[] Ids = ["edit", "parallax", "generate", "stylize"];
    public static bool Available(string id) => id == "edit" || id == "parallax" && VisionModels.DepthAvailable;
    public static object Public(string id) => new
    {
        id,
        kind = id is "edit" or "parallax" ? "edit" : id,
        name = id switch { "edit" => "원본 사진 편집", "parallax" => "입체감 있는 움직임 (실험)", "generate" => "사진에 움직임 생성", _ => "그림체 변환" },
        available = Available(id),
        uses_generative_model = id is "generate" or "stylize",
        ai_models = id == "parallax" ? "깊이 추정 모델 Depth Anything V2 Small (이 컴퓨터에서 실행)" : "",
        disclosure = id switch
        {
            "edit" => "원본 사진을 자르고 천천히 확대·이동시키고, 장면 사이를 전환 효과로 잇고, 색감을 보정한 영상입니다. AI가 장면을 새로 그리거나 사진 속 대상을 움직이게 한 부분은 없습니다.",
            "parallax" => "원본 사진 편집에 더해, 깊이 추정 AI 모델로 가까운 것과 먼 것을 구분해 서로 다른 속도로 움직였습니다. 새 장면이나 대상을 만들어 내지는 않았고 사진 속 대상이 스스로 움직이지도 않습니다. 앞뒤 물체의 경계가 늘어나 보이는 왜곡이 생길 수 있습니다.",
            "generate" => "사진을 첫 장면으로 삼아 image-to-video 생성 모델이 몇 초짜리 움직이는 영상을 만듭니다.",
            _ => "사진을 애니메이션 등 다른 그림체로 다시 그린 뒤 영상으로 만듭니다. 인물·장소가 달라 보일 수 있습니다."
        },
        requirement = id == "parallax" ? "깊이 추정 모델 파일(약 100MB)이 필요합니다. assets_cache를 확인해 주세요." : id is "generate" or "stylize" ? "외부 생성 서비스의 계정·API 키와 유료 호출 승인, 사진 외부 전송 동의가 필요합니다." : ""
    };
}
public record TimelineItem(Scene Scene, double Start, double End, double Transition, double SpanStart, double SpanEnd);
public static class Renderer
{
    public static List<TimelineItem> Timeline(Storyboard board)
    {
        var style = Catalog.Styles[board.Style]; var scenes = board.Included().ToList(); var tt = new double[scenes.Count];
        for (int i = 1; i < scenes.Count; i++) if (scenes[i].Transition != "cut") { double basic = style.TransitionSec > 0 ? style.TransitionSec : .35; if (scenes[i].Transition == "fade-black") basic = Math.Max(basic, .7); tt[i] = Math.Min(basic, .6 * Math.Min(scenes[i].Duration, scenes[i - 1].Duration)); }
        return scenes.Select((s, i) => new TimelineItem(s, s.Start, s.Start + s.Duration, tt[i], s.Start - tt[i] / 2, s.Start + s.Duration + (i + 1 < scenes.Count ? tt[i + 1] / 2 : 0))).ToList();
    }
    public static Mat Grade(Mat rgb, string grade)
    {
        if (grade == "neutral") return rgb.Clone(); var bytes = Images.Bytes(rgb);
        static double Curve(double x, double c) => Math.Clamp((x - .5) * c + .5, 0, 1);
        for (int i = 0; i < bytes.Length; i += 3)
        {
            double[] color = [bytes[i] / 255.0, bytes[i + 1] / 255.0, bytes[i + 2] / 255.0];
            double luma = color[0] * .2126 + color[1] * .7152 + color[2] * .0722;
            void Saturate(double amount) { double l = color[0] * .2126 + color[1] * .7152 + color[2] * .0722; for (int c = 0; c < 3; c++) color[c] = Math.Clamp(l + (color[c] - l) * amount, 0, 1); }
            switch (grade)
            {
                case "warm": color[0] *= 1.035; color[2] *= .95; Saturate(1.05); break;
                case "vivid": for (int c = 0; c < 3; c++) color[c] = Curve(color[c], 1.07); Saturate(1.16); break;
                case "cine": double[] shadows = [-.018, .004, .026], highlights = [.022, .004, -.02]; for (int c = 0; c < 3; c++) color[c] = Curve(color[c] + shadows[c] * (1 - luma) + highlights[c] * luma, 1.06); Saturate(.92); break;
                case "film": Saturate(.84); color[0] *= 1.02; color[2] *= .95; for (int c = 0; c < 3; c++) color[c] = .06 + Math.Clamp(color[c], 0, 1) * .89; break;
                case "bw": color = [Curve(luma, 1.1), Curve(luma, 1.1), Curve(luma, 1.1)]; break;
            }
            for (int c = 0; c < 3; c++) bytes[i + c] = (byte)(Math.Clamp(color[c], 0, 1) * 255 + .5);
        }
        using var mat = Mat.FromPixelData(rgb.Height, rgb.Width, MatType.CV_8UC3, bytes); return mat.Clone();
    }
    public static double Ease(double u) => u * u * (3 - 2 * u);
    public static Mat Blend(Mat a, Mat b, string kind, double u)
    {
        var output = new Mat(); double e = Ease(Math.Clamp(u, 0, 1));
        if (kind == "crossfade") Cv2.AddWeighted(a, 1 - e, b, e, 0, output);
        else if (kind == "fade-black") Cv2.ConvertScaleAbs(u < .5 ? a : b, output, u < .5 ? 1 - Ease(u * 2) : Ease(u * 2 - 1));
        else if (kind is "push-left" or "push-up")
        {
            output = a.Clone(); int w = a.Width, h = a.Height, off = (int)Math.Round(e * (kind == "push-left" ? w : h));
            if (kind == "push-left") { if (off < w) Copy(a, output, new(off, 0, w - off, h), new(0, 0, w - off, h)); if (off > 0) Copy(b, output, new(0, 0, off, h), new(w - off, 0, off, h)); }
            else { if (off < h) Copy(a, output, new(0, off, w, h - off), new(0, 0, w, h - off)); if (off > 0) Copy(b, output, new(0, 0, w, off), new(0, h - off, w, off)); }
        }
        else output = (u >= .5 ? b : a).Clone();
        return output;
    }
    static void Copy(Mat source, Mat dest, Rect a, Rect b) { using var src = new Mat(source, a); using var dst = new Mat(dest, b); src.CopyTo(dst); }
    static byte[]? MakeTitle(string text, int w, int h)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var title = new MagickImage(MagickColors.Transparent, (uint)w, (uint)h);
        title.Settings.Font = File.Exists(Settings.Asset("NotoSansKR-wght.ttf")) ? Settings.Asset("NotoSansKR-wght.ttf") : "Arial";
        title.Settings.FontWeight = FontWeight.Bold; int size = Math.Max(10, (int)(w * .088)), minimum = Math.Max(10, (int)(w * .04)); List<string> lines = [];
        while (true) { title.Settings.FontPointsize = size; lines.Clear(); foreach (var paragraph in text.Trim().Split('\n')) { string current = ""; foreach (var word in paragraph.Split(' ')) { var trial = (current + " " + word).Trim(); if ((title.FontTypeMetrics(trial)?.TextWidth ?? throw new InvalidDataException("글꼴 측정 실패")) <= w * .84 || current.Length == 0) current = trial; else { lines.Add(current); current = word; } } lines.Add(current); } lines = lines.Take(3).ToList(); if (lines.Count <= 2 && lines.All(l => (title.FontTypeMetrics(l)?.TextWidth ?? throw new InvalidDataException("글꼴 측정 실패")) <= w * .86) || size <= minimum) break; size = Math.Max(minimum, (int)(size * .9)); }
        int lineH = (int)(size * 1.32), y = (int)(h * .5 - lineH * lines.Count / 2.0);
        using var shadow = title.Clone(); var shadowDraw = new Drawables().Font(title.Settings.Font).FontPointSize(size).FillColor(new MagickColor(0, 0, 0, 170)).TextAlignment(TextAlignment.Center);
        var draw = new Drawables().Font(title.Settings.Font).FontPointSize(size).FillColor(MagickColors.White).TextAlignment(TextAlignment.Center);
        for (int i = 0; i < lines.Count; i++) { shadowDraw.Text(w / 2.0, y + i * lineH + size, lines[i]); draw.Text(w / 2.0, y + i * lineH + size, lines[i]); }
        shadow.Draw(shadowDraw); shadow.Blur(0, size * .16); title.Composite(shadow, CompositeOperator.Over); title.Draw(draw); using var pixels = title.GetPixels(); return pixels.ToByteArray(PixelMapping.RGBA);
    }
    static byte[][] Grain(int w, int h)
    {
        var random = new Random(3); var grains = new List<byte[]>();
        for (int k = 0; k < 6; k++) { var bytes = new byte[w / 2 * (h / 2)]; for (int i = 0; i < bytes.Length; i++) { double z = Math.Sqrt(-2 * Math.Log(Math.Max(1e-15, random.NextDouble()))) * Math.Cos(2 * Math.PI * random.NextDouble()); bytes[i] = (byte)Math.Clamp(127.5 + 42 * z, 0, 255); } using var small = Mat.FromPixelData(h / 2, w / 2, MatType.CV_8UC1, bytes); using var large = new Mat(); Cv2.Resize(small, large, new(w, h), 0, 0, InterpolationFlags.Linear); grains.Add(Images.Bytes(large)); }
        return grains.ToArray();
    }
    public static async Task<Dictionary<string, object?>> Render(Storyboard board, Dictionary<string, Photo> photos, Dictionary<string, string> proxies, string outputPath, string workDir, VisionModels models, Action<double, string>? progress = null, CancellationToken token = default, Func<ClipContext, IFrameClip>? clipFactory = null)
    {
        progress ??= (_, _) => { }; if (!Engines.Available(board.Engine)) throw new ArgumentException("이 생성 방식은 아직 쓸 수 없습니다.");
        var totalWatch = Stopwatch.StartNew(); var style = Catalog.Styles[board.Style]; var items = Timeline(board); if (items.Count == 0) throw new ArgumentException("영상에 넣을 장면이 없습니다.");
        int w = board.Output.Width / 2 * 2, h = board.Output.Height / 2 * 2, fps = board.Output.Fps, count = Math.Max(1, (int)Math.Round(board.TotalDuration * fps)); Directory.CreateDirectory(workDir); var timings = new Dictionary<string, double>();
        object? audioInfo = null; string? audioPath = null;
        if (board.Music.Mode == "synth") { progress(.01, "음악 합성"); var clock = Stopwatch.StartNew(); audioPath = System.IO.Path.Combine(workDir, "music.wav"); audioInfo = MusicSynth.Synthesize(audioPath, (double)count / fps, board.Music.Mood, board.Music.Bpm, token); timings["music_sec"] = Math.Round(clock.Elapsed.TotalSeconds, 2); }
        token.ThrowIfCancellationRequested(); var title = MakeTitle(board.Title, w, h); double titleEnd = Math.Min(items[0].End - .15, 3.2), fadeOut = Math.Min(1.2, items[^1].Scene.Duration * .5);
        byte[][]? grain = style.Grain > 0 ? Grain(w, h) : null; var vignette = new double[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { double d = Math.Sqrt(Math.Pow((x - w / 2.0) / (w / 2.0), 2) + Math.Pow((y - h / 2.0) / (h / 2.0), 2)) / Math.Sqrt(2); vignette[y * w + x] = (int)((1 - style.Vignette * Math.Pow(Math.Clamp((d - .35) / .65, 0, 1), 1.6)) * 255) / 255.0; }
        var clips = new Dictionary<int, IFrameClip>(); string pending = outputPath + ".part.mp4", logPath = System.IO.Path.Combine(workDir, "ffmpeg.log");
        var start = new ProcessStartInfo(Settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        void Add(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
        Add("-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}", "-r", fps.ToString(), "-i", "pipe:0"); if (audioPath is not null) Add("-i", audioPath);
        Add("-vf", "scale=in_range=full:out_range=tv:out_color_matrix=bt709,format=yuv420p", "-c:v", "libx264", "-preset", "medium", "-threads", "2", "-crf", "20", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-movflags", "+faststart"); if (audioPath is not null) Add("-c:a", "aac", "-b:a", "192k", "-shortest"); Add(pending);
        using var process = new Process { StartInfo = start }; var framesWatch = Stopwatch.StartNew(); string stderr = ""; Task<string>? errorRead = null;
        try
        {
            if (!process.Start()) throw new IOException("FFmpeg 실행 실패"); errorRead = process.StandardError.ReadToEndAsync(); using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            Mat Frame(int index, double t)
            {
                var it = items[index]; if (!clips.TryGetValue(index, out var clip))
                {
                    using var image = Images.Load(proxies[it.Scene.PhotoId]); using var graded = Grade(image, board.Grade); var proxy = proxies[it.Scene.PhotoId];
                    string? cache = board.Engine == "parallax" ? System.IO.Path.Combine(Directory.GetParent(System.IO.Path.GetDirectoryName(proxy)!)!.FullName, "depth", it.Scene.PhotoId + ".npy") : null;
                    var context = new ClipContext(graded, photos[it.Scene.PhotoId], it.Scene, style, it.SpanEnd - it.SpanStart, w, h, fps, models, cache);
                    clip = clipFactory is null ? new PhotoClip(graded, context.Analysis, context.Scene, style, context.Seconds, w, h, models, cache) : clipFactory(context); clips[index] = clip;
                }
                foreach (var old in clips.Keys.Where(k => k < index - 1).ToArray()) { clips[old].Dispose(); clips.Remove(old); }
                return clip.Frame((t - it.SpanStart) / Math.Max(1e-6, it.SpanEnd - it.SpanStart));
            }
            int current = 0;
            for (int f = 0; f < count; f++)
            {
                token.ThrowIfCancellationRequested(); double t = (double)f / fps; while (current + 1 < items.Count && t >= items[current].End) current++;
                var it = items[current]; var next = current + 1 < items.Count ? items[current + 1] : null; Mat frame;
                if (it.Transition > 0 && t < it.Start + it.Transition / 2) { using var a = Frame(current - 1, t); using var b = Frame(current, t); frame = Blend(a, b, it.Scene.Transition, (t - it.SpanStart) / it.Transition); }
                else if (next is not null && next.Transition > 0 && t >= next.SpanStart) { using var a = Frame(current, t); using var b = Frame(current + 1, t); frame = Blend(a, b, next.Scene.Transition, (t - next.SpanStart) / next.Transition); }
                else frame = Frame(current, t);
                using (frame)
                {
                    var bytes = Images.Bytes(frame); double titleAlpha = t < titleEnd ? Math.Min(1, Math.Min(Math.Max(0, (t - .25) / .4), Math.Max(0, (titleEnd - t) / .5))) : 0;
                    double fade = t < .35 ? Ease(t / .35) : t > board.TotalDuration - fadeOut ? Ease(Math.Max(0, (board.TotalDuration - t) / fadeOut)) : 1;
                    for (int i = 0; i < w * h; i++) for (int c = 0; c < 3; c++) { double value = bytes[i * 3 + c] * vignette[i]; if (grain is not null) value = Math.Clamp(value + (grain[f % 6][i] - 127.5) * style.Grain * 2.2, 0, 255); if (title is not null && titleAlpha > 0) { double alpha = title[i * 4 + 3] / 255.0 * titleAlpha; value = value * (1 - alpha) + title[i * 4 + c] * alpha; } bytes[i * 3 + c] = (byte)Math.Clamp(Math.Round(value * fade), 0, 255); }
                    await process.StandardInput.BaseStream.WriteAsync(bytes, token);
                }
                if (f % 15 == 0) progress(.03 + .92 * f / count, $"장면 {current + 1}/{items.Count} 그리는 중");
            }
            process.StandardInput.Close(); progress(.96, "인코딩 마무리"); using var finish = CancellationTokenSource.CreateLinkedTokenSource(token); finish.CancelAfter(TimeSpan.FromMinutes(10)); await process.WaitForExitAsync(finish.Token); stderr = await errorRead;
            if (process.ExitCode != 0 || !File.Exists(pending)) throw new IOException($"ffmpeg 인코딩 실패 (코드 {process.ExitCode}): {stderr[^Math.Min(800, stderr.Length)..]}");
            token.ThrowIfCancellationRequested(); File.Move(pending, outputPath, true);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            if (errorRead is not null) stderr = await errorRead;
            if (File.Exists(pending)) File.Delete(pending); throw;
        }
        finally { foreach (var clip in clips.Values) clip.Dispose(); File.WriteAllText(logPath, stderr); }
        timings["frames_sec"] = Math.Round(framesWatch.Elapsed.TotalSeconds, 2); timings["total_sec"] = Math.Round(totalWatch.Elapsed.TotalSeconds, 2); progress(1, "완료");
        return new() { ["engine"] = Engines.Public(board.Engine), ["width"] = w, ["height"] = h, ["fps"] = fps, ["frames"] = count, ["duration"] = Math.Round((double)count / fps, 3), ["scenes"] = items.Count, ["bytes"] = new FileInfo(outputPath).Length, ["timings"] = timings, ["audio"] = audioInfo, ["cost_usd"] = 0, ["cost_note"] = "외부 유료 서비스를 호출하지 않았습니다 (전부 이 컴퓨터에서 처리)." };
    }
}
