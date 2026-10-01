using System.Diagnostics;
using Photoreel;
using Xunit;

namespace Photoreel.Tests;

// Opt-in local evaluation. No private inputs or outputs belong in the repository.
[Trait("Category", "PrivatePhotos")]
public class PrivatePhotoTests
{
    [Fact]
    public async Task LocalPhotoGroupsExportAllAspectsWithActualModelsAndFullSizeOutput()
    {
        Settings.Root = Fixtures.Root;
        var root = Path.Combine(Settings.Root, "evidence", "private");
        var prior = Environment.GetEnvironmentVariable("PHOTOREEL_DATA");
        var report = new List<object>();
        try
        {
            Environment.SetEnvironmentVariable("PHOTOREEL_DATA", Path.Combine(root, "evaluation"));
            using var models = new VisionModels();
            Assert.True(models.EnsureClip(), models.ClipError);
            foreach (var group in new[] { "group-a", "group-b", "group-c" })
            {
                var project = Project.Create();
                foreach (var file in Directory.GetFiles(Path.Combine(root, group), "*.jpg")) project.AddPhoto(Path.GetFileName(file), File.ReadAllBytes(file), models);
                var photos = project.Photos();
                Assert.All(photos, p => { Assert.Equal("clip", p.Methods["semantic"]); Assert.Equal("yunet", p.Methods["faces"]); Assert.NotNull(p.TakenAt); });
                var plan = Planner.Build(photos, project.Embeddings(), new() { Concept = "여행 기록, 시간순으로", TargetDuration = 5 });
                project.SavePlan(plan);
                var byId = photos.ToDictionary(p => p.Id);
                var proxies = photos.ToDictionary(p => p.Id, p => project.Proxy(p.Id));
                var exports = new List<object>();
                foreach (var (aspect, width, height) in new[] { ("9:16", 240, 426), ("4:5", 240, 300), ("1:1", 240, 240), ("16:9", 426, 240) })
                    foreach (var engine in new[] { "edit", "parallax" })
                    {
                        plan.Engine = engine;
                        plan.Output = new() { Aspect = "evaluation", Width = width, Height = height, Fps = 12 };
                        plan.FinalizeBoard();
                        var work = Path.Combine(root, "exports", group, aspect.Replace(':', '-') + "-" + engine);
                        var stats = await Renderer.Render(plan, byId, proxies, Path.Combine(work, "video.mp4"), work, models);
                        await Decode(Path.Combine(work, "video.mp4"));
                        exports.Add(new { aspect, engine, stats });
                    }
                if (group == "group-a")
                {
                    plan.Engine = "edit"; plan.Output = new() { Aspect = "9:16", Fps = 30 }; plan.FinalizeBoard();
                    var work = Path.Combine(root, "exports", group, "full-size");
                    var stats = await Renderer.Render(plan, byId, proxies, Path.Combine(work, "video.mp4"), work, models);
                    await Decode(Path.Combine(work, "video.mp4"));
                    exports.Add(new { aspect = "9:16", engine = "edit", stats });
                }
                report.Add(new { group, count = photos.Count, detectedFaces = photos.Sum(p => p.Faces.Count), classified = photos.Count, chronological = plan.OrderMode == "chronological", exports });
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PHOTOREEL_DATA", prior);
            Settings.WriteJson(Path.Combine(root, "evaluation-summary.json"), new { externalTransmission = false, visualQualityReviewed = false, report });
        }
    }
    static async Task Decode(string file)
    {
        var start = new ProcessStartInfo(Settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-i", file, "-f", "null", "-" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, stderr);
    }
}
