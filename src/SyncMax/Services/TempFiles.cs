namespace SyncMax.Services;

/// <summary>
/// Временные файлы для скачивания/загрузки медиа. Содержимое вложений держим на диске,
/// а не в ОЗУ. Файлы складываются в отдельную подпапку системного temp и удаляются
/// после пересылки (см. <see cref="MessageRelayService"/>).
/// </summary>
public static class TempFiles
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "syncmax-relay");

    /// <summary>Новый уникальный путь во временной папке с заданным расширением (например ".jpg").</summary>
    public static string NewPath(string? extension = null)
    {
        Directory.CreateDirectory(Dir);
        var name = Guid.NewGuid().ToString("N") + NormalizeExtension(extension);
        return Path.Combine(Dir, name);
    }

    /// <summary>Удаляет файл, молча проглатывая ошибки (файл мог быть уже удалён/недоступен).</summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Уборка мусора — не критично, если не удалось (файл занят/уже удалён).
        }
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;
        return extension.StartsWith('.') ? extension : "." + extension;
    }
}
