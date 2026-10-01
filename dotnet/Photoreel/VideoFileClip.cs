using System.Diagnostics;
using OpenCvSharp;

namespace Photoreel;

/// <summary>Local video adapter for the original clip extension point. No generation service is connected.</summary>
public sealed class VideoFileClip : IFrameClip
{
    readonly byte[] frames;
    readonly int width, height, frameSize, count;

    public VideoFileClip(string path, int outWidth, int outHeight, int fps, double seconds)
    {
        if (outWidth < 1 || outHeight < 1 || fps < 1 || !double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("잘못된 영상 클립 설정");
        width = outWidth; height = outHeight; frameSize = checked(width * height * 3);
        var requested = checked((int)Math.Max(1, Math.Round(seconds * fps)));
        var maximum = checked((long)requested * frameSize);
        if (maximum > 512L * 1024 * 1024) throw new ArgumentException("영상 클립 디코드 메모리는 512MB 이하로 설정해 주세요.");
        var start = new ProcessStartInfo(Settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-i", path, "-vf", $"fps={fps},scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},format=rgb24", "-frames:v", requested.ToString(), "-f", "rawvideo", "-" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("FFmpeg 실행 실패");
        var stderr = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        try
        {
            process.StandardOutput.BaseStream.CopyTo(memory);
            process.WaitForExit();
            var error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || memory.Length < frameSize) throw new IOException("영상에서 프레임을 읽지 못함: " + error);
            frames = memory.ToArray(); count = frames.Length / frameSize;
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }

    public Mat Frame(double progress)
    {
        var index = (int)Math.Clamp(Math.Round(progress * (count - 1)), 0, count - 1);
        var bytes = frames.AsSpan(index * frameSize, frameSize).ToArray();
        using var wrapped = Mat.FromPixelData(height, width, MatType.CV_8UC3, bytes);
        return wrapped.Clone();
    }
    public void Dispose() { }
}
