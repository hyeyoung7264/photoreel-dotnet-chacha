using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using Photoreel;
using Xunit;

namespace Photoreel.Tests;

[Trait("Category", "RealModels")]
public class RealModelTests
{
    [Fact]
    public async Task ActualModelsAndBothEnginesExportDecodableVideo()
    {
        var root = Fixtures.Work("real-models");
        var file = Path.Combine(root, "synthetic.jpg");
        File.WriteAllBytes(file, Fixtures.Jpeg());
        using var rgb = Images.Load(file);
        using var models = new VisionModels();
        Assert.True(models.EnsureClip(), models.ClipError);
        var semantic = models.Semantic(rgb, .5, 0);
        Assert.Equal("clip", semantic.Method);
        Assert.Equal(512, semantic.Emb.Length);
        Assert.InRange(Math.Sqrt(semantic.Emb.Sum(v => (double)v * v)), .99999, 1.00001);
        Assert.All(semantic.Emb, v => Assert.True(float.IsFinite(v)));
        Assert.Equal("yunet", models.DetectFaces(rgb).Item2);
        using var depth = models.EstimateDepth(rgb);
        Assert.Equal((518, 392), (depth.Width, depth.Height));
        Assert.Equal(0, Images.Floats(depth).Min());
        Assert.Equal(1, Images.Floats(depth).Max());
        var photo = Fixtures.Photo("synthetic");
        photo.Width = rgb.Width; photo.Height = rgb.Height;
        foreach (var engine in new[] { "edit", "parallax" })
        {
            var board = new Storyboard { Engine = engine, Output = new() { Aspect = "test", Width = 240, Height = 320, Fps = 12 }, Scenes = [new() { PhotoId = photo.Id, Beats = 2 }] }.FinalizeBoard();
            var work = Path.Combine(root, engine);
            var stats = await Renderer.Render(board, new() { [photo.Id] = photo }, new() { [photo.Id] = file }, Path.Combine(work, "video.mp4"), work, models);
            Settings.WriteJson(Path.Combine(work, "stats.json"), stats);
            var start = new ProcessStartInfo(Settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-i", Path.Combine(work, "video.mp4"), "-f", "null", "-" }) start.ArgumentList.Add(arg);
            using var decoder = Process.Start(start)!;
            var error = await decoder.StandardError.ReadToEndAsync();
            await decoder.WaitForExitAsync();
            Assert.True(decoder.ExitCode == 0, error);
        }
    }
}

[Trait("Category", "Unit")]
public class ImportTests
{
    [Fact]
    public async Task FailedImportLeavesNoPartialProjectAndSuccessfulImportKeepsNpy()
    {
        var root = Fixtures.Work("import");
        var previous = Environment.GetEnvironmentVariable("PHOTOREEL_DATA");
        var source = Path.Combine(root, "source", "abcdef123456");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Settings.WriteJson(Path.Combine(source, "photos.json"), new[] { Fixtures.Photo("abcdef1234") });
        try
        {
            Environment.SetEnvironmentVariable("PHOTOREEL_DATA", target);
            Assert.Equal(1, await Cli.Run(["--import-project", source]));
            Assert.False(Directory.Exists(Path.Combine(target, "projects", "abcdef123456")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(target, "projects")));
            Directory.CreateDirectory(Path.Combine(source, "proxy"));
            File.WriteAllBytes(Path.Combine(source, "proxy", "abcdef1234.jpg"), Fixtures.Jpeg());
            Npy.Write(Path.Combine(source, "emb", "abcdef1234.npy"), [1, 0], [2]);
            Assert.Equal(0, await Cli.Run(["--import-project", source]));
            Assert.Equal(new float[] { 1, 0 }, new Project("abcdef123456").Embeddings()["abcdef1234"]);
            Assert.Equal(1, await Cli.Run(["--import-project", source]));
        }
        finally { Environment.SetEnvironmentVariable("PHOTOREEL_DATA", previous); }
    }
}
