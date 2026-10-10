using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hispania.Hosting;
using Xunit;

namespace Hispania.Deployment.Tests;

public sealed class Phase21CertificateSecurityTests
{
    [Theory]
    [InlineData(-1, 90, "not-yet-valid")][InlineData(0, 0, "expired")]
    [InlineData(1, 20, "expires-soon")][InlineData(1, 60, "valid")]
    public void CertificateDatesUseExplicitThirtyDayPolicy(int elapsedDays, int remainingDays, string expected)
    {
        var starts = DateTimeOffset.UtcNow.AddDays(-10);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=phase21-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(starts, starts.AddDays(elapsedDays + remainingDays + 2));
        var now = elapsedDays < 0 ? new DateTimeOffset(certificate.NotBefore.ToUniversalTime()).AddSeconds(-1) : remainingDays == 0 ? new DateTimeOffset(certificate.NotAfter.ToUniversalTime()) : new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).AddDays(-remainingDays);
        Assert.Equal(expected, CertificateSecurityPolicy.Evaluate(certificate, now).Status);
    }
}
