using OpenCvSharp;
namespace Photoreel;

public record CameraPath(string Framing, string Motion, double[] Start, double[] End);
public static class Camera
{
    public static (double W, double H) CoverWindow(int w, int h, double aspect) => w / (double)h > aspect ? (h * aspect / w, 1) : (1, w / aspect / h);
    public static double[]? FaceBox(List<Face> faces) => faces.Count == 0 ? null : [Math.Max(0, faces.Min(f => f.X - .45 * f.W)), Math.Max(0, faces.Min(f => f.Y - .7 * f.H)), Math.Min(1, faces.Max(f => f.X + 1.45 * f.W)), Math.Min(1, faces.Max(f => f.Y + 2.1 * f.H))];
    public static (double X, double Y, double Mass) BestCenter(double[][] sal, double ww, double wh)
    {
        const int n = 96; var raw = sal.SelectMany(x => x).Select(x => (float)x).ToArray();
        using var source = Images.FloatMat(raw, sal.Length, sal[0].Length); using var up = new Mat(); Cv2.Resize(source, up, new(n, n), 0, 0, InterpolationFlags.Linear);
        var pixels = Images.Floats(up); double sum = pixels.Sum(x => (double)x) + 1e-8; var ii = new double[n + 1, n + 1];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) ii[y + 1, x + 1] = pixels[y * n + x] / sum + ii[y, x + 1] + ii[y + 1, x] - ii[y, x];
        int kw = Math.Clamp((int)Math.Round(ww * n), 1, n), kh = Math.Clamp((int)Math.Round(wh * n), 1, n), cols = n - kw + 1, rows = n - kh + 1;
        double best = double.NegativeInfinity, mass = 0; int bx = 0, by = 0;
        for (int y = 0; y < rows; y++) for (int x = 0; x < cols; x++) { double m = ii[y + kh, x + kw] - ii[y, x + kw] - ii[y + kh, x] + ii[y, x]; double pull = -1e-4 * (Math.Pow((x - (cols - 1) / 2.0) / n, 2) + Math.Pow((y - (rows - 1) / 2.0) / n, 2)); if (m + pull > best) { best = m + pull; bx = x; by = y; mass = m; } }
        return ((bx + kw / 2.0) / n, (by + kh / 2.0) / n, mass);
    }
    public static (string, string) ChooseFraming(Photo photo, double aspect)
    {
        var (ww, wh) = CoverWindow(photo.Width, photo.Height, aspect); var box = FaceBox(photo.Faces);
        if (box is not null) return box[2] - box[0] <= ww * .96 && box[3] - box[1] <= wh * .96 ? ("cover", "얼굴이 모두 화면 안에 들어와 가득 채움") : ("fit-blur", "화면을 채우면 얼굴이 잘려 넓게 보임");
        return ww * wh >= .5 ? ("cover", "잘리는 부분이 적어 가득 채움") : ww * wh < .3 ? ("fit-blur", "가로로 매우 긴 사진이라 넓게 보임") : ("cover", "화면을 가득 채우고 잘리는 방향으로 훑음");
    }
    public static CameraPath PlanPath(int w, int h, double aspect, string motion, string framing, double seconds, double zoomRate, double panRate, double[] focus, double[][] sal, List<Face> faces)
    {
        double zoom = Math.Min(1.35, Math.Pow(1 + zoomRate, seconds)); var box = FaceBox(faces);
        if (framing == "fit-blur")
        {
            double ratio = (double)w / h, fw = ratio > aspect ? 1 : aspect / ratio, fh = ratio > aspect ? ratio / aspect : 1, step = Math.Min(1.12, Math.Pow(1 + zoomRate * .6, seconds));
            double lo = 1, hi = step, cx = .5, cy = .5;
            if (box is not null) { hi = Math.Clamp(Math.Min(.96 * fw / Math.Max(1e-6, box[2] - box[0]), .96 * fh / Math.Max(1e-6, box[3] - box[1])), 1, 1.3); lo = Math.Max(1, hi / step); cx = (box[0] + box[2]) / 2; cy = (box[1] + box[3]) / 2; }
            double[] a = [Math.Round(cx, 5), Math.Round(cy, 5), Math.Round(lo, 5)], b = [Math.Round(cx, 5), Math.Round(cy, 5), Math.Round(hi, 5)];
            return motion == "zoom-out" ? new("fit-blur", motion, b, a) : motion == "hold" ? new("fit-blur", motion, box is null ? a : b, box is null ? a : b) : new("fit-blur", "zoom-in", a, b);
        }
        var (ww, wh) = CoverWindow(w, h, aspect);
        if (box is not null) zoom = Math.Max(1, Math.Min(zoom, Math.Min(ww / Math.Max(1e-6, box[2] - box[0]), wh / Math.Max(1e-6, box[3] - box[1]))));
        double[] At(double cx, double cy, double z)
        {
            double fw = ww / z, fh = wh / z;
            if (box is not null) { cx = box[2] - box[0] <= fw ? Math.Clamp(cx, box[2] - fw / 2, box[0] + fw / 2) : (box[0] + box[2]) / 2; cy = box[3] - box[1] <= fh ? Math.Clamp(cy, box[3] - fh / 2, box[1] + fh / 2) : (box[1] + box[3]) / 2; }
            return [Math.Round(Math.Clamp(cx, fw / 2, 1 - fw / 2), 5), Math.Round(Math.Clamp(cy, fh / 2, 1 - fh / 2), 5), Math.Round(z, 5)];
        }
        var (wideX, wideY, _) = BestCenter(sal, ww, wh);
        if (motion is "pan-left" or "pan-right" or "pan-up" or "pan-down")
        {
            bool horizontal = motion is "pan-left" or "pan-right"; double z = 1, slack = 1 - (horizontal ? ww : wh);
            if (slack < .04) { z = 1.12; slack = 1 - (horizontal ? ww : wh) / z; }
            double size = (horizontal ? ww : wh) / z, dist = Math.Min(slack, panRate * seconds * size);
            if (box is not null) dist = Math.Min(dist, Math.Max(0, size - (horizontal ? box[2] - box[0] : box[3] - box[1])));
            if (dist >= .25 * panRate * seconds * size) { var (cx, cy, _) = BestCenter(sal, ww / z, wh / z); double mid = horizontal ? cx : cy, lo = Math.Clamp(mid - dist / 2, size / 2, Math.Max(size / 2, 1 - size / 2 - dist)); double a = lo, b = lo + dist; if (motion is "pan-left" or "pan-up") (a, b) = (b, a); return horizontal ? new("cover", motion, At(a, cy, z), At(b, cy, z)) : new("cover", motion, At(cx, a, z), At(cx, b, z)); }
            motion = "zoom-in";
        }
        if (motion == "hold" || zoom <= 1.004) { var p = At(wideX, wideY, 1); return new("cover", "hold", p, p); }
        var wide = At(wideX, wideY, 1); var tight = At(wideX + .7 * (focus[0] - wideX), wideY + .7 * (focus[1] - wideY), zoom);
        return motion == "zoom-out" ? new("cover", motion, tight, wide) : new("cover", "zoom-in", wide, tight);
    }
    public static (double X, double Y, double Z) Interpolate(CameraPath path, double p)
    {
        p = Math.Clamp(p, 0, 1); var a = path.Start; var b = path.End; double z = a[2] * Math.Pow(b[2] / a[2], p), q = Math.Abs(b[2] - a[2]) > 1e-6 ? (1 / a[2] - 1 / z) / (1 / a[2] - 1 / b[2]) : p;
        return (a[0] + (b[0] - a[0]) * q, a[1] + (b[1] - a[1]) * q, z);
    }
    public static double[] Affine(CameraPath path, double p, int sw, int sh, int ow, int oh)
    {
        var (cx, cy, z) = Interpolate(path, p);
        if (path.Framing == "fit-blur") { double s = Math.Min((double)ow / sw, (double)oh / sh) * z; double Offset(double c, int src, int dest) => src * s <= dest ? (dest - src * s) / 2 : Math.Clamp(dest / 2.0 - c * src * s, dest - src * s, 0); return [s, 0, Offset(cx, sw, ow), 0, s, Offset(cy, sh, oh)]; }
        var (ww, wh) = CoverWindow(sw, sh, (double)ow / oh); double winW = ww / z * sw, winH = wh / z * sh, sx = ow / winW, sy = oh / winH; return [sx, 0, -(cx * sw - winW / 2) * sx, 0, sy, -(cy * sh - winH / 2) * sy];
    }
    public static Mat Matrix(double[] coefficients) { using var matrix = Mat.FromPixelData(2, 3, MatType.CV_64FC1, coefficients); return matrix.Clone(); }
}
