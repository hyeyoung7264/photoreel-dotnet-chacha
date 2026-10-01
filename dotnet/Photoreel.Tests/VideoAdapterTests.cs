using System.Diagnostics;
using Photoreel;
using Xunit;

namespace Photoreel.Tests;

[Trait("Category", "Media")]
public class VideoAdapterTests
{
    static async Task RunFfmpeg(params string[] arguments)
    {
        var start = new ProcessStartInfo(Settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(3, 90)]
    public async Task FullResolutionCacheMeasuresDecodedSourceAndSupportsLongerScenes(int sourceSeconds, int frames)
    {
        var root = Fixtures.Work("video-full-" + sourceSeconds);
        var file = Path.Combine(root, "pattern.mp4");
        await RunFfmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30", "-t", sourceSeconds.ToString(), "-threads", "2", "-pix_fmt", "yuv420p", file);
        var existingCaches = Directory.GetFiles(Path.GetTempPath(), "photoreel-clip-*.raw").Order().ToArray();
        string cachePath;
        using (var clip = new VideoFileClip(file, 1080, 1920, 30, 3))
        {
            Assert.Equal(frames, clip.FrameCount); Assert.Equal((long)frames * 1080 * 1920 * 3, clip.CachedBytes);
            cachePath = Assert.Single(Directory.GetFiles(Path.GetTempPath(), "photoreel-clip-*.raw").Except(existingCaches));
            using var first = clip.Frame(0); using var last = clip.Frame(1); using var after = clip.Frame(2);
            Assert.Equal((1080, 1920), (first.Width, first.Height));
            Assert.NotEqual(Images.Bytes(first), Images.Bytes(last)); Assert.Equal(Images.Bytes(last), Images.Bytes(after));
        }
        Assert.False(File.Exists(cachePath));
        if (sourceSeconds != 1) return;
        var proxy = Path.Combine(root, "proxy.jpg"); File.WriteAllBytes(proxy, Fixtures.Jpeg());
        var photo = Fixtures.Photo("synthetic"); photo.Width = 320; photo.Height = 240;
        var board = new Storyboard { Output = new() { Width = 1080, Height = 1920, Fps = 30 }, Music = new() { Mode = "none", Bpm = 100 }, Scenes = [new() { PhotoId = photo.Id, Beats = 5 }] }.FinalizeBoard();
        Assert.Equal(3, board.TotalDuration);
        using var models = new VisionModels(); int calls = 0;
        var output = Path.Combine(root, "adapted-full.mp4");
        var stats = await Renderer.Render(board, new() { [photo.Id] = photo }, new() { [photo.Id] = proxy }, output, root, models,
            clipFactory: context => { calls++; return new VideoFileClip(file, context.OutWidth, context.OutHeight, context.Fps, context.Seconds); });
        Assert.Equal(1, calls); Assert.Equal(1080, stats["width"]);
        await RunFfmpeg("-v", "error", "-i", output, "-f", "null", "-");
        Assert.Equal(existingCaches, Directory.GetFiles(Path.GetTempPath(), "photoreel-clip-*.raw").Order().ToArray());
    }

    [Fact]
    public async Task ActualCacheLimitFailureDeletesTemporaryFileAndAllowsRetry()
    {
        var root = Fixtures.Work("video-cache-limit"); var file = Path.Combine(root, "pattern.mp4");
        await RunFfmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=12", "-t", "1", "-threads", "2", "-pix_fmt", "yuv420p", file);
        var before = Directory.GetFiles(Path.GetTempPath(), "photoreel-clip-*.raw").Order().ToArray();
        Assert.Throws<IOException>(() => new VideoFileClip(file, 240, 320, 12, 1, 240L * 320 * 3 * 2));
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "photoreel-clip-*.raw").Order().ToArray());
        using var retry = new VideoFileClip(file, 240, 320, 12, 1);
        Assert.Equal(12, retry.FrameCount);
    }

    [Fact]
    public async Task ExistingVideoAdapterDecodesCropAndClampsProgress()
    {
        var root = Fixtures.Work("video-adapter");
        var file = Path.Combine(root, "generated-test-pattern.mp4");
        var start = new ProcessStartInfo(Settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=12", "-t", "1", "-pix_fmt", "yuv420p", file }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var clip = new VideoFileClip(file, 240, 320, 12, 1);
        using var first = clip.Frame(0); using var last = clip.Frame(1); using var before = clip.Frame(-1); using var after = clip.Frame(2);
        Assert.Equal((240, 320), (first.Width, first.Height));
        Assert.Equal(Images.Bytes(first), Images.Bytes(before)); Assert.Equal(Images.Bytes(last), Images.Bytes(after));
        Assert.NotEqual(Images.Bytes(first), Images.Bytes(last));
        var image = Path.Combine(root, "proxy.jpg"); File.WriteAllBytes(image, Fixtures.Jpeg());
        var photo = Fixtures.Photo("synthetic"); photo.Width = 320; photo.Height = 240;
        var board = new Storyboard { Output = new() { Aspect = "test", Width = 240, Height = 320, Fps = 12 }, Music = new() { Mode = "none" }, Scenes = [new() { PhotoId = photo.Id, Beats = 2 }] }.FinalizeBoard();
        using var models = new VisionModels(); int calls = 0;
        var stats = await Renderer.Render(board, new() { [photo.Id] = photo }, new() { [photo.Id] = image }, Path.Combine(root, "adapted.mp4"), root, models,
            clipFactory: context => { calls++; return new VideoFileClip(file, context.OutWidth, context.OutHeight, context.Fps, context.Seconds); });
        Assert.Equal(1, calls); Assert.True(File.Exists(Path.Combine(root, "adapted.mp4"))); Assert.Equal(240, stats["width"]);
    }
}
