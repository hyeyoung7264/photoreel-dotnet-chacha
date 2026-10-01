using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Photoreel;
using Xunit;

namespace Photoreel.Tests;

[Trait("Category", "API")]
public class ApiTests
{
    sealed class Harness : IAsyncDisposable
    {
        public WebApplication App { get; private set; } = null!; public HttpClient Client { get; private set; } = null!; readonly string? priorData = Environment.GetEnvironmentVariable("PHOTOREEL_DATA"), priorModels = Environment.GetEnvironmentVariable("PHOTOREEL_NO_MODELS");
        public string Root { get; } = Fixtures.Work("api");
        public static async Task<Harness> Start()
        {
            var h = new Harness(); Environment.SetEnvironmentVariable("PHOTOREEL_DATA", Path.Combine(h.Root, "data")); Environment.SetEnvironmentVariable("PHOTOREEL_NO_MODELS", "1");
            h.App = Server.Build([]); h.App.Urls.Clear(); h.App.Urls.Add("http://127.0.0.1:0"); await h.App.StartAsync(); h.Client = new() { BaseAddress = new Uri(h.App.Urls.Single()) }; return h;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Environment.SetEnvironmentVariable("PHOTOREEL_DATA", priorData); Environment.SetEnvironmentVariable("PHOTOREEL_NO_MODELS", priorModels); }
        public async Task<string> Create() { var r = await Client.PostAsync("/api/projects", null); return (await r.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>(); }
        public async Task Upload(string id, int n = 3)
        {
            for (int i = 0; i < n; i++) { using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(Fixtures.Jpeg(i + 1)), "files", $"synthetic-{i}.jpg"); var r = await Client.PostAsync($"/api/projects/{id}/photos", form); Assert.Equal(HttpStatusCode.OK, r.StatusCode); var json = await r.Content.ReadFromJsonAsync<JsonObject>(); Assert.Single(json!["added"]!.AsArray()); }
        }
        public async Task<JsonObject> Plan(string id) { var r = await Client.PostAsJsonAsync($"/api/projects/{id}/plan", new { concept = "담백한 기록", title = "C# 테스트" }); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await r.Content.ReadFromJsonAsync<JsonObject>())!; }
        public async Task<JsonObject> Wait(string id)
        {
            var limit = DateTime.UtcNow.AddSeconds(60); while (DateTime.UtcNow < limit) { var j = (await Client.GetFromJsonAsync<JsonObject>($"/api/projects/{id}/render"))!; if (j["state"]!.GetValue<string>() != "running") return j; await Task.Delay(50); }
            throw new Exception("Render timeout");
        }
        public async Task<JsonObject> SmallPlan(string id)
        {
            var p = await Plan(id); p["output"] = JsonSerializer.SerializeToNode(new { aspect = "custom", width = 240, height = 426, fps = 12 }); foreach (var s in p["scenes"]!.AsArray()) s!["beats"] = 2;
            p["music"]!["mode"] = "none"; var r = await Client.PutAsJsonAsync($"/api/projects/{id}/plan", p); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await r.Content.ReadFromJsonAsync<JsonObject>())!;
        }
    }
    [Fact]
    public async Task UploadEditRenderPlaybackDownloadAndRestore()
    {
        await using var h = await Harness.Start(); var id = await h.Create(); await h.Upload(id); var p = await h.SmallPlan(id);
        var scenes = p["scenes"]!.AsArray(); if (scenes.Count > 1) { scenes[1]!["included"] = false; var first = scenes[0]!.DeepClone(); scenes.RemoveAt(0); scenes.Add(first); }
        var save = await h.Client.PutAsJsonAsync($"/api/projects/{id}/plan", p); Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"/api/projects/{id}/render", null)).StatusCode); var job = await h.Wait(id); Assert.Equal("done", job["state"]!.GetValue<string>()); var n = job["render"]!.GetValue<int>();
        var video = await h.Client.GetAsync($"/api/projects/{id}/renders/{n}/video?download=1"); Assert.Equal(HttpStatusCode.OK, video.StatusCode); Assert.Equal("attachment", video.Content.Headers.ContentDisposition!.DispositionType); var bytes = await video.Content.ReadAsByteArrayAsync(); Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{id}/renders/{n}/video"); request.Headers.Range = new(0, 99); var range = await h.Client.SendAsync(request); Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode); Assert.Equal(100, (await range.Content.ReadAsByteArrayAsync()).Length);
        var loaded = (await h.Client.GetFromJsonAsync<JsonObject>($"/api/projects/{id}"))!; Assert.Equal("ok", loaded["renders"]!.AsArray().Last()!["status"]!.GetValue<string>()); Assert.NotNull(new Project(id).Plan());
    }
    [Fact]
    public async Task InvalidInputsAreRejectedWithoutSaving()
    {
        await using var h = await Harness.Start(); var id = await h.Create(); Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync($"/api/projects/{id}/plan", new { })).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/projects/zzzz")).StatusCode);
        using (var form = new MultipartFormDataContent()) { form.Add(new ByteArrayContent([1, 2, 3]), "files", "broken.jpg"); var r = await h.Client.PostAsync($"/api/projects/{id}/photos", form); var body = await r.Content.ReadFromJsonAsync<JsonObject>(); Assert.Empty(body!["added"]!.AsArray()); Assert.Single(body["errors"]!.AsArray()); }
        await h.Upload(id, 2); var p = await h.Plan(id); var good = new Project(id).Plan()!; p["scenes"]![0]!["motion"] = "teleport"; Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PutAsJsonAsync($"/api/projects/{id}/plan", p)).StatusCode); Assert.Equal(good.Scenes[0].Motion, new Project(id).Plan()!.Scenes[0].Motion);
        p = await h.Plan(id); p["engine"] = "generate"; Assert.Equal(HttpStatusCode.OK, (await h.Client.PutAsJsonAsync($"/api/projects/{id}/plan", p)).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync($"/api/projects/{id}/render", null)).StatusCode);
    }
    [Fact]
    public async Task CancelDuplicateStartThenRepeat()
    {
        await using var h = await Harness.Start(); var id = await h.Create(); await h.Upload(id); _ = await h.Plan(id);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"/api/projects/{id}/render", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsync($"/api/projects/{id}/render", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"/api/projects/{id}/render/cancel", null)).StatusCode); var j = await h.Wait(id); Assert.Equal("cancelled", j["state"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(new Project(id).RenderDirectory(j["render"]!.GetValue<int>()), "video.mp4")));
        _ = await h.SmallPlan(id); Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"/api/projects/{id}/render", null)).StatusCode); Assert.Equal("done", (await h.Wait(id))["state"]!.GetValue<string>());
    }
    [Fact]
    public async Task EncoderFailureIsRecordedAndRetryWorks()
    {
        await using var h = await Harness.Start(); var id = await h.Create(); await h.Upload(id, 1); _ = await h.SmallPlan(id); var previous = Environment.GetEnvironmentVariable("PHOTOREEL_FFMPEG");
        try { Environment.SetEnvironmentVariable("PHOTOREEL_FFMPEG", Path.Combine(h.Root, "missing-ffmpeg.exe")); await h.Client.PostAsync($"/api/projects/{id}/render", null); var j = await h.Wait(id); Assert.Equal("error", j["state"]!.GetValue<string>()); Assert.Equal("failed", new Project(id).Renders().Last().GetProperty("status").GetString()); }
        finally { Environment.SetEnvironmentVariable("PHOTOREEL_FFMPEG", previous); }
        await h.Client.PostAsync($"/api/projects/{id}/render", null); Assert.Equal("done", (await h.Wait(id))["state"]!.GetValue<string>());
    }
    [Fact]
    public async Task RestartMarksUnfinishedJobAndPreservesPlan()
    {
        await using var h = await Harness.Start(); var id = await h.Create(); await h.Upload(id, 1); _ = await h.SmallPlan(id); var project = new Project(id); var (n, dir) = project.NewRender(); Settings.WriteJson(Path.Combine(dir, "storyboard.json"), project.Plan()); Settings.WriteJson(Path.Combine(project.DirectoryPath, "job.json"), new RenderJob { State = "running", Render = n, Stage = "임의 중단", StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        var lifetime = h.App.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(); using var models = new VisionModels(); var fresh = new Jobs(models, lifetime); Assert.Equal("error", fresh.Get(project).State); Assert.Equal("interrupted", project.Renders().Last().GetProperty("status").GetString()); Assert.NotNull(project.Plan());
    }
    [Fact]
    public async Task FirstRestoreContainsRecoveredHistory()
    {
        await using var h = await Harness.Start(); using var models = new VisionModels(); var project = Project.Create();
        project.AddPhoto("synthetic.jpg", Fixtures.Jpeg(), models); var board = Planner.Build(project.Photos(), project.Embeddings(), new()); project.SavePlan(board);
        var (n, dir) = project.NewRender(); Settings.WriteJson(Path.Combine(dir, "storyboard.json"), board);
        Settings.WriteJson(Path.Combine(project.DirectoryPath, "job.json"), new RenderJob { State = "running", Render = n, Stage = "임의 중단", StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        var firstRestore = (await h.Client.GetFromJsonAsync<JsonObject>($"/api/projects/{project.Id}"))!;
        Assert.Equal("error", firstRestore["job"]!["state"]!.GetValue<string>());
        Assert.Equal("interrupted", firstRestore["renders"]!.AsArray().Last()!["status"]!.GetValue<string>());
    }
    [Fact]
    public async Task NullFieldsAreClientErrorsAndDoNotReplaceValidPlan()
    {
        await using var h = await Harness.Start(); var id = await h.Create(); await h.Upload(id, 1); var valid = await h.Plan(id);
        foreach (var field in new[] { "output", "music", "scenes", "style", "grade", "engine", "title", "concept" })
        {
            var invalid = valid.DeepClone().AsObject(); invalid[field] = null;
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PutAsJsonAsync($"/api/projects/{id}/plan", invalid)).StatusCode);
        }
        foreach (var field in new[] { "concept", "title", "aspect" })
        {
            var invalid = new JsonObject { [field] = null };
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync($"/api/projects/{id}/plan", invalid)).StatusCode);
        }
        Assert.Equal(valid["total_duration"]!.GetValue<double>(), new Project(id).Plan()!.TotalDuration);
    }
    [Fact]
    public async Task CancellingQueuedJobDoesNotCancelRunningJob()
    {
        await using var h = await Harness.Start(); var a = await h.Create(); var b = await h.Create(); await h.Upload(a, 1); await h.Upload(b, 1); _ = await h.Plan(a); _ = await h.SmallPlan(b);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"/api/projects/{a}/render", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"/api/projects/{b}/render", null)).StatusCode);
        await h.Client.PostAsync($"/api/projects/{b}/render/cancel", null); Assert.Equal("cancelled", (await h.Wait(b))["state"]!.GetValue<string>());
        Assert.Equal("running", (await h.Client.GetFromJsonAsync<JsonObject>($"/api/projects/{a}/render"))!["state"]!.GetValue<string>());
        await h.Client.PostAsync($"/api/projects/{a}/render/cancel", null); Assert.Equal("cancelled", (await h.Wait(a))["state"]!.GetValue<string>());
    }
}
