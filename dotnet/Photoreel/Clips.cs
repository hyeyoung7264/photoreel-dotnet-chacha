using OpenCvSharp;
namespace Photoreel;

public interface IFrameClip : IDisposable { Mat Frame(double progress); }
public record ClipContext(Mat Image, Photo Analysis, Scene Scene, Style Style, double Seconds, int OutWidth, int OutHeight, int Fps, VisionModels Models, string? DepthCache);

public sealed class PhotoClip : IFrameClip
{
    readonly Mat source;
    readonly Mat? background, depth;
    readonly CameraPath path;
    readonly int ow, oh;
    readonly double plane;
    public CameraPath Path => path;
    public PhotoClip(Mat image, Photo photo, Scene scene, Style style, double seconds, int width, int height, VisionModels models, string? depthCache = null)
    {
        ow = width; oh = height;
        path = Camera.PlanPath(image.Width, image.Height, (double)ow / oh, scene.Motion, scene.Framing, seconds, style.ZoomRate, style.PanRate, photo.Focus, photo.Saliency, photo.Faces);
        double a = Camera.Affine(path, 0, image.Width, image.Height, ow, oh)[0], b = Camera.Affine(path, 1, image.Width, image.Height, ow, oh)[0], f = Math.Sqrt(Math.Min(a, b) * Math.Max(a, b));
        source = new Mat();
        if (f < .98) Cv2.Resize(image, source, new(Math.Max(2, (int)Math.Round(image.Width * f)), Math.Max(2, (int)Math.Round(image.Height * f))), 0, 0, InterpolationFlags.Area);
        else image.CopyTo(source);
        if (path.Framing == "fit-blur")
        {
            var (ww, wh) = Camera.CoverWindow(image.Width, image.Height, (double)ow / oh); int cw = (int)(ww * image.Width), ch = (int)(wh * image.Height);
            using var crop = new Mat(image, new Rect((image.Width - cw) / 2, (image.Height - ch) / 2, cw, ch)); using var small = new Mat();
            Cv2.Resize(crop, small, new(Math.Max(2, ow / 8), Math.Max(2, oh / 8)), 0, 0, InterpolationFlags.Area); Cv2.GaussianBlur(small, small, new(0, 0), Math.Max(2, ow / 8.0 / 18));
            background = new Mat(); Cv2.Resize(small, background, new(ow, oh), 0, 0, InterpolationFlags.Cubic); Cv2.ConvertScaleAbs(background, background, .62);
        }
        if (depthCache is null) return;
        using var d = File.Exists(depthCache) ? LoadDepth(depthCache) : models.EstimateDepth(image);
        if (!File.Exists(depthCache)) Npy.Write(depthCache, Images.Floats(d), [d.Height, d.Width], true);
        depth = new Mat(); int sw = source.Width, sh = source.Height; Cv2.Resize(d, depth, new(sw, sh), 0, 0, InterpolationFlags.Cubic);
        int k = Math.Max(3, (int)(sw * .004) | 1); using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new(k, k)); Cv2.Dilate(depth, depth, kernel); Cv2.GaussianBlur(depth, depth, new(0, 0), Math.Max(1, sw * .004));
        foreach (var face in photo.Faces)
        {
            int x0 = (int)Math.Max(0, (face.X - .3 * face.W) * sw), x1 = (int)Math.Min(sw, (face.X + 1.3 * face.W) * sw), y0 = (int)Math.Max(0, (face.Y - .4 * face.H) * sh), y1 = (int)Math.Min(sh, (face.Y + 1.4 * face.H) * sh);
            if (x1 - x0 > 2 && y1 - y0 > 2) { using var region = new Mat(depth, new Rect(x0, y0, x1 - x0, y1 - y0)); using var contiguous = region.Clone(); region.SetTo(new Scalar(VisionModels.Percentile(Images.Floats(contiguous).Order().ToArray(), .5))); }
        }
        if (photo.Faces.Count > 0) Cv2.GaussianBlur(depth, depth, new(0, 0), Math.Max(1, sw * .006));
        var values = Images.Floats(depth).Select(x => Math.Clamp(x, 0, 1)).ToArray(); depth.Dispose(); depth = Images.FloatMat(values, sh, sw); plane = VisionModels.Percentile(values.Order().ToArray(), .5);
    }
    static Mat LoadDepth(string path) { var a = Npy.Read(path); if (a.Shape.Length != 2) throw new InvalidDataException("잘못된 깊이 캐시"); return Images.FloatMat(a.Values, a.Shape[0], a.Shape[1]); }
    public Mat Frame(double progress)
    {
        var affine = Camera.Affine(path, progress, source.Width, source.Height, ow, oh); using var m = Camera.Matrix(affine);
        if (depth is null || background is not null) { var frame = background?.Clone() ?? new Mat(); Cv2.WarpAffine(source, frame, m, new(ow, oh), InterpolationFlags.Cubic, background is null ? BorderTypes.Replicate : BorderTypes.Transparent); return frame; }
        using var rel = new Mat(); Cv2.WarpAffine(depth, rel, m, new(ow, oh), InterpolationFlags.Linear, BorderTypes.Replicate); var d = Images.Floats(rel); var mx = new float[ow * oh]; var my = new float[ow * oh];
        double sx = affine[0], sy = affine[4], tx = affine[2], ty = affine[5], k = 2 * Math.Clamp(progress, 0, 1) - 1;
        for (int y = 0; y < oh; y++) for (int x = 0; x < ow; x++)
        {
            int i = y * ow + x; double value = d[i] - plane, bx = (x - tx) / sx, by = (y - ty) / sy;
            if (path.Motion is "pan-up" or "pan-down") by += value * (path.Motion == "pan-down" ? 1 : -1) * k * .013 * ow / sy;
            else if (path.Motion is "zoom-in" or "zoom-out") { double g = value * (path.Motion == "zoom-in" ? 1 : -1) * k * .035; bx -= g * (x - ow / 2.0) / sx; by -= g * (y - oh / 2.0) / sy; }
            else bx += value * (path.Motion == "pan-left" ? -1 : 1) * k * .013 * ow / sx;
            mx[i] = (float)bx; my[i] = (float)by;
        }
        using var mapX = Images.FloatMat(mx, oh, ow); using var mapY = Images.FloatMat(my, oh, ow); var output = new Mat(); Cv2.Remap(source, output, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Reflect101); return output;
    }
    public void Dispose() { source.Dispose(); background?.Dispose(); depth?.Dispose(); }
}
