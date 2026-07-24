using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;

namespace SyncMax.Services;

/// <summary>
/// Опциональная конвертация медиа через ffmpeg (внешний процесс). Разрешена для аудио и фото,
/// но НЕ для видео. Если ffmpeg не найден или конвертация не удалась — методы возвращают null,
/// и вызывающий код пересылает исходный файл как есть (или откатывается на отправку файлом).
/// Никаких данных в ОЗУ: работаем с временными файлами на диске.
/// </summary>
public sealed class MediaConverter
{
    private readonly MediaOptions _options;
    private readonly ILogger<MediaConverter> _logger;
    private readonly Lazy<bool> _available;

    public MediaConverter(IOptions<MediaOptions> options, ILogger<MediaConverter> logger)
    {
        _options = options.Value;
        _logger = logger;
        _available = new Lazy<bool>(ProbeFfmpeg);
    }

    /// <summary>Доступна ли конвертация (ffmpeg найден и запускается).</summary>
    public bool IsAvailable => _available.Value;

    /// <summary>
    /// Конвертирует аудио (напр. голос ogg/opus) в mp3 — формат, который принимает большинство
    /// платформ. Возвращает путь к новому временному файлу или null, если конвертация недоступна
    /// или не удалась.
    /// </summary>
    public Task<string?> TryConvertAudioToMp3Async(string inputPath, CancellationToken ct) =>
        RunAsync(inputPath, ".mp3", ["-i", inputPath, "-vn", "-acodec", "libmp3lame", "-q:a", "4"], ct);

    /// <summary>Конвертирует изображение в JPEG. null, если недоступно/не удалось.</summary>
    public Task<string?> TryConvertImageToJpegAsync(string inputPath, CancellationToken ct) =>
        RunAsync(inputPath, ".jpg", ["-i", inputPath, "-frames:v", "1"], ct);

    private async Task<string?> RunAsync(string inputPath, string outExtension, string[] inputArgs, CancellationToken ct)
    {
        if (!IsAvailable)
            return null;

        var output = TempFiles.NewPath(outExtension);
        // -y — перезаписать, -loglevel error — не шуметь в stderr.
        var args = new List<string> { "-y", "-loglevel", "error" };
        args.AddRange(inputArgs);
        args.Add(output);

        try
        {
            var psi = new ProcessStartInfo(_options.FfmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null)
            {
                TempFiles.TryDelete(output);
                return null;
            }

            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
                return output;

            var err = await process.StandardError.ReadToEndAsync(ct);
            _logger.LogWarning("ffmpeg вернул код {Code}: {Error}", process.ExitCode, err);
            TempFiles.TryDelete(output);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Конвертация {Input} не удалась.", inputPath);
            TempFiles.TryDelete(output);
            return null;
        }
    }

    private bool ProbeFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo(_options.FfmpegPath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit(5000);
            var ok = process.HasExited && process.ExitCode == 0;
            if (!ok)
                _logger.LogInformation("ffmpeg недоступен — конвертация медиа отключена.");
            return ok;
        }
        catch
        {
            _logger.LogInformation("ffmpeg не найден ({Path}) — конвертация медиа отключена.", _options.FfmpegPath);
            return false;
        }
    }
}
