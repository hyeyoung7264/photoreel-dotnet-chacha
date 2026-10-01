using System.Diagnostics;
using OpenCvSharp;

namespace Photoreel;

/// <summary>Local video adapter for the original clip extension point. No generation service is connected.</summary>
public sealed class VideoFileClip : IFrameClip
{
    public const long MaximumCacheBytes = 8L * 1024 * 1024 * 1024;
    const int MaximumFrameBytes = 64 * 1024 * 1024;
    readonly FileStream cache;
    readonly object gate = new();
    readonly int width, height, frameSize, count;
    public int FrameCount => count;
    public long CachedBytes => cache.Length;

    public VideoFileClip(string path, int outWidth, int outHeight, int fps, double seconds, long maximumCacheBytes = MaximumCacheBytes)
    {
        if (outWidth < 1 || outHeight < 1 || fps < 1 || !double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("잘못된 영상 클립 설정");
        width = outWidth; height = outHeight; frameSize = checked(width * height * 3);
        if (frameSize > MaximumFrameBytes || maximumCacheBytes < frameSize || maximumCacheBytes > MaximumCacheBytes) throw new ArgumentException("영상 클립 캐시 설정이 안전 범위를 벗어났습니다.");
        var requested = checked((int)Math.Max(1, Math.Round(seconds * fps)));
        var start = new ProcessStartInfo(Settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-threads", "2", "-i", path, "-vf", $"fps={fps},scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},format=rgb24", "-frames:v", requested.ToString(), "-threads", "2", "-f", "rawvideo", "-" }) start.ArgumentList.Add(arg);
        var decoded = new FileStream(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "photoreel-clip-" + Guid.NewGuid().ToString("N") + ".raw"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("FFmpeg 실행 실패");
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                var buffer = new byte[65536]; int read;
                while ((read = process.StandardOutput.BaseStream.Read(buffer)) > 0)
                {
                    if (decoded.Position + read > maximumCacheBytes) throw new IOException("실제 디코딩된 영상 캐시가 디스크 상한을 초과했습니다.");
                    decoded.Write(buffer, 0, read);
                }
                process.WaitForExit();
                var error = stderr.GetAwaiter().GetResult();
                if (process.ExitCode != 0 || decoded.Length < frameSize || decoded.Length % frameSize != 0) throw new IOException("영상에서 프레임을 읽지 못함: " + error);
                count = checked((int)(decoded.Length / frameSize)); cache = decoded;
            }
            catch { if (!process.HasExited) process.Kill(true); process.WaitForExit(); _ = stderr.GetAwaiter().GetResult(); throw; }
        }
        catch { decoded.Dispose(); throw; }
    }

    public Mat Frame(double progress)
    {
        if (!double.IsFinite(progress)) throw new ArgumentException("잘못된 영상 진행도");
        lock (gate)
        {
            var index = (int)Math.Clamp(Math.Round(progress * (count - 1)), 0, count - 1);
            var bytes = new byte[frameSize]; cache.Position = (long)index * frameSize; cache.ReadExactly(bytes);
            using var wrapped = Mat.FromPixelData(height, width, MatType.CV_8UC3, bytes);
            return wrapped.Clone();
        }
    }
    public void Dispose() { lock (gate) cache.Dispose(); }
}
