using System.Collections.Concurrent;
using System.Text.Json.Serialization;
namespace Photoreel;

public sealed class RenderJob
{
    public string State { get; set; } = "idle"; public double Progress { get; set; }
    public string Stage { get; set; } = "";
    public int Render { get; set; }
    public double StartedAt { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object?>? Stats { get; set; }
    public double ElapsedSec => StartedAt > 0 ? Math.Round(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - StartedAt, 1) : 0;
    [JsonIgnore] public CancellationTokenSource Cancellation { get; } = new();
}
public sealed class Jobs(VisionModels models, IHostApplicationLifetime lifetime)
{
    readonly ConcurrentDictionary<string, RenderJob> jobs = new(); readonly SemaphoreSlim cpu = new(1, 1);
    public RenderJob Get(Project project)
    {
        lock (project.Gate)
        {
            return jobs.GetOrAdd(project.DirectoryPath, _ =>
            {
                var saved = Settings.ReadJson<RenderJob>(Path.Combine(project.DirectoryPath, "job.json")) ?? new();
                if (saved.State == "running")
                {
                    saved.State = "error"; saved.Error = "앱 재시작으로 작업이 중단되었습니다. 다시 만들어 주세요."; saved.Stage = "재시작 후 복구";
                    var board = Settings.ReadJson<Storyboard>(Path.Combine(project.RenderDirectory(saved.Render), "storyboard.json")) ?? project.Plan();
                    if (board is not null) saved.Stats = project.SaveRender(saved.Render, board, new() { ["status"] = "interrupted", ["failed_stage"] = saved.Stage, ["error"] = saved.Error }); Persist(project, saved);
                }
                return saved;
            });
        }
    }
    static void Persist(Project p, RenderJob job) => Settings.WriteJson(Path.Combine(p.DirectoryPath, "job.json"), job);
    public void RequireIdle(Project p) { if (Get(p).State == "running") throw new BusyException("이미 만드는 중입니다."); }
    public RenderJob Start(Project p)
    {
        lock (p.Gate)
        {
            RequireIdle(p); var board = p.Plan(); if (board is null || !board.Included().Any()) throw new ArgumentException("구성안에 장면이 없습니다."); p.ValidatePlan(board);
            if (!Engines.Available(board.Engine)) throw new ArgumentException("이 생성 방식은 아직 쓸 수 없습니다.");
            var (n, dir) = p.NewRender(); Settings.WriteJson(Path.Combine(dir, "storyboard.json"), board);
            var job = new RenderJob { State = "running", Stage = "준비", Render = n, StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 }; jobs[p.DirectoryPath] = job; Persist(p, job);
            var photos = p.Photos().ToDictionary(x => x.Id); var proxies = photos.Keys.ToDictionary(id => id, p.Proxy);
            _ = Task.Run(() => Run(p, board, photos, proxies, job)); return job;
        }
    }
    async Task Run(Project p, Storyboard board, Dictionary<string, Photo> photos, Dictionary<string, string> proxies, RenderJob job)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, lifetime.ApplicationStopping); bool acquired = false;
        try
        {
            lock (p.Gate) { job.Stage = "다른 작업이 끝나길 기다리는 중"; Persist(p, job); }
            await cpu.WaitAsync(linked.Token); acquired = true;
            var stats = await Renderer.Render(board, photos, proxies, Path.Combine(p.RenderDirectory(job.Render), "video.mp4"), p.RenderDirectory(job.Render), models, (f, stage) => { lock (p.Gate) { job.Progress = Math.Round(f, 3); job.Stage = stage; Persist(p, job); } }, linked.Token);
            lock (p.Gate) { stats["status"] = "ok"; job.Stats = p.SaveRender(job.Render, board, stats); job.State = "done"; job.Progress = 1; job.Stage = "완료"; Persist(p, job); }
        }
        catch (Exception e) { lock (p.Gate) { bool cancelled = e is OperationCanceledException || linked.IsCancellationRequested; job.State = cancelled ? "cancelled" : "error"; job.Error = cancelled ? "사용자 취소 또는 앱 종료로 중단됨" : e.Message[..Math.Min(600, e.Message.Length)]; if (!cancelled) try { File.WriteAllText(Path.Combine(p.RenderDirectory(job.Render), "error.log"), e.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { } job.Stats = p.SaveRender(job.Render, board, new() { ["status"] = cancelled ? "cancelled" : "failed", ["failed_stage"] = job.Stage, ["error"] = job.Error, ["elapsed_sec"] = job.ElapsedSec }); if (cancelled) job.Stage = "취소됨"; Persist(p, job); } }
        finally { if (acquired) cpu.Release(); }
    }
    public RenderJob Cancel(Project p) { lock (p.Gate) { var job = Get(p); if (job.State == "running") job.Cancellation.Cancel(); return job; } }
}
public sealed class BusyException(string message) : Exception(message);
