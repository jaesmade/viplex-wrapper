using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ViPlex.Core;

namespace ViPlex.Windows;

internal static class VideoEditing
{
    private const int FramesPerSecond = 30;

    internal static async Task CreateAsync(string sourcePath, string outputPath, MediaKind kind,
        int sourceDurationMilliseconds, int trimStartMilliseconds, int trimEndMilliseconds,
        int fadeInMilliseconds, int fadeOutMilliseconds)
    {
        if (kind is not (MediaKind.Image or MediaKind.Video) || trimStartMilliseconds < 0 ||
            trimEndMilliseconds <= trimStartMilliseconds || trimEndMilliseconds > sourceDurationMilliseconds ||
            (kind == MediaKind.Image && trimStartMilliseconds != 0))
        {
            throw new ArgumentException("Choose a playback range within the source media.");
        }

        var duration = trimEndMilliseconds - trimStartMilliseconds;
        if (fadeInMilliseconds < 0 || fadeOutMilliseconds < 0 ||
            (long)fadeInMilliseconds + fadeOutMilliseconds > duration)
        {
            throw new ArgumentException("The fades must fit within the page duration.");
        }

        var executable = FindFfmpeg();
        var input = Path.GetFullPath(sourcePath);
        var output = Path.GetFullPath(outputPath);
        var directory = Directory.CreateDirectory(Path.GetDirectoryName(output)!).FullName;
        var encoded = Path.Combine(directory, $".render-{Guid.NewGuid():N}.mp4");
        try
        {
            var arguments = BuildArguments(input, encoded, kind, trimStartMilliseconds, duration,
                fadeInMilliseconds, fadeOutMilliseconds);
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                throw new IOException("FFmpeg could not start. Check that a Windows FFmpeg build is installed.", ex);
            }

            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var detail = (await errors.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(detail.Length == 0 ? "FFmpeg could not render this page."
                    : $"FFmpeg could not render this page.\n{detail[..Math.Min(detail.Length, 600)]}");
            }

            var inspected = MediaInspector.Inspect(encoded);
            if (inspected is not { Kind: MediaKind.Video, Width: ImageComposition.Width, Height: ImageComposition.Height })
            {
                throw new InvalidDataException("The rendered video does not match the display canvas.");
            }

            File.Move(encoded, output);
        }
        finally
        {
            try
            {
                File.Delete(encoded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The caller owns the export staging directory and will clean up any partial render.
            }
        }
    }

    private static List<string> BuildArguments(string input, string output, MediaKind kind, int trimStart,
        int duration, int fadeIn, int fadeOut)
    {
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-xerror"];
        if (kind == MediaKind.Image)
        {
            args.AddRange(["-loop", "1", "-framerate", FramesPerSecond.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (trimStart > 0)
        {
            args.AddRange(["-ss", Seconds(trimStart)]);
        }

        // Use display aspect ratio for fitting and even dimensions for the encoder.
        var width = ImageComposition.Width;
        var height = ImageComposition.Height;
        var fit = $"scale=w='max(2,min({width},trunc({height / 2}*dar)*2))':h='max(2,min({height},trunc({width / 2}/dar)*2))',setsar=1";
        List<string> videoFilters = ["overlay=(W-w)/2:(H-h)/2:shortest=1", "format=yuv420p"];
        AddFades(videoFilters, "fade", duration, fadeIn, fadeOut);
        var picture = $"[0:V:0]{fit},setpts=PTS-STARTPTS,fps={FramesPerSecond}[picture]";
        var canvas = $"color=black:s={width}x{height}:r={FramesPerSecond}[canvas]";
        var filter = $"{picture};{canvas};[canvas][picture]{string.Join(',', videoFilters)}[rendered]";
        args.AddRange(["-protocol_whitelist", "file", "-i", input, "-filter_complex", filter, "-map", "[rendered]"]);

        if (kind == MediaKind.Video)
        {
            args.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-ac", "2", "-b:a", "128k"]);
            List<string> audioFilters = [];
            AddFades(audioFilters, "afade", duration, fadeIn, fadeOut);
            if (audioFilters.Count != 0)
            {
                args.AddRange(["-af", string.Join(',', audioFilters)]);
            }
        }

        args.AddRange(["-t", Seconds(duration), "-c:v", "libx264", "-profile:v", "main", "-preset", "fast",
            "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-map_metadata", "-1",
            "-map_chapters", "-1", output]);
        return args;
    }

    private static void AddFades(List<string> filters, string filter, int duration, int fadeIn, int fadeOut)
    {
        if (fadeIn > 0)
        {
            filters.Add($"{filter}=t=in:d={Seconds(fadeIn)}");
        }

        if (fadeOut > 0)
        {
            filters.Add($"{filter}=t=out:st={Seconds(duration - fadeOut)}:d={Seconds(fadeOut)}");
        }
    }

    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    private static string FindFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ViPlex T10", "Tools", "ffmpeg.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim('"');
            if (Path.IsPathFullyQualified(directory))
            {
                var executable = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(executable))
                {
                    return executable;
                }
            }
        }

        throw new IOException("Trimming and fades require FFmpeg. Put ffmpeg.exe beside ViPlex.Windows.exe or add it to PATH.");
    }
}
