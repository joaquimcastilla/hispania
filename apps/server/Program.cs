using Hispania.ArchitectureTest.Contracts;
using Hispania.ArchitectureTest.Modules;
using Hispania.Runtime;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.FFprobe;
using Hispania.Library.LocalFileSystem;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Persistence.PostgreSql;
using Hispania.DistributedWork.Persistence.Sqlite;
using Hispania.Playback.Application;
using Hispania.Playback.Conservative;
using Hispania.Playback.Contracts;
using Hispania.Server;
using Hispania.Inventory.Application;
using Hispania.Inventory.Contracts;
using Hispania.Inventory.Persistence.Sqlite;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Jobs.Application;
using Hispania.Platform.WorkerAgentJobs;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;
using Hispania.ModuleSystem.Contracts;
using Hispania.Media.Domain.Sources;
using Hispania.Transcoding.FFmpeg;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
{
    // Workers may present their client certificate; Kestrel still validates its chain using
    // the coordinator host's configured trust store before endpoint-level pin authorization.
    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
}));
var webBuild = builder.Configuration["Web:Build"] ?? "modern";
WebUiStaticAssets.Configure(builder.Environment, webBuild);

builder.Services.AddArchitectureTestModules();
builder.Services.AddModuleRuntime();
builder.Services.AddSingleton<LocalPlatformEventBus>();
var platformDataPath = builder.Configuration["Platform:DataPath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "platform");
var durableEventBus = new SqliteDurablePlatformEventBus(
    builder.Configuration["Platform:Events:Path"] ?? Path.Combine(platformDataPath, "events.sqlite"),
    new Hispania.Platform.Contracts.Events.DurableEventOptionsV1(
        builder.Configuration.GetValue("Platform:Events:MaximumPayloadBytes", 262144),
        builder.Configuration.GetValue("Platform:Events:MaximumAttempts", 5),
        TimeSpan.FromSeconds(builder.Configuration.GetValue("Platform:Events:RetryDelaySeconds", 2))));
builder.Services.AddSingleton(durableEventBus);
builder.Services.AddSingleton<Hispania.Platform.Contracts.Events.IDurablePlatformEventBusV1>(sp => sp.GetRequiredService<SqliteDurablePlatformEventBus>());
builder.Services.AddSingleton(sp => new Hispania.Platform.Contracts.Events.DurableEventHandlerRegistryV1()
    .Register<StockChangedV1>("inventory.stock-changed", 1, "platform.inventory.stock-observer", sp.GetRequiredService<InventoryStockEventObserver>()));
builder.Services.AddSingleton<InventoryStockEventObserver>();
builder.Services.AddSingleton<IPlatformEventPublisher, DurablePlatformEventPublisher>();
builder.Services.AddHostedService<DurablePlatformEventDispatcher>();
var jobOptions = new PlatformJobOptionsV1(
    builder.Configuration.GetValue("Platform:Jobs:MaximumPayloadBytes", 262144),
    builder.Configuration.GetValue("Platform:Jobs:MaximumResultMetadataBytes", 8192),
    builder.Configuration.GetValue("Platform:Jobs:MaximumAttempts", 5),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Platform:Jobs:RetryDelaySeconds", 2)),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Platform:Jobs:ExecutionLeaseSeconds", 120)),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Platform:Jobs:PollIntervalMilliseconds", 500)));
jobOptions.Validate();
builder.Services.AddSingleton(jobOptions);
builder.Services.AddSingleton<PlatformJobMetricsV1>();
builder.Services.AddSingleton<IPlatformJobHandlerRegistryV1>(_ => new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1()));
builder.Services.AddSingleton<IPlatformRemoteJobExecutorV1>(services => new DistributedPlatformJobExecutor(services.GetRequiredService<IDistributedWorkCoordinatorV1>()));
builder.Services.AddPlatformJobsSqlite(builder.Configuration["Platform:Jobs:Path"] ?? Path.Combine(platformDataPath, "jobs.sqlite"));
builder.Services.AddSingleton<LocalPlatformJobDispatcher>();
builder.Services.AddSingleton<IPlatformJobExecutionControlV1>(sp => sp.GetRequiredService<LocalPlatformJobDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPlatformJobDispatcher>());
builder.Services.AddSingleton<IPlatformJobServiceV1, PlatformJobService>();
builder.Services.AddPlatformSqlitePersistence(
    builder.Configuration["Platform:Assets:MetadataPath"] ?? Path.Combine(platformDataPath, "assets.sqlite"),
    builder.Configuration["Platform:Assets:ContentPath"] ?? Path.Combine(platformDataPath, "assets"),
    builder.Configuration["Platform:Audit:Path"] ?? Path.Combine(platformDataPath, "audit.sqlite"),
    builder.Configuration.GetValue("Platform:Assets:MaximumBytes", 100L * 1024 * 1024));
