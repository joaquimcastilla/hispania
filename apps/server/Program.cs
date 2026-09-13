using Hispania.ArchitectureTest.Contracts;
using Hispania.ArchitectureTest.Modules;
using Hispania.Runtime;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.FFprobe;
using Hispania.Library.LocalFileSystem;
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
builder.Services.AddSingleton<IMediaCatalog, InMemoryMediaCatalog>();
builder.Services.AddSingleton<IMediaCatalogReader>(provider => provider.GetRequiredService<IMediaCatalog>());
builder.Services.AddSingleton<IMediaDiscovery, LocalFileSystemMediaDiscovery>();
builder.Services.AddSingleton<IMediaProbe>(provider => new FfprobeMediaProbe(roots, new FfprobeOptions(ffprobePath)));
builder.Services.AddSingleton<IProbedMediaMapper, ProbedMediaMapper>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<IPlaybackPlanner, DirectPlayPlanner>();
builder.Services.AddSingleton<ISourceContentAccessor, LocalFileSystemSourceContentAccessor>();
builder.Services.AddSingleton<IDirectPlayExecutor, DirectPlayExecutor>();
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
app.MapGet("/readyz", () => Results.Ok());
app.MapGet("/internal/module-test", (IArchitectureDemonstrationService service) => Results.Ok(service.GetResponse()));
app.MapGet("/internal/modules/health", (IModuleRuntime runtime) => Results.Ok(runtime.ListHealth()));
app.MapDirectPlayEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
