using Hispania.ArchitectureTest.Contracts;
using Hispania.ArchitectureTest.Modules;
using Hispania.Runtime;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.FFprobe;
using Hispania.Library.LocalFileSystem;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Hispania.Server;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;

var builder = WebApplication.CreateBuilder(args);
WebUiStaticAssets.Configure(builder.Environment);

builder.Services.AddArchitectureTestModules();
builder.Services.AddModuleRuntime();
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

builder.Services.AddSingleton(roots);
var sqlitePath = builder.Configuration["Persistence:Sqlite:Path"];
if (string.IsNullOrWhiteSpace(sqlitePath))
{
    sqlitePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "catalog.sqlite");
}
builder.Services.AddSqliteLibraryPersistence(sqlitePath);
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
builder.Services.AddSingleton<IMediaProbe>(provider => new FfprobeMediaProbe(roots, new FfprobeOptions(ffprobePath)));
builder.Services.AddSingleton<IProbedMediaMapper, ProbedMediaMapper>();
builder.Services.AddSingleton<IImportedPlayableMovieIdentityFactory, ImportedPlayableMovieIdentityFactory>();
builder.Services.AddSingleton(new LibraryScanScope(libraryOptions.Roots.Select(root => root.Key)));
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<IPlaybackPlanner, DirectPlayPlanner>();
builder.Services.AddSingleton<ISourceContentAccessor, LocalFileSystemSourceContentAccessor>();
builder.Services.AddSingleton<IDirectPlayExecutor, DirectPlayExecutor>();
builder.Services.AddSingleton<ICompatibleMediaTranscoder>(services => new FfmpegCompatibleMediaTranscoder(
    services.GetRequiredService<IMediaCatalogReader>(),
    services.GetRequiredService<ISourceContentAccessor>(),
    ffmpegPath,
    transcodeCachePath));
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
app.MapDirectPlayEndpoints();
app.MapEffectiveMetadataEndpoints();
app.MapMetadataEditingEndpoints();
app.MapPersonMetadataEndpoints();
app.MapTmdbMovieMetadataEndpoints();
app.MapMetadataEnrichmentControlEndpoints();
app.MapArtworkDeliveryEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
