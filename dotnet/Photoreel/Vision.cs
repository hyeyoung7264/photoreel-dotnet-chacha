using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
namespace Photoreel;

public record Label(string Key, string Name, string Prompt);
public record Face(double X, double Y, double W, double H, double Score);
public sealed class Tags
{
    public string? Scene { get; set; }
    public double? SceneConf { get; set; }
    public object[][] SceneTop { get; set; } = [];
    public string? Light { get; set; }
    public double? LightConf { get; set; }
    public string? Scale { get; set; }
    public double? ScaleConf { get; set; }
}
public sealed class Photo
{
    public string Id { get; set; } = ""; public string Filename { get; set; } = ""; public int Width { get; set; }
    public int Height { get; set; }
    public string? TakenAt { get; set; }
    public double Sharpness { get; set; }
    public double Brightness { get; set; }
    public double Warmth { get; set; }
    public double Colorfulness { get; set; }
    public List<Face> Faces { get; set; } = []; public double FaceArea { get; set; }
    public double[] Focus { get; set; } = [.5, .5];
    public double[][] Saliency { get; set; } = []; public Tags Tags { get; set; } = new(); public Dictionary<string, string> Methods { get; set; } = new();
}
public sealed class VisionModels : IDisposable
{
    public static readonly Label[] Scenes = [
    new("aerial","넓은 전경","an aerial view photo of a landscape from above"),new("nature","자연 풍경","a landscape photo of mountains, fields or nature scenery"),new("sea","바다·해변","a photo of a beach or the sea"),new("street","거리·건물","a photo of a city street or buildings"),new("market","시장·상점","a photo of a market, shop or stall"),new("food","음식","a close-up photo of food or drink"),new("person","인물","a portrait photo of one person"),new("people","여러 사람","a photo of a group of people"),new("animal","동물","a photo of a pet animal, a dog or a cat"),new("indoor","실내","an indoor photo of a room"),new("sign","표지·글자","a photo of a sign, signpost or text"),new("plant","꽃·식물","a close-up photo of flowers or plants"),new("road","길","a photo of a road, path or trail"),new("vehicle","탈것","a photo of a car, train, airplane or boat"),new("sunset","노을","a photo of a sunset or sunrise sky"),new("night","야경","a night photo with lights"),new("object","사물","a close-up photo of an object")];
    public static readonly Label[] Lights = [new("day", "낮", "a photo taken in bright daytime"), new("dusk", "해질녘", "a photo taken at sunset or dusk"), new("night", "밤", "a photo taken at night")];
    public static readonly Label[] Scales = [new("wide", "넓은 장면", "a wide shot showing a whole scene"), new("medium", "중간 거리", "a medium shot of a subject"), new("close", "가까운 장면", "a close-up shot of a detail")];
    readonly object gate = new(); InferenceSession? vision, text, depth; ClipTokenizer? tokenizer;
    readonly Dictionary<string, float[]> labelEmbeddings = new(); bool attempted;
    public string? ClipError { get; private set; }
    public string? FaceError { get; private set; }
    public static bool DepthAvailable => !Settings.ModelsDisabled && File.Exists(Settings.Asset("depth-anything-v2-small.onnx"));
    static Microsoft.ML.OnnxRuntime.SessionOptions Options() => new() { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, IntraOpNumThreads = 2, InterOpNumThreads = 1 };
    public bool EnsureClip()
    {
        lock (gate)
        {
            if (attempted) return vision is not null; attempted = true; try
            {
                if (Settings.ModelsDisabled) throw new InvalidOperationException("모델 없이 실행하도록 설정됨"); using var options = Options();
                vision = new(Settings.Asset("clip-vit-base-patch32-vision.onnx"), options); text = new(Settings.Asset("clip-vit-base-patch32-text.onnx"), options); tokenizer = new(Settings.Asset("clip-vit-base-patch32-tokenizer.json"));
                foreach (var label in Scenes.Concat(Lights).Concat(Scales)) labelEmbeddings[label.Prompt] = EmbedText(label.Prompt); return true;
            }
            catch (Exception e) { ClipError = e.Message; vision?.Dispose(); text?.Dispose(); vision = null; text = null; return false; }
        }
    }
    public long[] Tokenize(string prompt) { lock (gate) { if (!EnsureClip()) throw new InvalidOperationException(ClipError); return tokenizer!.Encode(prompt); } }
    static float[] Normalize(float[] data) { double norm = Math.Sqrt(data.Sum(x => (double)x * x)) + 1e-8; return data.Select(x => (float)(x / norm)).ToArray(); }
    static string OutputName(InferenceSession s, string preferred) => s.OutputMetadata.ContainsKey(preferred) ? preferred : s.OutputMetadata.Keys.First();
    public float[] EmbedText(string prompt)
    {
        lock (gate)
        {
            if (text is null) throw new InvalidOperationException("CLIP text unavailable"); var ids = tokenizer!.Encode(prompt);
            var feed = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])) };
            if (text.InputMetadata.ContainsKey("attention_mask")) feed.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length])));
            using var result = text.Run(feed, [OutputName(text, "text_embeds")]); return Normalize(result.First().AsTensor<float>().ToArray());
        }
    }
    public float[] EmbedImage(Mat rgb)
    {
        lock (gate)
        {
            if (!EnsureClip()) throw new InvalidOperationException(ClipError); var scale = 224.0 / Math.Min(rgb.Width, rgb.Height);
            using var im = Bicubic.Resize(rgb, Math.Max(224, (int)Math.Round(rgb.Width * scale)), Math.Max(224, (int)Math.Round(rgb.Height * scale)));
            using var cropped = new Mat(im, new Rect((im.Width - 224) / 2, (im.Height - 224) / 2, 224, 224)); using var crop = cropped.Clone();
            var data = TensorData(crop, [.48145466f, .4578275f, .40821073f], [.26862954f, .26130258f, .27577711f]);
            using var result = vision!.Run([NamedOnnxValue.CreateFromTensor(vision.InputMetadata.Keys.First(), new DenseTensor<float>(data, [1, 3, 224, 224]))], [OutputName(vision, "image_embeds")]); return Normalize(result.First().AsTensor<float>().ToArray());
        }
    }
    public static float[] TensorData(Mat rgb, float[] mean, float[] std) { var bytes = Images.Bytes(rgb); int n = rgb.Width * rgb.Height; var data = new float[n * 3]; for (int i = 0; i < n; i++) for (int c = 0; c < 3; c++) data[c * n + i] = (bytes[i * 3 + c] / 255f - mean[c]) / std[c]; return data; }
    public static double Dot(float[] a, float[] b) { if (a.Length != b.Length) throw new InvalidDataException("임베딩 차원이 다릅니다. 사진 분석을 다시 실행해 주세요."); double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
    (string Key, double Probability)[] Classify(float[] emb, Label[] labels) { var logits = labels.Select(l => 100 * Dot(labelEmbeddings[l.Prompt], emb)).ToArray(); var max = logits.Max(); var sum = logits.Sum(x => Math.Exp(x - max)); return labels.Select((l, i) => (l.Key, Math.Exp(logits[i] - max) / sum)).OrderByDescending(x => x.Item2).ToArray(); }
    public (float[] Emb, Tags Tags, string Method) Semantic(Mat rgb, double brightness, double warmth)
    {
        lock (gate)
        {
            if (!EnsureClip()) return (ColorEmbedding(rgb), new() { Light = brightness < .22 ? "night" : brightness < .4 && warmth > .08 ? "dusk" : "day" }, "color-hist");
            var emb = EmbedImage(rgb); var s = Classify(emb, Scenes); var l = Classify(emb, Lights); var z = Classify(emb, Scales);
            return (emb, new() { Scene = s[0].Key, SceneConf = Math.Round(s[0].Probability, 3), SceneTop = s.Take(3).Select(x => new object[] { x.Key, Math.Round(x.Probability, 3) }).ToArray(), Light = l[0].Key, LightConf = Math.Round(l[0].Probability, 3), Scale = z[0].Key, ScaleConf = Math.Round(z[0].Probability, 3) }, "clip");
        }
    }
    public static float[] ColorEmbedding(Mat rgb) { using var small = new Mat(); using var hsv = new Mat(); using var gray = new Mat(); using var layout = new Mat(); Cv2.Resize(rgb, small, new(64, 64), 0, 0, InterpolationFlags.Area); Cv2.CvtColor(small, hsv, ColorConversionCodes.RGB2HSV); Cv2.CvtColor(small, gray, ColorConversionCodes.RGB2GRAY); Cv2.Resize(gray, layout, new(8, 8), 0, 0, InterpolationFlags.Area); var pixels = Images.Bytes(hsv); var hist = new float[128]; for (int i = 0; i < pixels.Length; i += 3) hist[Math.Min(7, pixels[i] * 8 / 180) * 16 + pixels[i + 1] / 64 * 4 + pixels[i + 2] / 64]++; return Normalize(hist.Select(x => (float)Math.Sqrt(x / 4096f)).Concat(Images.Bytes(layout).Select(x => (x / 255f - .5f) * .15f)).ToArray()); }
    public Mat EstimateDepth(Mat rgb)
    {
        lock (gate)
        {
            if (!DepthAvailable) throw new InvalidOperationException("깊이 모델을 준비하지 못함"); if (depth is null) { using var options = Options(); depth = new(Settings.Asset("depth-anything-v2-small.onnx"), options); }
            double scale = 518.0 / Math.Max(rgb.Width, rgb.Height); int w = Math.Max(14, (int)Math.Round(rgb.Width * scale / 14) * 14), h = Math.Max(14, (int)Math.Round(rgb.Height * scale / 14) * 14);
            using var small = new Mat(); Cv2.Resize(rgb, small, new(w, h), 0, 0, InterpolationFlags.Area); var data = TensorData(small, [.485f, .456f, .406f], [.229f, .224f, .225f]);
            using var result = depth.Run([NamedOnnxValue.CreateFromTensor(depth.InputMetadata.Keys.First(), new DenseTensor<float>(data, [1, 3, h, w]))]); var tensor = result.First().AsTensor<float>(); var dims = tensor.Dimensions.ToArray(); var values = tensor.ToArray(); var sorted = values.Order().ToArray(); var lo = Percentile(sorted, .02); var hi = Percentile(sorted, .98); return Images.FloatMat(values.Select(x => (float)Math.Clamp((x - lo) / Math.Max(1e-6, hi - lo), 0, 1)).ToArray(), dims[^2], dims[^1]);
        }
    }
    public static double Percentile(float[] sorted, double p) { double at = (sorted.Length - 1) * p; int left = (int)at; return sorted[left] + (sorted[Math.Min(left + 1, sorted.Length - 1)] - sorted[left]) * (at - left); }
    public (List<Face>, string) DetectFaces(Mat rgb)
    {
        using var small = new Mat(); double scale = Math.Min(1, 1280.0 / Math.Max(rgb.Width, rgb.Height)); Cv2.Resize(rgb, small, new((int)Math.Round(rgb.Width * scale), (int)Math.Round(rgb.Height * scale)), 0, 0, InterpolationFlags.Area); int w = small.Width, h = small.Height; using var bgr = new Mat(); Cv2.CvtColor(small, bgr, ColorConversionCodes.RGB2BGR);
        if (!Settings.ModelsDisabled && File.Exists(Settings.Asset("face_detection_yunet_2023mar.onnx"))) try { using var detector = FaceDetectorYN.Create(Settings.Asset("face_detection_yunet_2023mar.onnx"), "", new(w, h), .75f, .3f, 200); using var found = new Mat(); detector.Detect(bgr, found); var faces = new List<Face>(); int foundRows = found.Rows; for (int i = 0; i < foundRows; i++) { var f = new Face(Math.Max(0, found.At<float>(i, 0) / w), Math.Max(0, found.At<float>(i, 1) / h), Math.Min(1, found.At<float>(i, 2) / w), Math.Min(1, found.At<float>(i, 3) / h), Math.Round(found.At<float>(i, 14), 3)); if (f.H * h >= .035 * Math.Min(w, h)) faces.Add(f); } return (faces, "yunet"); } catch (Exception e) { FaceError = e.Message; }
        try { using var cascade = new CascadeClassifier(Settings.Asset("haarcascade_frontalface_default.xml")); using var gray = new Mat(); Cv2.CvtColor(small, gray, ColorConversionCodes.RGB2GRAY); var boxes = cascade.DetectMultiScale(gray, 1.1, 6, HaarDetectionTypes.ScaleImage, new(Math.Max(24, w / 30), Math.Max(24, w / 30))); return (boxes.Where(x => x.Height >= .035 * Math.Min(w, h)).Select(x => new Face((double)x.X / w, (double)x.Y / h, (double)x.Width / w, (double)x.Height / h, .5)).ToList(), "haar"); } catch (Exception e) { FaceError = e.Message; return ([], "none"); }
    }
    public void Dispose() { vision?.Dispose(); text?.Dispose(); depth?.Dispose(); }
}
public static class Bicubic
{
    static double Kernel(double x) { x = Math.Abs(x); return x < 1 ? ((1.5 * x - 2.5) * x) * x + 1 : x < 2 ? (((-.5 * x + 2.5) * x - 4) * x) + 2 : 0; }
    static (int Start, int[] Weights)[] Weights(int input, int output) { double scale = (double)input / output, filter = Math.Max(1, scale), support = 2 * filter; var result = new (int, int[])[output]; for (int p = 0; p < output; p++) { double center = (p + .5) * scale; int start = Math.Max(0, (int)(center - support + .5)), end = Math.Min(input, (int)(center + support + .5)); var weights = Enumerable.Range(start, end - start).Select(i => Kernel((i - center + .5) / filter)).ToArray(); double sum = weights.Sum(); result[p] = (start, weights.Select(x => (int)Math.Round(x / sum * (1 << 22), MidpointRounding.AwayFromZero)).ToArray()); } return result; }
    public static Mat Resize(Mat rgb, int width, int height)
    {
        int sourceHeight = rgb.Height; var src = Images.Bytes(rgb); var horizontal = new byte[width * sourceHeight * 3]; var output = new byte[width * height * 3]; var wx = Weights(rgb.Width, width); var wy = Weights(sourceHeight, height);
        for (int y = 0; y < sourceHeight; y++) for (int x = 0; x < width; x++) for (int c = 0; c < 3; c++) { long sum = 1 << 21; var (start, weights) = wx[x]; for (int k = 0; k < weights.Length; k++) sum += (long)src[(y * rgb.Width + start + k) * 3 + c] * weights[k]; horizontal[(y * width + x) * 3 + c] = (byte)Math.Clamp(sum >> 22, 0, 255); }
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int c = 0; c < 3; c++) { long sum = 1 << 21; var (start, weights) = wy[y]; for (int k = 0; k < weights.Length; k++) sum += (long)horizontal[((start + k) * width + x) * 3 + c] * weights[k]; output[(y * width + x) * 3 + c] = (byte)Math.Clamp(sum >> 22, 0, 255); }
        using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC3, output); return mat.Clone();
    }
}
