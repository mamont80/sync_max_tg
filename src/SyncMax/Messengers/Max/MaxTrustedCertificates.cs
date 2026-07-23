using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SyncMax.Messengers.Max;

/// <summary>
/// Bot API MAX (platform-api2.max.ru) отдаёт TLS-цепочку, подписанную Russian Trusted
/// Sub CA / Root CA (Национальный удостоверяющий центр Минцифры РФ) — эти корни не входят
/// в системные доверенные хранилища большинства ОС, из-за чего запросы падают с
/// AuthenticationException/UntrustedRoot.
///
/// Доверие настраивается точечно, только для HttpClient MaxApiClient — системное
/// хранилище сертификатов машины не трогаем. Если обычная проверка ОС проходит
/// (сертификат уже доверен как-то иначе), эта логика не вмешивается.
/// </summary>
internal static class MaxTrustedCertificates
{
    private const string RootFileName = "russian_trusted_root_ca.cer";
    private const string SubFileName = "russian_trusted_sub_ca.cer";

    public static void ConfigureValidation(HttpClientHandler handler)
    {
        var root = LoadOrNull(RootFileName);
        if (root is null)
            return; // файла нет рядом с бинарником — оставляем стандартную проверку ОС как есть

        var sub = LoadOrNull(SubFileName);

        handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
        {
            if (errors == SslPolicyErrors.None)
                return true;

            // Несовпадение имени хоста или отсутствие сертификата не прощаем никогда —
            // доверяем дополнительному корню, но не ослабляем остальную проверку.
            if (cert is null || chain is null ||
                errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
                errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                return false;

            // Проверяем цепочку заново, доверяя только Russian Trusted Root CA.
            // RevocationMode.NoCheck — российские OCSP/CRL точки не всегда доступны
            // из-за пределов РФ, а сама проверка доверия для нас важнее отзыва.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.CustomTrustStore.Add(root);
            if (sub is not null)
                chain.ChainPolicy.ExtraStore.Add(sub);

            return chain.Build(cert);
        };
    }

    private static X509Certificate2? LoadOrNull(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Certificates", fileName);
        return File.Exists(path) ? X509CertificateLoader.LoadCertificateFromFile(path) : null;
    }
}
