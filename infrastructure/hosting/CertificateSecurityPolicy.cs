using System.Security.Cryptography.X509Certificates;

namespace Hispania.Hosting;

public static class CertificateSecurityPolicy
{
    public const int WarningDays = 30;
    public sealed record CertificateStatus(string Status, DateTimeOffset NotBefore, DateTimeOffset NotAfter);
    public static CertificateStatus Evaluate(X509Certificate2 certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var starts = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var ends = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        var status = now < starts ? "not-yet-valid" : now >= ends ? "expired" : ends - now <= TimeSpan.FromDays(WarningDays) ? "expires-soon" : "valid";
        return new(status, starts, ends);
    }
}
