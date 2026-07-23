using System.Security.Cryptography;

namespace SyncMax.Services;

/// <summary>Генерирует случайный 6-значный код связки (криптостойкий источник).</summary>
public sealed class CodeGenerator
{
    public string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
