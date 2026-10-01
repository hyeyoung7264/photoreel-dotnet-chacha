using Photoreel;
using OpenCvSharp;
using ImageMagick;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Photoreel.Tests;

public static class Fixtures
{
    public static readonly double[][] Flat = Enumerable.Range(0, 24).Select(_ => Enumerable.Repeat(1.0 / 576, 24).ToArray()).ToArray();
    public static Photo Photo(string id, string scene = "nature", string light = "day", string? taken = null, List<Face>? faces = null) => new() { Id = id, Filename = id + ".jpg", Width = 3200, Height = 2133, TakenAt = taken, Sharpness = 500, Brightness = .5, Colorfulness = .3, Faces = faces ?? [], FaceArea = faces?.Sum(f => f.W * f.H) ?? 0, Saliency = Flat, Tags = new() { Scene = scene, SceneConf = .9, SceneTop = [[scene, .9]], Light = light, Scale = "wide" }, Methods = new() { ["faces"] = "yunet", ["semantic"] = "clip" } };
    public static Dictionary<string, float[]> Embeddings(List<Photo> photos) => photos.Select((p, i) => (p, i)).ToDictionary(x => x.p.Id, x => Enumerable.Range(0, Math.Max(32, photos.Count)).Select(k => k == x.i ? 1f : 0f).ToArray());
    public static byte[] Jpeg(int seed = 0, int w = 320, int h = 240)
    {
        using var image = new Mat(h, w, MatType.CV_8UC3); for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) image.Set(y, x, new Vec3b((byte)((x * 3 + seed * 71) % 220 + 20), (byte)((y * 5 + seed * 23) % 220 + 20), (byte)((x + y + seed * 47) % 220 + 20)));
        Cv2.Circle(image, new(w / 2, h / 2), Math.Min(w, h) / 4, new(20 + seed * 10, 180, 60), -1); using var bgr = new Mat(); Cv2.CvtColor(image, bgr, ColorConversionCodes.RGB2BGR); Cv2.ImEncode(".jpg", bgr, out var bytes); return bytes;
    }
    public static string Root
    {
        get { var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent; return dir?.FullName ?? throw new Exception("Repository root missing"); }
    }
    public static string Work(string suffix) { Settings.Root = Root; var path = Path.Combine(Root, "evidence", "tests", suffix + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
[Trait("Category", "Unit")]
public class CameraTests
{
    public static IEnumerable<object[]> Cases() => from size in new[] { (3200, 2133), (2400, 3200), (1080, 1920), (4000, 1000), (1000, 1000) } from aspect in new[] { 9.0 / 16, 1, 16.0 / 9 } from motion in Catalog.Motions.Keys select new object[] { size.Item1, size.Item2, aspect, motion };
    [Theory, MemberData(nameof(Cases))]
    public void WindowRemainsInsidePhoto(int w, int h, double aspect, string motion)
    {
        var path = Camera.PlanPath(w, h, aspect, motion, "cover", 3, .05, .08, [.8, .2], Fixtures.Flat, []); var (ww, wh) = Camera.CoverWindow(w, h, aspect);
        for (int i = 0; i <= 10; i++) { var (x, y, z) = Camera.Interpolate(path, i / 10.0); Assert.InRange(x - ww / z / 2, -1e-4, 1); Assert.InRange(y - wh / z / 2, -1e-4, 1); Assert.InRange(x + ww / z / 2, 0, 1.0001); Assert.InRange(y + wh / z / 2, 0, 1.0001); }
    }
    public static IEnumerable<object[]> Motions() => Catalog.Motions.Keys.Select(x => new object[] { x });
    [Theory, MemberData(nameof(Motions))]
    public void FacesStayInside(string motion)
    {
        List<Face> faces = [new(.62, .30, .12, .12, 1), new(.40, .34, .10, .10, 1)]; var box = Camera.FaceBox(faces)!; var path = Camera.PlanPath(2400, 3200, 9.0 / 16, motion, "cover", 4, .06, .09, [.55, .36], Fixtures.Flat, faces); var (ww, wh) = Camera.CoverWindow(2400, 3200, 9.0 / 16);
        for (int i = 0; i <= 10; i++) { var (x, y, z) = Camera.Interpolate(path, i / 10.0); Assert.True(x - ww / z / 2 <= box[0] + 1e-4); Assert.True(y - wh / z / 2 <= box[1] + 1e-4); Assert.True(x + ww / z / 2 >= box[2] - 1e-4); Assert.True(y + wh / z / 2 >= box[3] - 1e-4); }
    }
    [Fact] public void WideGroupUsesBlurFit() { var faces = Enumerable.Range(0, 5).Select(i => new Face(.05 + .18 * i, .4, .08, .12, 1)).ToList(); Assert.Equal("fit-blur", Camera.ChooseFraming(Fixtures.Photo("a", faces: faces), 9.0 / 16).Item1); }
    [Fact] public void SalientSideIsFound() { var sal = Fixtures.Flat.Select(r => r.Select(_ => 0.0).ToArray()).ToArray(); for (int y = 10; y < 14; y++) for (int x = 18; x < 23; x++) sal[y][x] = 1; var (cx, _, mass) = Camera.BestCenter(sal, .375, 1); Assert.True(cx > .7 && mass > .9); }
    [Fact] public void AffineMapsWindow() { var path = Camera.PlanPath(3200, 2133, 9.0 / 16, "zoom-in", "cover", 3, .03, .05, [.5, .5], Fixtures.Flat, []); var (cx, cy, z) = Camera.Interpolate(path, .3); var (ww, wh) = Camera.CoverWindow(3200, 2133, 9.0 / 16); var m = Camera.Affine(path, .3, 3200, 2133, 1080, 1920); Assert.InRange(Math.Abs(m[0] * (cx - ww / z / 2) * 3200 + m[2]), 0, .05); Assert.InRange(Math.Abs(m[4] * (cy + wh / z / 2) * 2133 + m[5] - 1920), 0, .05); }
}
[Trait("Category", "Unit")]
public class PlannerTests
{
    [Theory]
    [InlineData("잔잔하고 따뜻한 여행 기록", "calm")]
    [InlineData("신나는 하이라이트!", "upbeat")]
    [InlineData("영화 같은 웅장한 분위기", "cinematic")]
    [InlineData("필름 느낌의 추억", "nostalgic")]
    [InlineData("A calm, cozy recap", "calm")]
    [InlineData("우리 집 강아지들", "clean")]
    public void ConceptStyles(string text, string style) => Assert.Equal(style, Concept.Interpret(text).Style);
    [Fact] public void Modifiers() { var r = Concept.Interpret("잔잔하게, 시간순으로, 흑백, 음악 없이, 40초 정도"); Assert.Equal(("calm", "chronological", "bw", false, 40.0), (r.Style, r.Order, r.Grade, r.Music, r.Duration)); Assert.Equal(90, Concept.Interpret("1분 30초짜리").Duration); Assert.True(Concept.Interpret("천천히 보여줘").Pace > 1); Assert.True(Concept.Interpret("담백하게 빠르게").Pace < 1); }
    [Fact] public void UnsupportedRequestsAreHonest() { var r = Concept.Interpret("지브리 애니메이션 스타일로, 파도가 치고 움직이게 해줘"); Assert.Equal(2, r.Unsupported.Count); Assert.All(r.Unsupported, u => Assert.Contains("적용되지 않았습니다", u)); }
    [Fact] public void KnownNegationLimitationIsRetained() => Assert.Contains("person", Concept.Interpret("인물 빼고").Emphasis);
    [Fact] public void ChronologicalOrderAndBeats() { var photos = Enumerable.Range(0, 6).Select(i => Fixtures.Photo("p" + i, taken: new DateTime(2026, 5, 1, 9, 0, 0).AddMinutes((5 - i) * 10).ToString("s"))).ToList(); var board = Planner.Build(photos, Fixtures.Embeddings(photos), new() { Concept = "담백한 기록" }); Assert.Equal("chronological", board.OrderMode); Assert.Equal(new[] { "p5", "p4", "p3", "p2", "p1", "p0" }, board.Included().Select(s => s.PhotoId)); Assert.All(board.Included(), s => Assert.InRange(Math.Abs(s.Start / (60.0 / board.Music.Bpm) - Math.Round(s.Start / (60.0 / board.Music.Bpm))), 0, .001)); }
    [Fact] public void ContentOrderIsNotClaimedAsFact() { List<Photo> photos = [Fixtures.Photo("night", "night", "night"), Fixtures.Photo("food", "food"), Fixtures.Photo("dusk", "sunset", "dusk"), Fixtures.Photo("aerial", "aerial"), Fixtures.Photo("market", "market")]; var b = Planner.Build(photos, Fixtures.Embeddings(photos), new()); Assert.Equal("aerial", b.Included().First().PhotoId); Assert.Equal(new[] { "dusk", "night" }, b.Included().TakeLast(2).Select(s => s.PhotoId)); Assert.Contains("실제 촬영 순서와 다를", b.OrderBasis); }
    [Fact] public void DedupKeepsSharperAndReason() { List<Photo> photos = [Fixtures.Photo("a"), Fixtures.Photo("b"), Fixtures.Photo("c")]; photos[0].Sharpness = 900; photos[1].Sharpness = 300; var e = Fixtures.Embeddings(photos); e["b"] = e["a"]; var b = Planner.Build(photos, e, new()); var excluded = Assert.Single(b.Scenes, s => !s.Included); Assert.Equal("b", excluded.PhotoId); Assert.Contains("a.jpg", excluded.ExcludeReason); }
    [Fact] public void TrimmingAndTargetDuration() { var photos = Enumerable.Range(0, 30).Select(i => Fixtures.Photo("p" + i)).ToList(); var b = Planner.Build(photos, Fixtures.Embeddings(photos), new() { Concept = "신나는 하이라이트" }); Assert.InRange(b.TotalDuration, 15, 30.01); Assert.Contains(b.Scenes, s => s.ExcludeReason.Contains("길이에 맞추려고")); photos = photos.Take(12).ToList(); b = Planner.Build(photos, Fixtures.Embeddings(photos), new() { Concept = "잔잔하게 50초" }); Assert.InRange(b.TotalDuration, 44, 56); }
    [Fact] public void ManualOrderAndExclusionsSurviveStyleChange() { var photos = Enumerable.Range(0, 5).Select(i => Fixtures.Photo("p" + i)).ToList(); var previous = new Storyboard { Scenes = [new() { PhotoId = "p3" }, new() { PhotoId = "p0" }, new() { PhotoId = "p4", Included = false }, new() { PhotoId = "p1" }, new() { PhotoId = "p2", Included = false, ExcludeReason = "원래 이유" }] }; var b = Planner.Build(photos, Fixtures.Embeddings(photos), new() { Style = "cinematic", KeepCurrentOrder = true }, previous); Assert.Equal(new[] { "p3", "p0", "p1" }, b.Included().Select(s => s.PhotoId)); Assert.Equal("원래 이유", b.Scenes.Single(s => s.PhotoId == "p2").ExcludeReason); }
    [Fact] public void PaceLengthensScenes() { var p = Enumerable.Range(0, 11).Select(i => Fixtures.Photo("p" + i)).ToList(); var e = Fixtures.Embeddings(p); var a = Planner.Build(p, e, new() { Concept = "필름 같은 추억" }); var b = Planner.Build(p, e, new() { Concept = "필름 같은 추억, 천천히" }); Assert.True(b.Included().ElementAt(1).Duration > a.Included().ElementAt(1).Duration); Assert.Equal(a.Included().Count(), b.Included().Count()); }
    [Theory][InlineData("9:16", 1080, 1920)][InlineData("4:5", 1080, 1350)][InlineData("1:1", 1080, 1080)][InlineData("16:9", 1920, 1080)] public void Aspects(string aspect, int w, int h) { var b = new Storyboard { Output = new() { Aspect = aspect } }.FinalizeBoard(); Assert.Equal((w, h), (b.Output.Width, b.Output.Height)); }
    [Theory][InlineData("motion")][InlineData("grade")][InlineData("style")][InlineData("beats")][InlineData("bpm")][InlineData("fps")] public void InvalidPlanRejected(string kind) { var b = new Storyboard { Scenes = [new() { PhotoId = "a" }] }; switch (kind) { case "motion": b.Scenes[0].Motion = "teleport"; break; case "grade": b.Grade = "xx"; break; case "style": b.Style = "xx"; break; case "beats": b.Scenes[0].Beats = 0; break; case "bpm": b.Music.Bpm = 0; break; case "fps": b.Output.Fps = 0; break; } Assert.Throws<ArgumentException>(() => b.FinalizeBoard()); }
}
[Trait("Category", "Media")]
public class MediaTests
{
    [Theory]
    [InlineData("neutral")]
    [InlineData("warm")]
    [InlineData("vivid")]
    [InlineData("cine")]
    [InlineData("film")]
    [InlineData("bw")]
    public void GradeKeepsDimensionsAndBlackWhite(string grade) { using var source = new Mat(12, 16, MatType.CV_8UC3, new Scalar(50, 120, 180)); using var result = Renderer.Grade(source, grade); Assert.Equal(source.Size(), result.Size()); var data = Images.Bytes(result); if (grade == "bw") Assert.True(data[0] == data[1] && data[1] == data[2]); if (grade == "neutral") Assert.Equal(Images.Bytes(source), data); }
    [Theory]
    [InlineData("crossfade")]
    [InlineData("fade-black")]
    [InlineData("push-left")]
    [InlineData("push-up")]
    [InlineData("cut")]
    public void TransitionEndpoints(string kind) { using var a = new Mat(10, 12, MatType.CV_8UC3, new Scalar(12, 24, 36)); using var b = new Mat(10, 12, MatType.CV_8UC3, new Scalar(210, 220, 230)); using var start = Renderer.Blend(a, b, kind, 0); using var end = Renderer.Blend(a, b, kind, 1); Assert.Equal(Images.Bytes(a), Images.Bytes(start)); Assert.Equal(Images.Bytes(b), Images.Bytes(end)); if (kind == "fade-black") { using var middle = Renderer.Blend(a, b, kind, .5); Assert.Equal(0, Cv2.Mean(middle).Val0); } }
    [Theory][InlineData(false)][InlineData(true)] public void NpyRoundTrip(bool half) { var root = Fixtures.Work("npy"); float[] values = [0, .1f, .5f, 1]; var path = Path.Combine(root, "data.npy"); Npy.Write(path, values, [2, 2], half); var a = Npy.Read(path); Assert.Equal(new[] { 2, 2 }, a.Shape); for (int i = 0; i < 4; i++) Assert.InRange(Math.Abs(a.Values[i] - values[i]), 0, half ? .001 : 0); }
    [Theory]
    [InlineData("calm")]
    [InlineData("light")]
    [InlineData("upbeat")]
    [InlineData("cinematic")]
    [InlineData("lofi")]
    public void AccompanimentIsStereoPcmAndFinite(string mood) { var root = Fixtures.Work("music"); var path = Path.Combine(root, "music.wav"); var report = System.Text.Json.JsonSerializer.SerializeToElement(MusicSynth.Synthesize(path, 2, mood, 100), Settings.Json); Assert.Equal(44 + 2 * 44100 * 4, new FileInfo(path).Length); Assert.InRange(report.GetProperty("peak").GetDouble(), .01, .89); Assert.InRange(report.GetProperty("rms_db").GetDouble(), -20, -13); Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4)); }
    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("tiff")]
    [InlineData("heic")]
    [InlineData("heif")]
    public void DecodeFormats(string extension) { var root = Fixtures.Work("decode"); using var image = new MagickImage(Fixtures.Jpeg()); var path = Path.Combine(root, "synthetic." + extension); image.Write(path); using var decoded = Images.Load(path); Assert.Equal((320, 240), (decoded.Width, decoded.Height)); }
    [Theory]
    [InlineData(1, 320, 240)]
    [InlineData(2, 320, 240)]
    [InlineData(3, 320, 240)]
    [InlineData(4, 320, 240)]
    [InlineData(5, 240, 320)]
    [InlineData(6, 240, 320)]
    [InlineData(7, 240, 320)]
    [InlineData(8, 240, 320)]
    public void ExifOrientationAndDate(int orientation, int width, int height) { var root = Fixtures.Work("exif"); using var image = new MagickImage(Fixtures.Jpeg()); var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)orientation); exif.SetValue(ExifTag.DateTimeOriginal, "2026:05:01 10:20:30"); image.SetProfile(exif); image.Orientation = (OrientationType)orientation; var path = Path.Combine(root, "rotated.jpg"); image.Write(path); using var decoded = Images.Load(path); Assert.Equal((width, height), (decoded.Width, decoded.Height)); Assert.Equal("2026-05-01T10:20:30", Images.TakenAt(path)); }
}
