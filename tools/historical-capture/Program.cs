using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Contracts.Authorization;

if (args.Length != 1) throw new ArgumentException("Supply a NEW staging output directory, never an existing baseline.");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Capture refuses to overwrite an existing corpus.");
Directory.CreateDirectory(output);
var options = new JsonSerializerOptions { WriteIndented = true };
void Bytes(string relative, byte[] value) { var path = Path.Combine(output, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, value); }
void Json<T>(string path, T value) => Bytes(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, options) + "\n"));
var id = Guid.Parse("00000000-0000-0000-0000-000000000007");
var epoch = DateTimeOffset.UnixEpoch;
Json("serialization/0.7/asset-reference.json", new AssetReference(new AssetId(id)));
Json("serialization/0.7/identity-user.json", new User(new UserId(id), "baseline07", "TEST ONLY", UserStatus.Enabled, epoch, epoch));
Json("serialization/0.7/identity-session.json", new Session(new SessionId(id), new UserId(id), epoch, epoch.AddHours(12), null));
Json("serialization/0.7/authorization-role.json", new Role(new RoleId(id), "Baseline Readers", "TEST ONLY", epoch, epoch));
Json("serialization/0.7/permission-identifiers.json", PlatformPermissionIds.BuiltIn.Order(StringComparer.Ordinal).ToArray());
var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("TEST-ONLY-07-module-token"));
async Task Frame<T>(string name, T value) { using var stream = new MemoryStream(); await ModuleWorkerProtocolV1.WriteFrameAsync(stream, value); Bytes("protocols/0.7/module-" + name + ".bin", stream.ToArray()); }
await Frame("hello", new ModuleWorkerHelloV1("hispania.extension-demo.greeting", "1.0.0", ModuleWorkerProtocolV1.ProtocolId, 1, 1, token));
await Frame("negotiation", new ModuleWorkerNegotiationV1(true, 1, null));
await Frame("success", new ModuleWorkerResponseV1("test-request-07", true, JsonSerializer.SerializeToElement("hello-07"), null));
await Frame("error", new ModuleWorkerResponseV1("test-request-07", false, null, new ModuleWorkerErrorV1("TEST_FAILURE", "TEST ONLY", false)));
await Frame("cancel", new ModuleWorkerCancelV1("test-request-07"));
var capability = new DistributedCapabilityV1("test.baseline.echo", "1.0");
var inventory = new DistributedWorkerInventoryV1("TEST-OS", "x64", 2, null, 1, [], [capability]);
Json("protocols/0.7/worker-connect.json", new DistributedWorkerConnectRequestV1(1, 1, inventory));
Json("protocols/0.7/worker-negotiation.json", new DistributedWorkerConnectResponseV1(1, [capability]));
var request = new DistributedJobRequestV1("test-job-07", capability.ContractId, capability.ContractVersion, "echo", JsonSerializer.SerializeToElement("test-payload"), RetrySafe: true);
Json("protocols/0.7/worker-assignment.json", new DistributedLeasedJobV1(new DistributedJobLeaseV1(request.JobId, "test-worker-07", 2, epoch.AddHours(1)), request));
Json("protocols/0.7/worker-result.json", new DistributedWorkerCompletionV1(2, JsonSerializer.SerializeToElement("valid")));
Json("protocols/0.7/worker-stale-result.json", new DistributedWorkerCompletionV1(1, JsonSerializer.SerializeToElement("stale")));
Bytes("contracts/0.7/Hispania.Platform.Contracts.dll", File.ReadAllBytes(typeof(User).Assembly.Location));
Bytes("contracts/0.7/Hispania.ModuleSystem.Contracts.dll", File.ReadAllBytes(typeof(ModuleManifest).Assembly.Location));
var module = File.ReadAllBytes(typeof(Hispania.ExtensionDemo.IndependentGreetingModule).Assembly.Location);
var contracts = File.ReadAllBytes(typeof(Hispania.ExtensionDemo.Contracts.IExtensionDemoV1).Assembly.Location);
Bytes("modules/0.7/greeting/Hispania.ExtensionDemo.dll", module);
Bytes("contracts/0.7/Hispania.ExtensionDemo.Contracts.dll", contracts);
string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
var manifest = new ModuleManifest {
    Id = "hispania.extension-demo.greeting", Name = "Independent greeting extension", Version = "1.0.0",
    Implements = ["hispania.extension-demo.greeting@1.0"], Provides = ["hispania.extension-demo.greeting@1.0"],
    MinimumHostVersion = "1.0.0", Runtime = "dotnet-inprocess-trusted", RestartRequiredForUpdate = false,
    EntryAssembly = "Hispania.ExtensionDemo.dll", EntryType = "Hispania.ExtensionDemo.IndependentGreetingModule",
    SharedAssemblies = ["Hispania.ExtensionDemo.Contracts"],
    Files = new(StringComparer.Ordinal) { ["Hispania.ExtensionDemo.dll"] = Hash(module), ["Hispania.ExtensionDemo.Contracts.dll"] = Hash(contracts) }
};
using var rsa = RSA.Create(2048); // Ephemeral TEST key: only public key and signature leave this process.
manifest = ModulePackageSignature.Sign(manifest, "TEST-ONLY-baseline-07", rsa.ExportPkcs8PrivateKeyPem());
Json("manifests/0.7/greeting.json", manifest);
Bytes("manifests/0.7/TEST-ONLY-publisher-public.pem", Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n"));
using var package = new MemoryStream();
using (var zip = new ZipArchive(package, ZipArchiveMode.Create, true)) {
    void Entry(string name, byte[] value) { var entry = zip.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero); using var stream = entry.Open(); stream.Write(value); }
    Entry("module.json", JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    Entry("Hispania.ExtensionDemo.dll", module); Entry("Hispania.ExtensionDemo.Contracts.dll", contracts);
}
Bytes("packages/0.7/greeting.hispania", package.ToArray());
