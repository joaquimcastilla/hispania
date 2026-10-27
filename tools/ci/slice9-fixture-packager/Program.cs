using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: Slice9.FixturePackager <fixture-assembly> <contracts-assembly> <package-output> <public-key-output>");
    return 2;
}

var fixturePath = Path.GetFullPath(args[0]);
var contractsPath = Path.GetFullPath(args[1]);
var packagePath = Path.GetFullPath(args[2]);
var publicKeyPath = Path.GetFullPath(args[3]);
if (!File.Exists(fixturePath) || !File.Exists(contractsPath) || File.Exists(packagePath) || File.Exists(publicKeyPath))
    throw new InvalidOperationException("Fixture inputs are missing or output paths already exist.");

var fixtureName = Path.GetFileName(fixturePath);
var fixtureHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant();
var manifest = new ModuleManifest
{
    PackageVersion = "1.0.0",
    PackageDependencies = [],
    RequiredModuleHostProtocol = 1,
    Id = "hispania.slice7.fixture",
    Name = "HISPANIA Slice 7 Fixture",
    Version = "1.0.0",
    Implements = [],
    Provides = [],
    Requires = [],
    OptionalDependencies = [],
    Dependencies = [],
    DependencyMinimumVersions = new(StringComparer.Ordinal),
    Permissions = ["core.modules.manage"],
    MinimumHostVersion = "1.0.0",
    MaximumHostVersion = "1.0.0",
    Runtime = "dotnet-outofprocess",
    RestartRequiredForUpdate = true,
    EntryAssembly = fixtureName,
    EntryType = "Hispania.Slice7.Fixture.Slice7FixtureModule",
    SharedAssemblies = ["Hispania.ModuleSystem.Contracts"],
    ContractTypes = new(StringComparer.Ordinal),
    Files = new Dictionary<string, string>(StringComparer.Ordinal) { [fixtureName] = fixtureHash },
    PackageFormatVersion = 2
};

using var rsa = RSA.Create(2048);
var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
var privatePem = rsa.ExportPkcs8PrivateKeyPem();
var signed = ModulePackageSignature.Sign(manifest, "hispania.slice7.test", privatePem);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(signed, jsonOptions);
var trustedPublisher = new Dictionary<string, string>(StringComparer.Ordinal) { ["hispania.slice7.test"] = publicPem };
if (ModulePackageSignature.Verify(signed, trustedPublisher, new HashSet<string>(StringComparer.Ordinal)) != PublisherSignatureStatus.Trusted)
    throw new CryptographicException("Generated fixture signature did not verify.");

Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
Directory.CreateDirectory(Path.GetDirectoryName(publicKeyPath)!);
using (var stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
{
    var moduleEntry = zip.CreateEntry("module.json", CompressionLevel.Optimal);
    using (var output = moduleEntry.Open()) output.Write(manifestBytes);
    var payload = zip.CreateEntry(fixtureName, CompressionLevel.Optimal);
    using var payloadOutput = payload.Open();
    using var input = File.OpenRead(fixturePath);
    input.CopyTo(payloadOutput);
}
using (var verificationZip = ZipFile.OpenRead(packagePath))
{
    if (verificationZip.Entries.Count != 2 || verificationZip.GetEntry("module.json") is not { } packagedManifest ||
        verificationZip.GetEntry(fixtureName) is not { } packagedPayload)
        throw new InvalidDataException("Generated package inventory is unexpected.");
    using var manifestStream = packagedManifest.Open();
    var roundTrippedManifest = JsonSerializer.Deserialize<ModuleManifest>(manifestStream, jsonOptions)
        ?? throw new InvalidDataException("Generated package manifest could not be read back.");
    if (ModulePackageSignature.Verify(roundTrippedManifest, trustedPublisher, new HashSet<string>(StringComparer.Ordinal)) != PublisherSignatureStatus.Trusted)
        throw new CryptographicException("Packaged fixture signature did not verify after serialization.");
    using var payloadStream = packagedPayload.Open();
    var packagedHash = Convert.ToHexString(SHA256.HashData(payloadStream)).ToLowerInvariant();
    if (!string.Equals(packagedHash, fixtureHash, StringComparison.Ordinal))
        throw new InvalidDataException("Packaged fixture payload hash differs from the signed manifest.");
}
File.WriteAllText(publicKeyPath, publicPem, new UTF8Encoding(false));
Console.WriteLine($"Signed fixture created; publisher=hispania.slice7.test; payloadSha256={fixtureHash}");
return 0;
