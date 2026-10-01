using System.Text.Json;
using Hispania.Library.Contracts;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;

namespace Hispania.Server;

public static class MetadataEditingEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEditingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/media/{mediaEntityId:guid}/metadata");
        var adminGroup = group.MapGroup("").AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(MediaPermissionIds.Admin, allowLegacyAdminBearer: false));
        adminGroup.MapGet("/edit", GetAsync);
        adminGroup.MapPut("/settings", UpdateSettingsAsync);
        adminGroup.MapPut("/locks/{fieldCode}", UpdateLockAsync);
        adminGroup.MapPut("/scalars/{fieldCode}/manual", SetScalarAsync);
        adminGroup.MapDelete("/scalars/{fieldCode}/manual", ClearScalarAsync);
        MapCollection(adminGroup, "genres", MetadataCollection.Genres);
        MapCollection(adminGroup, "tags", MetadataCollection.Tags);
        MapCollection(adminGroup, "studios", MetadataCollection.Studios);
        return endpoints;
    }

    private static void MapCollection(RouteGroupBuilder group, string route, MetadataCollection collection)
    {
        group.MapPut($"/{route}/manual", (Guid mediaEntityId, CollectionRequest request, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct) => SetCollectionAsync(mediaEntityId, request, collection, catalog, service, ct));
        group.MapDelete($"/{route}/manual", (Guid mediaEntityId, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct) => ClearCollectionAsync(mediaEntityId, collection, catalog, service, ct));
    }

    private static async Task<IResult> GetAsync(Guid mediaEntityId, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct)
    {
        var target = Resolve(catalog, mediaEntityId);
        if (target is null) return Results.NotFound();
        return Results.Ok(await ReadModelAsync(target, service, ct));
    }

    private static async Task<IResult> UpdateSettingsAsync(Guid mediaEntityId, SettingsRequest? request, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct)
    {
        var target = Resolve(catalog, mediaEntityId);
        if (target is null) return Results.NotFound();
        if (request is null || !Reasonable(request.PreferredMetadataLanguage) || !Reasonable(request.MetadataCountryRegion)) return Invalid("invalid_metadata_settings", "Settings values must be null or reasonably sized strings.");
        await service.UpdateSettingsAsync(new MetadataSettings(target!.WorkId, request.PreferredMetadataLanguage, request.MetadataCountryRegion, request.LockItemMetadata), ct);
        return Results.Ok(await ReadModelAsync(target!, service, ct));
    }

    private static async Task<IResult> UpdateLockAsync(Guid mediaEntityId, string fieldCode, LockRequest? request, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct)
    {
        var target = Resolve(catalog, mediaEntityId);
        if (target is null) return Results.NotFound();
        if (!MetadataEditingFieldPolicy.IsLockable(fieldCode) || request is null) return Invalid("invalid_metadata_field", "The metadata field cannot be locked.");
        await service.SetFieldLockAsync(new MetadataFieldLock(target!.WorkId, fieldCode, request.Locked), ct);
        return Results.Ok(await ReadModelAsync(target!, service, ct));
    }

    private static async Task<IResult> SetScalarAsync(Guid mediaEntityId, string fieldCode, ScalarRequest? request, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct)
    {
        var target = Resolve(catalog, mediaEntityId);
        if (target is null) return Results.NotFound();
        if (!MetadataEditingFieldPolicy.IsScalarEditable(fieldCode) || request?.Value is null) return Invalid("invalid_metadata_field", "The metadata field cannot be edited manually.");
        if (!TryReadValue(fieldCode, request.Value.Value, out var value)) return Invalid("invalid_metadata_value", "The value has an invalid shape for this metadata field.");
        await service.SetManualScalarAsync(target!.WorkId, fieldCode, value!, DateTimeOffset.UtcNow, ct);
        return Results.Ok(await ReadModelAsync(target!, service, ct));
    }

    private static async Task<IResult> ClearScalarAsync(Guid mediaEntityId, string fieldCode, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct)
    {
        var target = Resolve(catalog, mediaEntityId);
        if (target is null) return Results.NotFound();
        if (!MetadataEditingFieldPolicy.IsScalarEditable(fieldCode)) return Invalid("invalid_metadata_field", "The metadata field cannot be edited manually.");
        await service.ClearManualScalarAsync(target!.WorkId, fieldCode, ct);
        return Results.Ok(await ReadModelAsync(target!, service, ct));
    }

    private static async Task<IResult> SetCollectionAsync(Guid mediaEntityId, CollectionRequest? request, MetadataCollection collection, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct)
    {
        var target = Resolve(catalog, mediaEntityId);
        if (target is null) return Results.NotFound();
        if (request?.Values is null || request.Values.Any(value => !Reasonable(value))) return Invalid("invalid_metadata_collection", "Collection values must be non-null and reasonably sized strings.");
        var values = request.Values.ToArray();
        switch (collection)
        {
            case MetadataCollection.Genres: await service.SetManualGenresAsync(target!.WorkId, values, DateTimeOffset.UtcNow, ct); break;
            case MetadataCollection.Tags: await service.SetManualTagsAsync(target!.WorkId, values, DateTimeOffset.UtcNow, ct); break;
            case MetadataCollection.Studios: await service.SetManualStudiosAsync(target!.WorkId, values, DateTimeOffset.UtcNow, ct); break;
        }
        return Results.Ok(await ReadModelAsync(target!, service, ct));
    }

    private static async Task<IResult> ClearCollectionAsync(Guid mediaEntityId, MetadataCollection collection, IMediaCatalogReader catalog, IMetadataEditingService service, CancellationToken ct)
    {
        var target = Resolve(catalog, mediaEntityId);
        if (target is null) return Results.NotFound();
        switch (collection)
        {
            case MetadataCollection.Genres: await service.ClearManualGenresAsync(target!.WorkId, ct); break;
            case MetadataCollection.Tags: await service.ClearManualTagsAsync(target!.WorkId, ct); break;
            case MetadataCollection.Studios: await service.ClearManualStudiosAsync(target!.WorkId, ct); break;
        }
        return Results.Ok(await ReadModelAsync(target!, service, ct));
    }

    private static MediaTarget? Resolve(IMediaCatalogReader catalog, Guid id)
    { var entry = id == Guid.Empty ? null : catalog.GetById(new MediaEntityId(id)); return entry is null ? null : new(entry.Id.Value, entry.Media.Work.Id); }
    private static Task<MetadataEditorHttpResponse> ReadModelAsync(MediaTarget target, IMetadataEditingService service, CancellationToken ct) => service.GetAsync(target.WorkId, ct).AsTask().ContinueWith(task => MetadataEditorHttpResponse.From(target.MediaId, task.Result), ct);
    private static IResult Invalid(string code, string message) => Results.BadRequest(new ErrorResponse(code, message));
    private static bool Reasonable(string? value) => value is null || (value.Length <= 256 && !value.Contains('\0'));
    private static bool TryReadValue(string field, JsonElement json, out MetadataValue? value)
    { value = null; if (field is "title" or "original-title" or "description" && json.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(json.GetString())) { value = MetadataValue.Text(json.GetString()!); return true; } if (field == "release-date" && json.ValueKind == JsonValueKind.String && DateOnly.TryParse(json.GetString(), out var date)) { value = MetadataValue.Date(date); return true; } return false; }

    private sealed record MediaTarget(Guid MediaId, WorkId WorkId);
    private enum MetadataCollection { Genres, Tags, Studios }
}

