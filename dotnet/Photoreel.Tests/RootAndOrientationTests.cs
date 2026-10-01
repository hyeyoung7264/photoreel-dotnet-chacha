using ImageMagick;
using OpenCvSharp;
using Photoreel;
using Xunit;

namespace Photoreel.Tests;

[Trait("Category", "Unit")]
public class RootTests
{
    [Fact]
    public async Task AtomicJsonReplacementSurvivesShortWindowsReaderLock()
    {
        var root = Fixtures.Work("atomic-json"); var file = Path.Combine(root, "value.json"); Settings.WriteJson(file, new { value = 1 });
        var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(50); reader.Dispose(); });
        Settings.WriteJson(file, new { value = 2 }); await release;
        Assert.Equal(2, Settings.ReadJson<System.Text.Json.JsonElement>(file).GetProperty("value").GetInt32());
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public void AssetPresenceDoesNotMoveSourceOrPublishedDataRoot()
    {
        var root = Fixtures.Work("stable-root");
        var project = Path.Combine(root, "dotnet", "Photoreel"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Photoreel.csproj"), "<Project />");
        var bin = Path.Combine(project, "bin", "Debug", "net10.0"); Directory.CreateDirectory(bin);
        var publish = Path.Combine(root, "publish"); Directory.CreateDirectory(publish);
        Assert.Equal(root, Settings.FindRoot(bin)); Assert.Equal(publish, Settings.FindRoot(publish));
        Directory.CreateDirectory(Path.Combine(root, "assets_cache"));
        Assert.Equal(root, Settings.FindRoot(bin)); Assert.Equal(publish, Settings.FindRoot(publish));
    }
}

[Trait("Category", "Media")]
public class OrientationPixelTests
{
    [Theory]
    [InlineData(1, 0, 1, 2, 3)]
    [InlineData(2, 1, 0, 3, 2)]
    [InlineData(3, 3, 2, 1, 0)]
    [InlineData(4, 2, 3, 0, 1)]
    [InlineData(5, 0, 2, 1, 3)]
    [InlineData(6, 2, 0, 3, 1)]
    [InlineData(7, 3, 1, 2, 0)]
    [InlineData(8, 1, 3, 0, 2)]
    public void ExifMirrorsAndRotationsMoveActualCorners(int orientation, int tl, int tr, int bl, int br)
    {
        var root = Fixtures.Work("orientation-pixels");
        Vec3b[] colors = [new(230, 20, 20), new(20, 230, 20), new(20, 20, 230), new(230, 230, 20)];
        using var rgb = new Mat(80, 120, MatType.CV_8UC3);
        for (int y = 0; y < 80; y++) for (int x = 0; x < 120; x++) rgb.Set(y, x, colors[(y >= 40 ? 2 : 0) + (x >= 60 ? 1 : 0)]);
        var file = Path.Combine(root, "corners.jpg"); Images.SaveJpeg(rgb, file);
        using (var image = new MagickImage(file))
        {
            var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)orientation);
            image.SetProfile(exif); image.Orientation = (OrientationType)orientation; image.Write(file);
        }
        using var decoded = Images.Load(file);
        var points = new[] { (8, 8, tl), (decoded.Width - 9, 8, tr), (8, decoded.Height - 9, bl), (decoded.Width - 9, decoded.Height - 9, br) };
        foreach (var (x, y, expected) in points)
        {
            var actual = decoded.At<Vec3b>(y, x);
            for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(actual[c] - colors[expected][c]), 0, 12);
        }
    }
}
