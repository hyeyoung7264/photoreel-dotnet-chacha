using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;

namespace Photoreel;

public static class Server
{
    static readonly object[] Examples = [new { label = "여행", text = "잔잔하고 따뜻한 분위기의 여행 기록" }, new { label = "여행 하이라이트", text = "신나고 경쾌한 여행 하이라이트, 빠르게" }, new { label = "일상", text = "담백하게 정리한 이번 주 일상" }, new { label = "가족", text = "필름 사진 같은 가족의 추억, 인물 위주로" }, new { label = "반려동물", text = "우리 강아지의 발랄한 하루" }, new { label = "행사", text = "영화처럼 묵직한 분위기의 행사 스케치, 시간순으로" }];
    public static object PublicPhoto(Photo p) => new { id = p.Id, filename = p.Filename, width = p.Width, height = p.Height, taken_at = p.TakenAt, faces = p.Faces.Count, scene = VisionModels.Scenes.FirstOrDefault(l => l.Key == p.Tags.Scene)?.Name, scene_conf = p.Tags.SceneConf, light = VisionModels.Lights.FirstOrDefault(l => l.Key == p.Tags.Light)?.Name, methods = p.Methods };
    static Project RequireProject(string pid) { Project p; try { p = new(pid); } catch (ArgumentException) { throw new FileNotFoundException("잘못된 프로젝트 id"); } if (!p.Exists) throw new FileNotFoundException("프로젝트를 찾을 수 없습니다."); return p; }
    static IResult Json(object? data) => Results.Json(data, Settings.Json);
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = Settings.Root });
        var host = Environment.GetEnvironmentVariable("PHOTOREEL_HOST") ?? "127.0.0.1"; var port = Environment.GetEnvironmentVariable("PHOTOREEL_PORT") ?? "8765";
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 80L * 60 * 1024 * 1024 + 1024 * 1024);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 80L * 60 * 1024 * 1024 + 1024 * 1024);
        builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; });
        builder.Services.AddSingleton<VisionModels>(); builder.Services.AddSingleton<Jobs>();
        var app = builder.Build(); var models = app.Services.GetRequiredService<VisionModels>(); var jobs = app.Services.GetRequiredService<Jobs>();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception e) when (e is ArgumentException or FileNotFoundException or BusyException or JsonException or BadHttpRequestException) { context.Response.StatusCode = e is FileNotFoundException ? 404 : e is BusyException ? 409 : 400; await context.Response.WriteAsJsonAsync(new { detail = e.Message }, Settings.Json); }
            catch (Exception e) { app.Logger.LogError(e, "Photoreel 요청 실패"); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { detail = e.Message[..Math.Min(600, e.Message.Length)] }, Settings.Json); }
        });
        app.MapGet("/", () => Results.File(Path.Combine(AppContext.BaseDirectory, "web", "index.html"), "text/html; charset=utf-8", enableRangeProcessing: false));
        app.MapGet("/api/meta", () => Json(new { styles = Catalog.Styles.Values.Select(s => s.Public()), motions = Catalog.Motions, transitions = Catalog.Transitions, framings = Catalog.Framings, grades = Catalog.Grades, music_moods = Catalog.Moods, aspects = Catalog.Aspects, default_aspect = "9:16", auto_duration = new[] { 15, 30 }, engines = Engines.Ids.Select(Engines.Public), concept_examples = Examples, understanding = new { clip = models.EnsureClip(), note = "사진 분석은 전부 이 컴퓨터에서 실행되며 사진을 외부로 보내지 않습니다.", error = models.ClipError } }));
        app.MapPost("/api/projects", () => Json(new { id = Project.Create().Id }));
        app.MapGet("/api/projects/{pid}", (string pid) => { var p = RequireProject(pid); lock (p.Gate) { var job = jobs.Get(p); return Json(new { id = p.Id, photos = p.Photos().Select(PublicPhoto), plan = p.Plan(), renders = p.Renders(), job }); } });
        app.MapPost("/api/projects/{pid}/photos", async (string pid, HttpRequest request) =>
        {
            var p = RequireProject(pid); var form = await request.ReadFormAsync(); var added = new List<object>(); var errors = new List<object>();
            foreach (var file in form.Files.Where(f => f.Name == "files"))
            {
                var name = Path.GetFileName(file.FileName);
                try { lock (p.Gate) jobs.RequireIdle(p); if (file.Length > 60 * 1024 * 1024) throw new ArgumentException("파일이 너무 큽니다 (60MB 초과)."); using var memory = new MemoryStream(); await file.CopyToAsync(memory, request.HttpContext.RequestAborted); lock (p.Gate) { jobs.RequireIdle(p); added.Add(PublicPhoto(p.AddPhoto(name, memory.ToArray(), models))); } }
                catch (BusyException) { throw; }
                catch (Exception e) when (e is ArgumentException or IOException) { errors.Add(new { filename = name, error = e.Message }); }
            }
            return Json(new { added, errors });
        });
        app.MapDelete("/api/projects/{pid}/photos/{photoId}", (string pid, string photoId) => { var p = RequireProject(pid); lock (p.Gate) { jobs.RequireIdle(p); p.RemovePhoto(photoId); } return Json(new { ok = true }); });
        app.MapGet("/api/projects/{pid}/photos/{photoId}/thumb", (string pid, string photoId) => { var p = RequireProject(pid); if (!p.Photos().Any(x => x.Id == photoId)) throw new FileNotFoundException("사진을 찾을 수 없습니다."); return Results.File(p.Thumb(photoId), "image/jpeg"); });
        app.MapPost("/api/projects/{pid}/plan", (string pid, PlanRequest req) => { var p = RequireProject(pid); lock (p.Gate) { jobs.RequireIdle(p); var board = Planner.Build(p.Photos(), p.Embeddings(), req, p.Plan()); p.SavePlan(board); return Json(board); } });
        app.MapPut("/api/projects/{pid}/plan", (string pid, Storyboard board) => { var p = RequireProject(pid); lock (p.Gate) { jobs.RequireIdle(p); p.ValidatePlan(board); p.SavePlan(board); return Json(board); } });
        app.MapPost("/api/projects/{pid}/render", (string pid) => Json(jobs.Start(RequireProject(pid))));
        app.MapGet("/api/projects/{pid}/render", (string pid) => { var p = RequireProject(pid); lock (p.Gate) return Json(jobs.Get(p)); });
        app.MapPost("/api/projects/{pid}/render/cancel", (string pid) => Json(jobs.Cancel(RequireProject(pid))));
        app.MapGet("/api/projects/{pid}/renders/{n:int}/video", (string pid, int n, int? download) => { var p = RequireProject(pid); var path = Path.Combine(p.RenderDirectory(n), "video.mp4"); if (!File.Exists(path)) throw new FileNotFoundException("영상을 찾을 수 없습니다."); return Results.File(path, "video/mp4", download == 1 ? $"photoreel-{pid[..6]}-{n}.mp4" : null, enableRangeProcessing: true); });
        return app;
    }
    public static async Task Run(string[] args) { await using var app = Build(args); await app.RunAsync(); }
}
