using System.Net.Http.Headers;
using Hispania.Metadata.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.Metadata.Tmdb;

public static class TmdbServiceCollectionExtensions
{
    public static IServiceCollection AddTmdbArtworkContentResolver(this IServiceCollection services)
    {
        services.AddHttpClient<IArtworkContentResolver, TmdbArtworkContentResolver>();
        return services;
    }

    public static IServiceCollection AddTmdbMovieMetadataProvider(this IServiceCollection services, TmdbMovieMetadataProviderOptions options)
    {
        if (!options.Enabled) return services;
        options.ValidateForEnabledProvider();
        services.AddSingleton(options);
        services.AddHttpClient<TmdbMovieMetadataProvider>(client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ReadAccessToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddSingleton<IMovieMetadataProvider>(provider => provider.GetRequiredService<TmdbMovieMetadataProvider>());
        services.AddSingleton<IPersonMetadataProvider>(provider => provider.GetRequiredService<TmdbMovieMetadataProvider>());
        return services;
    }
}
