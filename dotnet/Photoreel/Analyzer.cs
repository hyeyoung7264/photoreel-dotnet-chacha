using OpenCvSharp;
namespace Photoreel;

public static class Analyzer
{
    public static (Photo, float[]) Analyze(string id, string original, string proxy, string filename, VisionModels models)
    {
        using var full = Images.Load(original); using var rgb = Images.Resize(full, 3200); Images.SaveJpeg(rgb, proxy);
        int width = rgb.Width, height = rgb.Height; using var small = new Mat(); using var gray = new Mat(); using var lap = new Mat();
        Cv2.Resize(rgb, small, new(512, Math.Max(1, (int)Math.Round(512.0 * height / width))), 0, 0, InterpolationFlags.Area); Cv2.CvtColor(small, gray, ColorConversionCodes.RGB2GRAY); Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var sharp); var mean = Cv2.Mean(small); double brightness = Cv2.Mean(gray).Val0 / 255, warmth = (mean.Val0 - mean.Val2) / 255;
        var (faces, faceMethod) = models.DetectFaces(rgb); var sal = Saliency(rgb, faces); double fx = 0, fy = 0;
        for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++) { fx += sal[y][x] * (x + .5) / 24; fy += sal[y][x] * (y + .5) / 24; }
        double area = faces.Sum(f => f.W * f.H); if (faces.Count > 0) { fx = faces.Sum(f => (f.X + f.W / 2) * f.W * f.H) / area; fy = faces.Sum(f => (f.Y + f.H / 2) * f.W * f.H) / area; }
        var semantic = models.Semantic(rgb, brightness, warmth);
        var photo = new Photo { Id = id, Filename = filename, Width = width, Height = height, TakenAt = Images.TakenAt(original), Sharpness = Math.Round(sharp.Val0 * sharp.Val0, 1), Brightness = Math.Round(brightness, 3), Warmth = Math.Round(warmth, 3), Colorfulness = Math.Round(Colorfulness(small), 3), Faces = faces, FaceArea = Math.Round(area, 4), Focus = [Math.Round(fx, 3), Math.Round(fy, 3)], Saliency = sal.Select(row => row.Select(v => Math.Round(v, 5)).ToArray()).ToArray(), Tags = semantic.Tags, Methods = new() { ["faces"] = faceMethod, ["semantic"] = semantic.Method } };
        return (photo, semantic.Emb);
    }
    public static double[][] Saliency(Mat rgb, List<Face> faces)
    {
        const int n = 64, g = 24; using var small = new Mat(); using var gray = new Mat(); using var real = new Mat(); using var frequency = new Mat();
        Cv2.Resize(rgb, small, new(n, n), 0, 0, InterpolationFlags.Area); Cv2.CvtColor(small, gray, ColorConversionCodes.RGB2GRAY); gray.ConvertTo(real, MatType.CV_64FC1); Cv2.Dft(real, frequency, DftFlags.ComplexOutput);
        var spec = new double[n * n * 2]; System.Runtime.InteropServices.Marshal.Copy(frequency.Data, spec, 0, spec.Length); var log = new double[n * n];
        for (int i = 0; i < log.Length; i++) log[i] = Math.Log(Math.Sqrt(spec[i * 2] * spec[i * 2] + spec[i * 2 + 1] * spec[i * 2 + 1]) + 1e-8);
        using var raw = Mat.FromPixelData(n, n, MatType.CV_64FC1, log); using var blurred = new Mat(); Cv2.Blur(raw, blurred, new(3, 3)); var average = new double[n * n]; System.Runtime.InteropServices.Marshal.Copy(blurred.Data, average, 0, average.Length);
        var values = new double[n * n * 2]; for (int i = 0; i < log.Length; i++) { double magnitude = Math.Exp(log[i] - average[i]), phase = Math.Atan2(spec[i * 2 + 1], spec[i * 2]); values[i * 2] = magnitude * Math.Cos(phase); values[i * 2 + 1] = magnitude * Math.Sin(phase); }
        using var residual = Mat.FromPixelData(n, n, MatType.CV_64FC2, values); using var inverse = new Mat(); Cv2.Dft(residual, inverse, DftFlags.Inverse | DftFlags.Scale | DftFlags.ComplexOutput); System.Runtime.InteropServices.Marshal.Copy(inverse.Data, values, 0, values.Length);
        var sal = new float[n * n]; for (int i = 0; i < sal.Length; i++) sal[i] = (float)(values[i * 2] * values[i * 2] + values[i * 2 + 1] * values[i * 2 + 1]);
        using var map = Images.FloatMat(sal, n, n); Cv2.GaussianBlur(map, map, new(0, 0), 3); sal = Images.Floats(map); double maximum = sal.Max() + 1e-8;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { double xx = (double)x / (n - 1), yy = (double)y / (n - 1); sal[y * n + x] = (float)(sal[y * n + x] / maximum * (.6 + .4 * Math.Exp(-(Math.Pow(xx - .5, 2) + Math.Pow(yy - .5, 2)) / .18))); }
        using var centered = Images.FloatMat(sal, n, n); using var resized = new Mat(); Cv2.Resize(centered, resized, new(g, g), 0, 0, InterpolationFlags.Area); var data = Images.Floats(resized).Select(x => (double)x).ToArray(); double sum = data.Sum() + 1e-8; for (int i = 0; i < data.Length; i++) data[i] /= sum;
        if (faces.Count > 0) { var faceMap = new double[g * g]; foreach (var f in faces) { int x0 = (int)Math.Clamp((f.X - .25 * f.W) * g, 0, g - 1), x1 = (int)Math.Clamp(Math.Ceiling((f.X + 1.25 * f.W) * g), 1, g), y0 = (int)Math.Clamp((f.Y - .35 * f.H) * g, 0, g - 1), y1 = (int)Math.Clamp(Math.Ceiling((f.Y + 2.2 * f.H) * g), 1, g); for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) faceMap[y * g + x] += f.W * f.H; } double mass = faceMap.Sum(); if (mass > 0) for (int i = 0; i < data.Length; i++) data[i] = .3 * data[i] + .7 * faceMap[i] / mass; }
        sum = data.Sum() + 1e-8; return Enumerable.Range(0, g).Select(y => data.Skip(y * g).Take(g).Select(x => x / sum).ToArray()).ToArray();
    }
    static double Colorfulness(Mat rgb)
    {
        var bytes = Images.Bytes(rgb); int n = bytes.Length / 3; double rgs = 0, ybs = 0, rg2 = 0, yb2 = 0;
        for (int i = 0; i < bytes.Length; i += 3) { double rg = bytes[i] - bytes[i + 1], yb = .5 * (bytes[i] + bytes[i + 1]) - bytes[i + 2]; rgs += rg; ybs += yb; rg2 += rg * rg; yb2 += yb * yb; }
        return (Math.Sqrt(Math.Max(0, rg2 / n - Math.Pow(rgs / n, 2)) + Math.Max(0, yb2 / n - Math.Pow(ybs / n, 2))) + .3 * Math.Sqrt(Math.Pow(rgs / n, 2) + Math.Pow(ybs / n, 2))) / 255;
    }
}
