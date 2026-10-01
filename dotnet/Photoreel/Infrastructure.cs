using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageMagick;
using OpenCvSharp;

namespace Photoreel;

public static class Settings
{
    public static string FindRoot(string? baseDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable("PHOTOREEL_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var executableRoot = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        for (var directory = new DirectoryInfo(executableRoot); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "dotnet", "Photoreel", "Photoreel.csproj")) &&
               executableRoot.StartsWith(Path.Combine(directory.FullName, "dotnet", "Photoreel", "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        return executableRoot;
    }
    public static string Root { get; set; } = Directory.GetCurrentDirectory();
    public static string Data => Path.GetFullPath(Environment.GetEnvironmentVariable("PHOTOREEL_DATA") ?? Path.Combine(Root, "data-dotnet"));
    public static string Assets => Path.GetFullPath(Environment.GetEnvironmentVariable("PHOTOREEL_ASSETS") ?? Path.Combine(Root, "assets_cache"));
    public static string Ffmpeg => Environment.GetEnvironmentVariable("PHOTOREEL_FFMPEG") ?? Path.Combine(Root, "native", "ffmpeg.exe");
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Asset(string name) => Path.Combine(Assets, name);
    public static bool ModelsDisabled => Environment.GetEnvironmentVariable("PHOTOREEL_NO_MODELS") == "1";
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); break; }
                catch (Exception e) when (attempt < 8 && (e is IOException or UnauthorizedAccessException) && ((e.HResult & 0xffff) is 5 or 32 or 33)) { Thread.Sleep(10 * (attempt + 1)); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json);
    }
}

public static class Images
{
    public static Mat Load(string path)
    {
        using var image = new MagickImage(path);
        if ((ulong)image.Width * image.Height > 100_000_000) throw new InvalidDataException("이미지가 너무 큽니다 (100MP 초과).");
        image.AutoOrient(); image.ColorSpace = ColorSpace.sRGB; image.Alpha(AlphaOption.Remove); image.Depth = 8;
        using var pixels = image.GetPixels();
        var bytes = pixels.ToByteArray(PixelMapping.RGB)!;
        using var wrapped = Mat.FromPixelData((int)image.Height, (int)image.Width, MatType.CV_8UC3, bytes);
        return wrapped.Clone();
    }
    public static string? TakenAt(string path)
    {
        try
        {
            using var im = new MagickImage(path); var exif = im.GetExifProfile();
            var text = exif?.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif?.GetValue(ExifTag.DateTimeDigitized)?.Value ?? exif?.GetValue(ExifTag.DateTime)?.Value;
            return DateTime.TryParseExact(text?.Trim()[..Math.Min(19, text.Trim().Length)], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value.ToString("yyyy-MM-ddTHH:mm:ss") : null;
        }
        catch { return null; }
    }
    public static Mat Resize(Mat rgb, int max)
    {
        var scale = Math.Min(1.0, (double)max / Math.Max(rgb.Width, rgb.Height));
        var result = new Mat(); Cv2.Resize(rgb, result, new Size(Math.Max(1, (int)Math.Round(rgb.Width * scale)), Math.Max(1, (int)Math.Round(rgb.Height * scale))), 0, 0, InterpolationFlags.Lanczos4); return result;
    }
    public static void SaveJpeg(Mat rgb, string path, int quality = 95)
    {
        using var bgr = new Mat(); Cv2.CvtColor(rgb, bgr, ColorConversionCodes.RGB2BGR);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Cv2.ImWrite(path, bgr, [new ImageEncodingParam(ImwriteFlags.JpegQuality, quality)]);
    }
    public static byte[] Bytes(Mat mat) { var bytes = new byte[checked((int)(mat.Total() * mat.ElemSize()))]; System.Runtime.InteropServices.Marshal.Copy(mat.Data, bytes, 0, bytes.Length); return bytes; }
    public static float[] Floats(Mat mat) { var data = new float[checked((int)mat.Total())]; System.Runtime.InteropServices.Marshal.Copy(mat.Data, data, 0, data.Length); return data; }
    public static Mat FloatMat(float[] data, int rows, int cols) { using var mat = Mat.FromPixelData(rows, cols, MatType.CV_32FC1, data); return mat.Clone(); }
}

public record NpyArray(float[] Values, int[] Shape);
public static class Npy
{
    public static NpyArray Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(6).SequenceEqual(new byte[] { 0x93, 78, 85, 77, 80, 89 })) throw new InvalidDataException("NPY magic");
        int major = reader.ReadByte(); reader.ReadByte();
        var length = major == 1 ? reader.ReadUInt16() : checked((int)reader.ReadUInt32());
        var header = Encoding.UTF8.GetString(reader.ReadBytes(length));
        if (Regex.IsMatch(header, "'fortran_order':\\s*True")) throw new InvalidDataException("Fortran NPY unsupported");
        var type = Regex.Match(header, "['\"]descr['\"]:\\s*['\"]([^'\"]+)").Groups[1].Value;
        var shape = Regex.Match(header, "['\"]shape['\"]:\\s*\\(([^)]*)\\)").Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        var count = shape.Aggregate(1, checked((a, b) => a * b)); if (count < 0 || count > 100_000_000) throw new InvalidDataException("NPY size");
        if (type is not "<f4" and not "<f2" and not "|f4" and not "|f2") throw new InvalidDataException("NPY dtype " + type);
        var values = new float[count]; for (int i = 0; i < count; i++) values[i] = type.EndsWith("f2") ? (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()) : reader.ReadSingle();
        return new(values, shape);
    }
    public static void Write(string path, float[] values, int[] shape, bool half = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dims = string.Join(", ", shape) + (shape.Length == 1 ? "," : "");
        var header = $"{{'descr': '<f{(half ? 2 : 4)}', 'fortran_order': False, 'shape': ({dims}), }}";
        header += new string(' ', (64 - (10 + header.Length + 1) % 64) % 64) + "\n";
        var temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp))) { writer.Write(new byte[] { 0x93, 78, 85, 77, 80, 89, 1, 0 }); writer.Write((ushort)header.Length); writer.Write(Encoding.ASCII.GetBytes(header)); foreach (var v in values) { if (half) writer.Write(BitConverter.HalfToUInt16Bits((Half)v)); else writer.Write(v); } }
        File.Move(temp, path, true);
    }
}

