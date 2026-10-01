using System.Globalization;
namespace Photoreel;

public static class Cli
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help")) { Console.WriteLine("Photoreel <사진폴더> --concept <문장> [--title 제목] [--aspect 9:16|4:5|1:1|16:9] [--seconds 20] [--style clean|calm|upbeat|cinematic|nostalgic] [--engine edit|parallax] [--no-music] [--out out.mp4] [--plan-only]\nPhotoreel --serve\nPhotoreel --import-project <기존 프로젝트 폴더>\n환경변수: PHOTOREEL_DATA, PHOTOREEL_ASSETS, PHOTOREEL_FFMPEG, PHOTOREEL_PORT"); return 0; }
            if (args[0] == "--import-project")
            {
                if (args.Length != 2) throw new ArgumentException("기존 프로젝트 폴더 하나를 지정해 주세요.");
                var source = Path.GetFullPath(args[1]); var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(source)); var imported = new Project(id);
                if (Directory.Exists(imported.DirectoryPath)) throw new ArgumentException("대상 프로젝트가 이미 있습니다. 덮어쓰지 않습니다.");
                if (!File.Exists(Path.Combine(source, "photos.json"))) throw new ArgumentException("기존 프로젝트가 아닙니다.");
                var staging = imported.DirectoryPath + ".import-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(staging);
                    foreach (var sub in new[] { "orig", "proxy", "thumb", "emb", "depth", "renders" }) if (Directory.Exists(Path.Combine(source, sub))) CopyTree(Path.Combine(source, sub), Path.Combine(staging, sub));
                    foreach (var file in new[] { "photos.json", "plan.json" }) if (File.Exists(Path.Combine(source, file))) File.Copy(Path.Combine(source, file), Path.Combine(staging, file));
                    var photos = Settings.ReadJson<List<Photo>>(Path.Combine(staging, "photos.json")) ?? throw new InvalidDataException("사진 목록이 없습니다.");
                    if (photos.Count > 80 || photos.Any(p => p is null || !System.Text.RegularExpressions.Regex.IsMatch(p.Id ?? "", "^[0-9a-f]{6,32}$")) || photos.Select(p => p.Id).Distinct().Count() != photos.Count) throw new InvalidDataException("잘못된 사진 목록");
                    foreach (var photo in photos)
                    {
                        var embedding = Npy.Read(Path.Combine(staging, "emb", photo.Id + ".npy"));
                        if (embedding.Shape.Length != 1 || embedding.Values.Length == 0 || embedding.Values.Any(v => !float.IsFinite(v))) throw new InvalidDataException("잘못된 임베딩");
                        if (!File.Exists(Path.Combine(staging, "proxy", photo.Id + ".jpg"))) throw new InvalidDataException("사진 사본이 없습니다.");
                    }
                    if (Settings.ReadJson<Storyboard>(Path.Combine(staging, "plan.json")) is { } board)
                    {
                        board.FinalizeBoard(); var ids = photos.Select(p => p.Id).ToHashSet(); var seen = new HashSet<string>();
                        if (!Engines.Ids.Contains(board.Engine) || board.Scenes.Any(s => !ids.Contains(s.PhotoId) || !seen.Add(s.PhotoId))) throw new InvalidDataException("잘못된 구성안");
                    }
                    Directory.Move(staging, imported.DirectoryPath);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                Console.WriteLine("가져옴: " + imported.DirectoryPath); return 0;
            }
            var options = new Dictionary<string, string>(); var flags = new HashSet<string>();
            for (int i = 1; i < args.Length; i++) { if (args[i] is "--no-music" or "--plan-only") flags.Add(args[i]); else { if (i + 1 >= args.Length || !new[] { "--concept", "--title", "--aspect", "--seconds", "--style", "--engine", "--out" }.Contains(args[i])) throw new ArgumentException("알 수 없는 옵션: " + args[i]); options[args[i]] = args[++i]; } }
            var files = Directory.GetFiles(args[0]).Where(p => Project.ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.Ordinal).ToArray(); if (files.Length == 0) throw new ArgumentException("사진이 없습니다: " + args[0]);
            using var models = new VisionModels(); var project = Project.Create(); var started = System.Diagnostics.Stopwatch.StartNew();
            foreach (var file in files) { var p = project.AddPhoto(Path.GetFileName(file), File.ReadAllBytes(file), models); Console.WriteLine($"분석 {p.Filename}: {p.Tags.Scene} / {p.Tags.Light} / 얼굴 {p.Faces.Count}"); }
            var analyzeSec = started.Elapsed.TotalSeconds;
            var req = new PlanRequest { Concept = options.GetValueOrDefault("--concept", ""), Title = options.GetValueOrDefault("--title", ""), Aspect = options.GetValueOrDefault("--aspect", "9:16"), TargetDuration = options.TryGetValue("--seconds", out var s) ? double.Parse(s, CultureInfo.InvariantCulture) : null, Style = options.GetValueOrDefault("--style"), Music = !flags.Contains("--no-music") };
            var plan = Planner.Build(project.Photos(), project.Embeddings(), req); plan.Engine = options.GetValueOrDefault("--engine", "edit"); project.ValidatePlan(plan); project.SavePlan(plan);
            Console.WriteLine($"스타일: {plan.Style} · {plan.TotalDuration:F1}초\n순서 기준: {plan.OrderBasis}"); foreach (var note in plan.Notes.Concat(plan.Unsupported)) Console.WriteLine(" * " + note);
            if (flags.Contains("--plan-only")) { Console.WriteLine(project.DirectoryPath); return 0; }
            using var cancel = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            var (n, dir) = project.NewRender(); double last = 0;
            var stats = await Renderer.Render(plan, project.Photos().ToDictionary(p => p.Id), project.Photos().ToDictionary(p => p.Id, p => project.Proxy(p.Id)), Path.Combine(dir, "video.mp4"), dir, models, (f, stage) => { if (f - last >= .1 || f == 1) { last = f; Console.WriteLine($"{f:P0} {stage}"); } }, cancel.Token);
            stats["status"] = "ok"; ((Dictionary<string, double>)stats["timings"]!)["analyze_sec"] = Math.Round(analyzeSec, 2); project.SaveRender(n, plan, stats);
            var dest = Path.GetFullPath(options.GetValueOrDefault("--out", "out.mp4")); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(Path.Combine(dir, "video.mp4"), dest, true); Console.WriteLine("완료: " + dest + "\n프로젝트: " + project.DirectoryPath); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
    static void CopyTree(string source, string dest) { if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("연결된 폴더는 가져올 수 없습니다."); Directory.CreateDirectory(dest); foreach (var file in Directory.GetFiles(source)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("연결된 파일은 가져올 수 없습니다."); File.Copy(file, Path.Combine(dest, Path.GetFileName(file))); } foreach (var dir in Directory.GetDirectories(source)) CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir))); }
}