public static class MetadataEditingFieldPolicy
{
    private static readonly HashSet<string> Scalars = new(["title", "original-title", "description", "release-date"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Locks = new(["title", "original-title", "description", "release-date", "genre", "genres", "tag", "tags", "studio", "studios"], StringComparer.OrdinalIgnoreCase);
    public static bool IsScalarEditable(string fieldCode) => !string.IsNullOrWhiteSpace(fieldCode) && Scalars.Contains(fieldCode);
    public static bool IsLockable(string fieldCode) => !string.IsNullOrWhiteSpace(fieldCode) && Locks.Contains(fieldCode);
}

public sealed record SettingsRequest(string? PreferredMetadataLanguage, string? MetadataCountryRegion, bool LockItemMetadata);
public sealed record LockRequest(bool Locked);
public sealed record ScalarRequest(JsonElement? Value);
public sealed record CollectionRequest(IReadOnlyCollection<string>? Values);
public sealed record ErrorResponse(string Code, string Message);
public sealed record MetadataEditorHttpResponse(Guid MediaId, Guid WorkId, MetadataSettingsResponse Settings, IReadOnlyCollection<MetadataFieldLockResponse> Locks, IReadOnlyCollection<MetadataFieldResponse> Fields, IReadOnlyCollection<MetadataCollectionResponse> Collections, IReadOnlyCollection<NormalizedPerson> People, IReadOnlyCollection<MetadataCreditResponse> Credits)
{
    public static MetadataEditorHttpResponse From(Guid mediaId, MetadataEditorSnapshot model) => new(mediaId, model.WorkId.Value, new(model.Settings.PreferredMetadataLanguage, model.Settings.MetadataCountryRegion, model.Settings.LockItemMetadata), model.Locks.Select(x => new MetadataFieldLockResponse(x.FieldCode, x.IsLocked)).ToArray(), model.Fields.Select(MetadataFieldResponse.From).ToArray(), model.Collections.Select(MetadataCollectionResponse.From).ToArray(), model.People, model.Credits.Select(x => new MetadataCreditResponse(x.PersonId, x.WorkId.Value, x.CreditType, x.Character, x.Job, x.Ordinal, x.Origin.ToString(), x.ProviderCode, x.ObservedAt)).ToArray());
}
public sealed record MetadataSettingsResponse(string? PreferredMetadataLanguage, string? MetadataCountryRegion, bool LockItemMetadata);
public sealed record MetadataFieldLockResponse(string FieldCode, bool Locked);
public sealed record MetadataFieldResponse(string FieldCode, object? Effective, object? Manual, bool Locked, IReadOnlyCollection<MetadataSourceResponse> Sources)
{ public static MetadataFieldResponse From(MetadataEditorField x) => new(x.FieldCode, MetadataValueResponse.From(x.EffectiveValue), MetadataValueResponse.From(x.ManualValue), x.IsLocked, x.Sources.Select(MetadataSourceResponse.From).ToArray()); }
public sealed record MetadataSourceResponse(string Origin, string? ProviderCode, DateTimeOffset ObservedAt, object? Value)
{ public static MetadataSourceResponse From(MetadataEditorSource x) => new(x.Origin.ToString(), x.ProviderCode, x.ObservedAt, MetadataValueResponse.From(x.Value)); }
public sealed record MetadataCollectionResponse(string CollectionCode, IReadOnlyCollection<string> Effective, IReadOnlyCollection<string> Manual, bool Locked, IReadOnlyCollection<MetadataCollectionSourceResponse> Sources)
{ public static MetadataCollectionResponse From(MetadataEditorCollectionProjection x) => new(x.CollectionCode, x.Effective, x.Manual, x.IsLocked, x.Sources.Select(s => MetadataCollectionSourceResponse.From(x.CollectionCode, s)).ToArray()); }
public sealed record MetadataCollectionSourceResponse(string Origin, string? ProviderCode, DateTimeOffset ObservedAt, IReadOnlyCollection<string> Values)
{ public static MetadataCollectionSourceResponse From(string collectionCode, RichMetadataSourceSnapshot source) => new(source.Origin.ToString(), source.ProviderCode, source.ObservedAt, collectionCode.Equals("genres", StringComparison.OrdinalIgnoreCase) ? source.Genres : collectionCode.Equals("tags", StringComparison.OrdinalIgnoreCase) ? source.Tags : source.Studios.Select(x => x.Name).ToArray()); }
public sealed record MetadataCreditResponse(Guid PersonId, Guid WorkId, string CreditType, string? Character, string? Job, int? Ordinal, string Origin, string? ProviderCode, DateTimeOffset ObservedAt);
public static class MetadataValueResponse
{ public static object? From(MetadataValue? value) => value is null ? null : value.Kind switch { MetadataValueKind.Text => value.TextValue, MetadataValueKind.Integer => value.IntegerValue, MetadataValueKind.Decimal => value.DecimalValue, MetadataValueKind.Boolean => value.BooleanValue, MetadataValueKind.Date => value.DateValue, MetadataValueKind.DateTimeOffset => value.DateTimeOffsetValue, _ => null }; }
