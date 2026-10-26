using System.Diagnostics;

namespace Memtly.Core.Helpers;

/// <summary>libheif through the distro-maintained ImageMagick decoder, isolated from the web process.</summary>
public static class HeifThumbnail
{
    private static readonly SemaphoreSlim Decoder = new(1);

    public static async Task<bool> Generate(string input, string output, int size, int quality = 80)
    {
        if (!OperatingSystem.IsLinux() || !UploadMediaValidator.IsValid(input)) return false;
        if (!await Decoder.WaitAsync(TimeSpan.FromSeconds(10))) return false;
        try
        {
            var dimensions = await Run("identify", "-ping", "-format", "%w %h", $"{Path.GetFullPath(input)}[0]");
            var values = dimensions?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values?.Length != 2 || !int.TryParse(values[0], out var width) || !int.TryParse(values[1], out var height) ||
                width <= 0 || height <= 0 || (long)width * height > UploadMediaValidator.MaxPixels) return false;
            var result = await Run("convert", $"{Path.GetFullPath(input)}[0]", "-auto-orient", "-thumbnail",
                $"{Math.Clamp(size, 100, 4096)}x{Math.Clamp(size, 100, 4096)}>", "-strip", "-quality", Math.Clamp(quality, 1, 100).ToString(), $"webp:{Path.GetFullPath(output)}");
            return result != null && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch { return false; }
        finally { Decoder.Release(); }
    }

    private static async Task<string?> Run(string command, params string[] args)
    {
        var start = new ProcessStartInfo("prlimit") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var limit in new[] { "--as=1073741824", "--cpu=25", "--", command }) start.ArgumentList.Add(limit);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["MAGICK_THREAD_LIMIT"] = "1";
        start.Environment["OMP_NUM_THREADS"] = "1";
        start.Environment["MAGICK_CONFIGURE_PATH"] = Path.Combine(AppContext.BaseDirectory, "Assets", "Heif");
        using var process = Process.Start(start);
        if (process == null) return null;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return null;
        }
        await stderr; // Drain errors, never include file metadata or native diagnostics in the response.
        return process.ExitCode == 0 ? await stdout : null;
    }
}
