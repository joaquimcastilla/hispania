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

builder.Services.AddSingleton(roots);
var sqlitePath = builder.Configuration["Persistence:Sqlite:Path"];
if (string.IsNullOrWhiteSpace(sqlitePath))
{
    sqlitePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "catalog.sqlite");
}
builder.Services.AddSqliteLibraryPersistence(sqlitePath);
builder.Services.AddSingleton<IMediaDiscovery, LocalFileSystemMediaDiscovery>();
builder.Services.AddSingleton<IMediaProbe>(provider => new FfprobeMediaProbe(roots, new FfprobeOptions(ffprobePath)));
builder.Services.AddSingleton<IProbedMediaMapper, ProbedMediaMapper>();
builder.Services.AddSingleton<IImportedPlayableMovieIdentityFactory, ImportedPlayableMovieIdentityFactory>();
builder.Services.AddSingleton(new LibraryScanScope(libraryOptions.Roots.Select(root => root.Key)));
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<IPlaybackPlanner, DirectPlayPlanner>();
builder.Services.AddSingleton<ISourceContentAccessor, LocalFileSystemSourceContentAccessor>();
builder.Services.AddSingleton<IDirectPlayExecutor, DirectPlayExecutor>();
builder.Services.AddSingleton<LibraryReadiness>();
builder.Services.AddHostedService<PersistenceStartupService>();
builder.Services.AddHostedService<InitialLibraryScanService>();

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
app.MapGet("/internal/module-test", (IArchitectureDemonstrationService service) => Results.Ok(service.GetResponse()));
app.MapGet("/internal/modules/health", (IModuleRuntime runtime) => Results.Ok(runtime.ListHealth()));
app.MapDirectPlayEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
