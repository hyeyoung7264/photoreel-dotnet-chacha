using Photoreel;
using OpenCvSharp;
using ImageMagick;

Settings.Root = Settings.FindRoot();
Cv2.SetNumThreads(2);
if (args.Contains("--probe"))
{
    Directory.CreateDirectory("evidence/probe");
    Directory.CreateDirectory("evidence/browser-fixtures");
    for (int seed = 0; seed < 4; seed++)
    {
        using var pattern = new Mat(240, 320, MatType.CV_8UC3);
        for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) pattern.Set(y, x, new Vec3b((byte)((x * 3 + seed * 71) % 220 + 20), (byte)((y * 5 + seed * 23) % 220 + 20), (byte)((x + y + seed * 47) % 220 + 20)));
        Cv2.Circle(pattern, new(160, 120), 60, new(20 + seed * 10, 180, 60), -1);
        Images.SaveJpeg(pattern, $"evidence/browser-fixtures/synthetic-{seed}.jpg");
    }
    using var fixture = new Mat(240, 320, MatType.CV_8UC3);
    for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) fixture.Set(y, x, new Vec3b((byte)(40 + x / 2), (byte)(40 + y / 2), (byte)(40 + (x + y) % 180)));
    Cv2.Circle(fixture, new(160, 100), 50, new(240, 90, 40), -1);
    Images.SaveJpeg(fixture, "evidence/probe/synthetic.jpg");
    using var rgb = Images.Load("evidence/probe/synthetic.jpg");
    using var models = new VisionModels();
    if (!models.EnsureClip()) throw new Exception(models.ClipError);
    var labels = VisionModels.Scenes.Concat(VisionModels.Lights).Concat(VisionModels.Scales).ToArray();
    var prompts = labels.Select(l => l.Prompt).Concat(new[] { "  HELLO\tworld! ", "Cafe\u0301 café", "사진: 바다·해변 🌊", "It's a dog's photo, 123.", "<|startoftext|>test<|endoftext|>", string.Join(" ", Enumerable.Repeat("hello", 100)) });
    var tokens = prompts.Select(p => new { prompt = p, ids = models.Tokenize(p) }).ToArray();
    Settings.WriteJson("evidence/probe/tokens.json", tokens);
    var semantic = models.Semantic(rgb, .5, 0);
    var faces = models.DetectFaces(rgb);
    if (faces.Item2 != "yunet") throw new Exception("YuNet inference failed: " + models.FaceError);
    using var depth = models.EstimateDepth(rgb); var d = Images.Floats(depth);
    Npy.Write("evidence/probe/depth.npy", d, [depth.Height, depth.Width], true);
    Npy.Write("evidence/probe/embedding.npy", semantic.Emb, [semantic.Emb.Length]);
    var report = new { clip = semantic.Method, tags = semantic.Tags, embedding = semantic.Emb.Length, norm = Math.Sqrt(semantic.Emb.Sum(x => (double)x * x)), yunet = faces.Item2, faces = faces.Item1.Count, depth = new { depth.Width, depth.Height, min = d.Min(), max = d.Max() }, decode = new { rgb.Width, rgb.Height }, native = new { opencv = Cv2.GetVersionString(), magick = MagickNET.Version } };
    Settings.WriteJson("evidence/probe/models.json", report); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, Settings.Json));
    var photo = new Photo { Id = "synthetic", Width = rgb.Width, Height = rgb.Height, Saliency = Enumerable.Range(0, 24).Select(_ => Enumerable.Repeat(1.0 / 576, 24).ToArray()).ToArray() };
    foreach (var engine in new[] { "edit", "parallax" })
    {
        var board = new Storyboard { Title = "C# 검증", Engine = engine, Output = new() { Aspect = "test", Width = 240, Height = 320, Fps = 12 }, Music = new() { Mode = "synth", Mood = "light", Bpm = 100 }, Scenes = [new() { PhotoId = photo.Id, Beats = 3, Motion = "pan-right" }] }.FinalizeBoard();
        var work = "evidence/probe/" + engine;
        var stats = await Renderer.Render(board, new() { [photo.Id] = photo }, new() { [photo.Id] = "evidence/probe/synthetic.jpg" }, work + "/video.mp4", work, models, (f, s) => { if (f == 1) Console.WriteLine(engine + " " + s); });
        Settings.WriteJson(work + "/stats.json", stats);
    }
    return;
}
if (args.Length == 0 || args[0] == "--serve") await Server.Run(args.Skip(args.Length == 0 ? 0 : 1).ToArray());
else Environment.ExitCode = await Cli.Run(args);
