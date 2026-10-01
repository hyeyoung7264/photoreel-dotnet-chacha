using System.Diagnostics;
using Photoreel;
using Xunit;

namespace Photoreel.Tests;

[Trait("Category", "Media")]
public class VideoAdapterTests
{
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