/// <summary>Exact tokenizer.json byte-BPE configuration, implemented without a Python runtime.</summary>
public sealed class ClipTokenizer
{
    readonly Dictionary<string, long> vocab;
    readonly Dictionary<(string, string), int> ranks = new();
    readonly Dictionary<byte, string> bytes = new();
    readonly Dictionary<string, long[]> cache = new();
    readonly Regex split = new("<\\|startoftext\\|>|<\\|endoftext\\|>|'s|'t|'re|'ve|'m|'ll|'d|[\\p{L}]+|[\\p{N}]|[^\\s\\p{L}\\p{N}]+", RegexOptions.Compiled);
    public ClipTokenizer(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path)); var model = json.RootElement.GetProperty("model");
        vocab = model.GetProperty("vocab").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt64(), StringComparer.Ordinal);
        int rank = 0; foreach (var merge in model.GetProperty("merges").EnumerateArray()) { var pair = merge.ValueKind == JsonValueKind.String ? merge.GetString()!.Split(' ') : merge.EnumerateArray().Select(x => x.GetString()!).ToArray(); ranks[(pair[0], pair[1])] = rank++; }
        var printable = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToHashSet(); int extra = 0;
        for (int b = 0; b < 256; b++) bytes[(byte)b] = char.ConvertFromUtf32(printable.Contains(b) ? b : 256 + extra++);
    }
    public long[] Encode(string text)
    {
        text = Regex.Replace(text.Normalize(NormalizationForm.FormC), "\\s+", " ").ToLowerInvariant();
        var ids = new List<long> { 49406 };
        foreach (Match match in split.Matches(text))
        {
            var token = match.Value;
            if (token is "<|startoftext|>" or "<|endoftext|>") { ids.Add(vocab[token]); continue; }
            if (!cache.TryGetValue(token, out var encoded))
            {
                var parts = Encoding.UTF8.GetBytes(token).Select(x => bytes[x]).ToList(); parts[^1] += "</w>";
                while (parts.Count > 1)
                {
                    int best = int.MaxValue, index = -1; for (int i = 0; i < parts.Count - 1; i++) if (ranks.TryGetValue((parts[i], parts[i + 1]), out var r) && r < best) { best = r; index = i; }
                    if (index < 0) break;
                    var left = parts[index]; var right = parts[index + 1];
                    for (int i = 0; i < parts.Count - 1; i++) if (parts[i] == left && parts[i + 1] == right) { parts[i] += parts[i + 1]; parts.RemoveAt(i + 1); }
                }
                encoded = parts.Select(x => vocab[x]).ToArray(); cache[token] = encoded;
            }
            ids.AddRange(encoded);
        }
        ids.Add(49407); return ids.Take(77).ToArray(); // Original truncates after special-token processing.
    }
}
