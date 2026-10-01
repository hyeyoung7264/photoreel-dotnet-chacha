using System.Text.Json;
using Photoreel;
using Xunit;

namespace Photoreel.Tests;

[Trait("Category", "Reference")]
public class ReferenceTests
{
    [Fact]
    public void SavedPythonSyntheticPlanAndAnalysisAgreeWithoutExecutingPython()
    {
        Settings.Root = Fixtures.Root;
        var root = Path.Combine(Settings.Root, "evidence", "reference");
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime-results.json")));
        var baseline = results.RootElement.GetProperty("reel_automatic_plan").Deserialize<Storyboard>(Settings.Json)!;
        var project = Path.Combine(root, "reel-data", "projects", "fb7c1b1256c5");
        var photos = Settings.ReadJson<List<Photo>>(Path.Combine(project, "photos.json"))!;
        var embeddings = photos.ToDictionary(p => p.Id, p => Npy.Read(Path.Combine(project, "emb", p.Id + ".npy")).Values);
        var actual = Planner.Build(photos, embeddings, new() { Concept = baseline.Concept });
        Assert.Equal(JsonSerializer.Serialize(baseline, Settings.Json), JsonSerializer.Serialize(actual, Settings.Json));

        var prior = Environment.GetEnvironmentVariable("PHOTOREEL_NO_MODELS");
        var metrics = new List<object>();
        try
        {
            Environment.SetEnvironmentVariable("PHOTOREEL_NO_MODELS", "1");
            using var models = new VisionModels();
            foreach (var photo in photos)
            {
                var original = Directory.GetFiles(Path.Combine(project, "orig"), photo.Id + ".*").Single();
                var proxy = Path.Combine(root, "reanalysis", photo.Id + ".jpg");
                var (analysis, emb) = Analyzer.Analyze(photo.Id, original, proxy, photo.Filename, models);
                var cosine = VisionModels.Dot(embeddings[photo.Id], emb);
                var sharpness = Math.Abs(analysis.Sharpness - photo.Sharpness);
                var brightness = Math.Abs(analysis.Brightness - photo.Brightness);
                var warmth = Math.Abs(analysis.Warmth - photo.Warmth);
                var saliency = analysis.Saliency.SelectMany(r => r).Zip(photo.Saliency.SelectMany(r => r), (a, b) => Math.Abs(a - b)).Max();
                metrics.Add(new { fixture = photo.Filename, cosine, sharpness, brightness, warmth, saliency });
                Assert.Equal((photo.Width, photo.Height), (analysis.Width, analysis.Height));
                Assert.InRange(cosine, .999, 1.00001);
                Assert.InRange(brightness, 0, .005);
                Assert.InRange(warmth, 0, .005);
            }
        }
        finally { Environment.SetEnvironmentVariable("PHOTOREEL_NO_MODELS", prior); Settings.WriteJson(Path.Combine(root, "parity.json"), new { exactPlan = true, baselineUsesClip = false, pythonExecuted = false, metrics }); }
    }
}