var identityLifetime = TimeSpan.FromMinutes(builder.Configuration.GetValue("Identity:SessionLifetimeMinutes", 720));
builder.Services.AddPlatformIdentitySqlite(
    builder.Configuration["Identity:Sqlite:Path"] ?? Path.Combine(platformDataPath, "identity.sqlite"),
    options: new Hispania.Platform.Contracts.Identity.IdentityOptions(identityLifetime));
builder.Services.AddPlatformAuthorizationSqlite(
    builder.Configuration["Identity:Sqlite:Path"] ?? Path.Combine(platformDataPath, "identity.sqlite"),
    builder.Configuration["Identity:Bootstrap:Token"]);
var inventoryDatabasePath = builder.Configuration["Inventory:Sqlite:Path"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "inventory.sqlite");
builder.Services.AddInventorySqlite(inventoryDatabasePath);
var distributedStatePath = builder.Configuration["DistributedWorkers:StatePath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "distributed-coordinator.sqlite");
builder.Services.AddSingleton<IDistributedWorkStateStoreV1>(_ => new SqliteDistributedWorkStateStore(distributedStatePath));
var distributedTransferPath = builder.Configuration["DistributedWorkers:TransferPath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "distributed-transfers");
var distributedTransferStorageBytes = builder.Configuration.GetValue("DistributedWorkers:TransferStorageBytes", 40L * 1024 * 1024 * 1024);
builder.Services.AddSingleton<DistributedMediaTransferRegistry>(services =>
{
    // Acquire the single-coordinator SQLite lock before the transfer registry performs startup
    // cleanup of abandoned partial files. A second coordinator must fail before touching them.
    _ = services.GetRequiredService<IDistributedWorkStateStoreV1>();
    return new DistributedMediaTransferRegistry(distributedTransferPath, distributedTransferStorageBytes);
});
builder.Services.AddSingleton<IDistributedMediaTransferBrokerV1>(services => services.GetRequiredService<DistributedMediaTransferRegistry>());
builder.Services.AddHostedService<DistributedMediaTransferCleanupService>();
builder.Services.AddSingleton<DistributedWorkCoordinator>(services => new DistributedWorkCoordinator(services.GetRequiredService<IDistributedWorkStateStoreV1>()));
builder.Services.AddSingleton<Hispania.ModuleSystem.Contracts.IDistributedWorkCoordinatorV1>(services => services.GetRequiredService<DistributedWorkCoordinator>());
builder.Services.AddHostedService(services => new DistributedWorkLeaseMonitor(
    services.GetRequiredService<Hispania.ModuleSystem.Contracts.IDistributedWorkCoordinatorV1>(),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("DistributedWorkers:LeaseMonitorSeconds", 1))));
builder.Services.AddSingleton<ModuleExtensionCatalog>(services => new ModuleExtensionCatalog(
    builder.Configuration["Modules:ExtensionsPath"] ?? Path.Combine(AppContext.BaseDirectory, "extensions"),
    new Version(1, 0, 0),
    services.GetRequiredService<IModuleRuntime>(),
    services.GetRequiredService<IModuleRegistry>(),
    new Dictionary<string, Func<IModule>>(StringComparer.Ordinal),
    builder.Configuration.GetSection("Modules:TrustedExtensionIds").Get<string[]>() ?? [],
    builder.Configuration["Modules:HostPath"],
    builder.Configuration.GetSection("Modules:TrustedPublisherKeys").Get<Dictionary<string, string>>(),
    builder.Configuration.GetSection("Modules:RevokedPublisherIds").Get<string[]>(),
    builder.Configuration.GetValue("Modules:RequirePublisherSignature", false),
    builder.Configuration.GetSection("Modules:OutOfProcessResources").Get<OutOfProcessResourceLimits>(),
    builder.Configuration.GetSection("Modules:OutOfProcessRestarts").Get<OutOfProcessRestartPolicy>()));
var libraryOptions = builder.Configuration.GetSection("Library").Get<LibraryOptions>() ?? new LibraryOptions();
if (libraryOptions.Roots.Count == 0)
{
    throw new InvalidOperationException("Library:Roots must contain at least one configured root.");
}

var roots = new LocalLibraryRootConfiguration(libraryOptions.Roots.Select(root => new LocalLibraryRoot(root.Key, root.Path)));
var ffprobePath = builder.Configuration["Ffprobe:ExecutablePath"];
if (string.IsNullOrWhiteSpace(ffprobePath))
{
    throw new InvalidOperationException("Ffprobe:ExecutablePath is required.");
}
var ffmpegPath = builder.Configuration["Ffmpeg:ExecutablePath"];
if (string.IsNullOrWhiteSpace(ffmpegPath))
{
    var ffprobeDirectory = Path.GetDirectoryName(ffprobePath);
    var siblingFfmpeg = string.IsNullOrWhiteSpace(ffprobeDirectory) ? null : Path.Combine(ffprobeDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
    ffmpegPath = siblingFfmpeg is not null && File.Exists(siblingFfmpeg) ? siblingFfmpeg : "ffmpeg";
}
var transcodeCachePath = builder.Configuration["Playback:TranscodeCachePath"];
if (string.IsNullOrWhiteSpace(transcodeCachePath)) transcodeCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "transcoded-media");
var workerPolicy = builder.Configuration.GetSection("Playback:Worker").Get<FfmpegWorkerPolicy>() ?? new FfmpegWorkerPolicy();
workerPolicy.Validate();
builder.Services.AddSingleton(workerPolicy);
var distributedTranscodingModeText = builder.Configuration["DistributedWorkers:Transcoding:Mode"] ?? "Local";
if (!Enum.TryParse<DistributedTranscodingMode>(distributedTranscodingModeText, ignoreCase: true, out var distributedTranscodingMode))
    throw new InvalidOperationException("DistributedWorkers:Transcoding:Mode must be Local, RemotePreferred, or RemoteOnly.");

builder.Services.AddSingleton(roots);
var persistenceProvider = builder.Configuration["Persistence:Provider"] ?? "sqlite";
if (persistenceProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
{
    var sqlitePath = builder.Configuration["Persistence:Sqlite:Path"];
    if (string.IsNullOrWhiteSpace(sqlitePath))
    {
        sqlitePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "catalog.sqlite");
    }
    builder.Services.AddSqliteLibraryPersistence(sqlitePath);
}
else if (persistenceProvider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration["Persistence:PostgreSql:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Persistence:PostgreSql:ConnectionString is required when Persistence:Provider=postgresql.");
    builder.Services.AddPostgreSqlLibraryPersistence(connectionString);
}
else throw new InvalidOperationException($"Unsupported Persistence:Provider '{persistenceProvider}'.");
builder.Services.AddTmdbArtworkContentResolver();
builder.Services.AddSingleton<IEffectiveMetadataResolver, EffectiveMetadataResolver>();
builder.Services.AddSingleton<IEffectiveMetadataProjectionReader, EffectiveMetadataProjectionReader>();
builder.Services.AddSingleton<IMetadataEditingService, MetadataEditingService>();
builder.Services.AddSingleton<MovieMetadataIndexDocument>(serviceProvider => new MovieMetadataIndexDocument(
    builder.Configuration["Metadata:InventoryPath"],
    serviceProvider.GetRequiredService<IMediaCatalogReader>(),
    serviceProvider.GetRequiredService<IMetadataEnrichmentStore>()));
builder.Services.AddHostedService<MovieMetadataIndexStartupService>();
var tmdbOptions = builder.Configuration.GetSection("Metadata:Tmdb").Get<TmdbMovieMetadataProviderOptions>() ?? new TmdbMovieMetadataProviderOptions();
if (tmdbOptions.Enabled)
{
    builder.Services.AddTmdbMovieMetadataProvider(tmdbOptions);
    builder.Services.AddSingleton<MovieMetadataApplicationService>();
    builder.Services.AddSingleton<IMovieMetadataAutoMatcher, MovieMetadataAutoMatcher>();
    builder.Services.AddSingleton<MovieMetadataAutoEnrichmentService>();
}
builder.Services.AddSingleton<IMediaDiscovery, LocalFileSystemMediaDiscovery>();
builder.Services.AddSingleton<IMediaProbe>(provider => new FfprobeMediaProbe(provider.GetRequiredService<IMediaSourceContentAccessV1>(), new FfprobeOptions(ffprobePath)));
builder.Services.AddSingleton<IProbedMediaMapper, ProbedMediaMapper>();
builder.Services.AddSingleton<IImportedPlayableMovieIdentityFactory, ImportedPlayableMovieIdentityFactory>();
builder.Services.AddSingleton(new LibraryScanScope(libraryOptions.Roots.Select(root => root.Key)));
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<IModule>(services => services.GetRequiredService<DirectPlayPlanner>());
builder.Services.AddSingleton<DirectPlayPlanner>();
builder.Services.AddSingleton<ConservativePlaybackPlanner>();
builder.Services.AddSingleton<IModule>(services => services.GetRequiredService<ConservativePlaybackPlanner>());
var playbackPlannerId = builder.Configuration["Modules:PlaybackPlanner"] ?? "hispania.playback.direct-planner";
builder.Services.AddSingleton<HotSwappablePlaybackPlanner>(services => new HotSwappablePlaybackPlanner(
    playbackPlannerId,
    services.GetRequiredService<ICapabilityResolver>().Resolve<IPlaybackPlanner>("hispania.playback.planner", new Version(1, 0), playbackPlannerId)));
var experimentStatePath = builder.Configuration["PlaybackExperiments:StatePath"];
var experimentAssignmentSalt = builder.Configuration["PlaybackExperiments:AssignmentSalt"];
IPlaybackExperimentStore experimentStore = string.IsNullOrWhiteSpace(experimentStatePath)
    ? new InMemoryPlaybackExperimentStore()
    : new JsonPlaybackExperimentStore(experimentStatePath);
builder.Services.AddSingleton<IPlaybackExperimentStore>(experimentStore);
builder.Services.AddSingleton<IVariantAssignmentStore>(experimentStore);
builder.Services.AddSingleton<VariantExperimentRouter<IPlaybackPlanner>>(services =>
    new VariantExperimentRouter<IPlaybackPlanner>(services.GetRequiredService<IVariantAssignmentStore>(), experimentAssignmentSalt));
builder.Services.AddSingleton<PlaybackExperimentService>(services => new PlaybackExperimentService(
    services.GetRequiredService<IModuleRegistry>(), services.GetRequiredService<ModuleRuntime>(),
    services.GetRequiredService<VariantExperimentRouter<IPlaybackPlanner>>(), experimentStore, experimentAssignmentSalt,
    services.GetRequiredService<ModuleExtensionCatalog>()));
builder.Services.AddHostedService(services => services.GetRequiredService<PlaybackExperimentService>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPlaybackPlanner, ExperimentRoutedPlaybackPlanner>();
builder.Services.AddSingleton<PlaybackPlannerHotSwapService>();
builder.Services.AddSingleton<LocalFileSystemSourceContentAccessor>();
builder.Services.AddSingleton<ISourceContentAccessor>(provider => provider.GetRequiredService<LocalFileSystemSourceContentAccessor>());
builder.Services.AddSingleton<IMediaSourceContentAccessV1>(provider => provider.GetRequiredService<LocalFileSystemSourceContentAccessor>());
builder.Services.AddSingleton<IDirectPlayExecutor, DirectPlayExecutor>();
builder.Services.AddSingleton<FfmpegTranscodingCapabilityHealth>();
builder.Services.AddSingleton<ITranscodingCapabilityHealthV1>(services => services.GetRequiredService<FfmpegTranscodingCapabilityHealth>());
builder.Services.AddSingleton<IModule>(services => services.GetRequiredService<FfmpegCompatibleMediaTranscoder>());
builder.Services.AddSingleton<FfmpegCompatibleMediaTranscoder>(services => new FfmpegCompatibleMediaTranscoder(
    services.GetRequiredService<IMediaCatalogReader>(),
    services.GetRequiredService<ISourceContentAccessor>(),
    ffmpegPath,
    transcodeCachePath,
    builder.Configuration["Ffmpeg:WorkerAssemblyPath"],
    workerPolicy,
    services.GetRequiredService<FfmpegTranscodingCapabilityHealth>(),
    services.GetRequiredService<IDistributedWorkCoordinatorV1>(),
    services.GetRequiredService<IDistributedMediaTransferBrokerV1>(),
    distributedTranscodingMode));
builder.Services.AddSingleton<ITranscodedContentStore>(services => services.GetRequiredService<FfmpegCompatibleMediaTranscoder>());
builder.Services.AddSingleton<ICompatibleMediaTranscoderV1>(services => services.GetRequiredService<ICapabilityResolver>()
    .Resolve<ICompatibleMediaTranscoderV1>("hispania.playback.compatible-transcoder", new Version(1, 0), "hispania.transcoding.ffmpeg"));
builder.Services.AddSingleton<LibraryReadiness>();
builder.Services.AddHostedService<PersistenceStartupService>();
builder.Services.AddHostedService<InitialLibraryScanService>();
if (tmdbOptions.Enabled)
{
    builder.Services.AddSingleton<AutomaticMovieMetadataEnrichmentHostedService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<AutomaticMovieMetadataEnrichmentHostedService>());
}

var selectedModuleId = builder.Configuration["Modules:ArchitectureDemonstration"]
    ?? throw new InvalidOperationException("Modules:ArchitectureDemonstration must select a module ID.");

builder.Services.AddSingleton<IArchitectureDemonstrationService>(serviceProvider =>
    serviceProvider.GetRequiredService<IModuleResolver>()
        .Resolve<IArchitectureDemonstrationService>(selectedModuleId));

var app = builder.Build();
await SeedInventoryDemoAsync(app.Services);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok());
app.MapGet("/readyz", (LibraryReadiness readiness) => readiness.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapGet("/internal/metadata-enrichment", (IServiceProvider services) =>
{
    var enrichment = services.GetService<AutomaticMovieMetadataEnrichmentHostedService>();
    return enrichment is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(enrichment.LastRun);
});
app.MapGet("/internal/module-test", (IArchitectureDemonstrationService service) => Results.Ok(service.GetResponse()));
app.MapGet("/internal/modules/health", (IModuleRuntime runtime) => Results.Ok(runtime.ListHealth()));
app.MapModuleAdminEndpoints();
app.MapDistributedWorkerEndpoints();
app.MapGet("/internal/capabilities/transcoding/health", (ITranscodingCapabilityHealthV1 health) => Results.Ok(health.GetSnapshot()));
app.MapDirectPlayEndpoints();
app.MapEffectiveMetadataEndpoints();
app.MapMetadataEditingEndpoints();
app.MapPersonMetadataEndpoints();
app.MapTmdbMovieMetadataEndpoints();
app.MapMetadataEnrichmentControlEndpoints();
app.MapArtworkDeliveryEndpoints();
app.MapInventoryEndpoints();
app.MapPlatformAssetEndpoints();
app.MapPlatformAuditEndpoints();
app.MapPlatformEventAdminEndpoints();
app.MapPlatformJobEndpoints();
app.MapIdentityEndpoints();
app.MapAuthorizationEndpoints();
app.MapInventoryMediaAssetEndpoints();
var testShutdownToken = builder.Configuration["Testing:ShutdownToken"];
if (!string.IsNullOrWhiteSpace(testShutdownToken))
{
    app.MapPost("/_test/shutdown", (HttpContext http, IHostApplicationLifetime lifetime) =>
    {
        if (!string.Equals(http.Request.Headers.Authorization.ToString(), $"Bearer {testShutdownToken}", StringComparison.Ordinal)) return Results.Unauthorized();
        lifetime.StopApplication();
        return Results.Accepted();
    });
}
app.MapFallbackToFile("index.html");

app.Run();

static async Task SeedInventoryDemoAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var query = scope.ServiceProvider.GetRequiredService<IInventoryQueryV1>();
    if ((await query.GetItemsAsync()).Count != 0) return;
    var command = scope.ServiceProvider.GetRequiredService<IInventoryCommandV1>();
    var assets = scope.ServiceProvider.GetRequiredService<IPlatformAssetStore>();
    var item = await command.CreateItemAsync("BP48", "Battery Pack 48V");
    var warehouse = await command.CreateWarehouseAsync("MAD", "Madrid Warehouse");
    var location = await command.CreateLocationAsync(warehouse.Id, "A-01", "Shelf A-01");
    await command.ReceiveAsync(item.Id, location.Id, 12);
    var scopeRef = new AssetScope("erp.inventory", item.Id.Value.ToString("D"));
    var policy = new AssetAccessPolicy("inventory-demo-public-read", PublicRead: true);
    var samples = new (AssetType Type, string Mime, string Label, byte[] Bytes)[]
    {
        (AssetType.Image, "image/svg+xml", "Product image", System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='640' height='360'><rect width='100%' height='100%' fill='#253a56'/><text x='50%' y='52%' fill='white' text-anchor='middle' font-size='28'>Battery Pack 48V</text></svg>")),
        (AssetType.Document, "application/pdf", "Datasheet PDF", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "demo-assets", "bp48-datasheet.pdf"))),
        (AssetType.Video, "video/mp4", "Product video", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "demo-assets", "bp48-demo.mp4")))
    };
    foreach (var sample in samples)
    {
        await using var content = new MemoryStream(sample.Bytes);
        var stored = await assets.StoreAsync(sample.Type, sample.Mime, content, scopeRef, policy, new Dictionary<string, string> { ["label"] = sample.Label });
        await command.AssociateAssetAsync(item.Id, new AssetReference(stored.Id));
    }
}
