using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hispania.ModuleSystem.Contracts;

public enum PublisherSignatureStatus { NoSignature, Trusted, UnknownPublisher, RevokedPublisher, Invalid }

/// <summary>RSA-PSS signature over canonical manifest metadata, including the SHA-256 payload map.</summary>
public static class ModulePackageSignature
{
    public const string Algorithm = "rsa-pss-sha256";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ModuleManifest Sign(ModuleManifest manifest, string publisherId, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        var unsigned = manifest with { PublisherId = publisherId, SignatureAlgorithm = Algorithm, Signature = null };
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = Convert.ToBase64String(rsa.SignData(GetSigningBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return unsigned with { Signature = signature };
    }

    public static PublisherSignatureStatus Verify(ModuleManifest manifest, IReadOnlyDictionary<string, string> trustedPublicKeys, ISet<string> revokedPublisherIds)
    {
        if (string.IsNullOrWhiteSpace(manifest.Signature))
            return manifest.PublisherId is null && manifest.SignatureAlgorithm is null ? PublisherSignatureStatus.NoSignature : PublisherSignatureStatus.Invalid;
        if (string.IsNullOrWhiteSpace(manifest.PublisherId) || manifest.SignatureAlgorithm != Algorithm || revokedPublisherIds.Contains(manifest.PublisherId))
            return revokedPublisherIds.Contains(manifest.PublisherId ?? "") ? PublisherSignatureStatus.RevokedPublisher : PublisherSignatureStatus.Invalid;
        if (!trustedPublicKeys.TryGetValue(manifest.PublisherId, out var publicKeyPem)) return PublisherSignatureStatus.UnknownPublisher;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var signature = Convert.FromBase64String(manifest.Signature);
            var valid = rsa.VerifyData(GetSigningBytes(manifest with { Signature = null }), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return valid ? PublisherSignatureStatus.Trusted : PublisherSignatureStatus.Invalid;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return PublisherSignatureStatus.Invalid;
        }
    }

    public static byte[] GetSigningBytes(ModuleManifest manifest)
    {
        var element = JsonSerializer.SerializeToElement(manifest, JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default: element.WriteTo(writer); break;
        }
    }
}
