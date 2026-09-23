using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Hispania.Library.Persistence.EFCore;

public sealed class EfRichMetadataStore(IDbContextFactory<LibraryCatalogDbContext> factory) : IRichMetadataStore
{
    public async ValueTask<RichMetadataSnapshot> LoadAsync(EnrichmentTarget target, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var id = target.WorkId?.Value ?? throw new InvalidOperationException("Rich movie metadata requires a Work target.");
        var genres = await db.RichGenres.AsNoTracking().Where(x => x.TargetId == id).OrderBy(x => x.Ordinal).ThenBy(x => x.NormalizedName).Select(x => x.Name).ToArrayAsync(cancellationToken);
        var tags = await db.RichTags.AsNoTracking().Where(x => x.TargetId == id).OrderBy(x => x.Ordinal).ThenBy(x => x.NormalizedName).Select(x => x.Name).ToArrayAsync(cancellationToken);
        var studios = await db.RichStudios.AsNoTracking().Where(x => x.TargetId == id).OrderBy(x => x.Ordinal).ThenBy(x => x.ExternalId).Select(x => new NormalizedStudio(x.ExternalId, x.Name)).ToArrayAsync(cancellationToken);
        var credits = await (from c in db.RichCredits.AsNoTracking() join p in db.RichPersons.AsNoTracking() on c.PersonId equals p.Id where c.TargetId == id orderby c.Ordinal, c.CreditType, p.Name select new { c, p }).ToArrayAsync(cancellationToken);
        var people = credits.Select(x => Person(x.p)).DistinctBy(x => (x.ProviderCode, x.ProviderId)).ToArray();
        var providers = (await db.RichGenres.AsNoTracking().Where(x => x.TargetId == id).Select(x => x.ProviderCode).ToArrayAsync(cancellationToken))
            .Concat(await db.RichTags.AsNoTracking().Where(x => x.TargetId == id).Select(x => x.ProviderCode).ToArrayAsync(cancellationToken))
            .Concat(await db.RichStudios.AsNoTracking().Where(x => x.TargetId == id).Select(x => x.ProviderCode).ToArrayAsync(cancellationToken))
            .Concat(await db.RichCredits.AsNoTracking().Where(x => x.TargetId == id).Select(x => x.ProviderCode).ToArrayAsync(cancellationToken))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sources = new List<RichMetadataSourceSnapshot>();
        foreach (var providerCode in providers)
        {
            var sourceGenres = await db.RichGenres.AsNoTracking().Where(x => x.TargetId == id && x.ProviderCode == providerCode).OrderBy(x => x.Ordinal).ThenBy(x => x.NormalizedName).ToArrayAsync(cancellationToken);
            var sourceTags = await db.RichTags.AsNoTracking().Where(x => x.TargetId == id && x.ProviderCode == providerCode).OrderBy(x => x.Ordinal).ThenBy(x => x.NormalizedName).ToArrayAsync(cancellationToken);
            var sourceStudios = await db.RichStudios.AsNoTracking().Where(x => x.TargetId == id && x.ProviderCode == providerCode).OrderBy(x => x.Ordinal).ThenBy(x => x.ExternalId).ToArrayAsync(cancellationToken);
            var sourceCredits = await (from c in db.RichCredits.AsNoTracking() join p in db.RichPersons.AsNoTracking() on c.PersonId equals p.Id where c.TargetId == id && c.ProviderCode == providerCode orderby c.Ordinal, c.CreditType, p.Name select new { c, p }).ToArrayAsync(cancellationToken);
            var observedTicks = sourceGenres.Select(x => x.ObservedUtcTicks).Concat(sourceTags.Select(x => x.ObservedUtcTicks)).Concat(sourceStudios.Select(x => x.ObservedUtcTicks)).Concat(sourceCredits.Select(x => x.c.ObservedUtcTicks)).DefaultIfEmpty().Max();
            var manual = providerCode is "manual" or "unknown";
            sources.Add(new RichMetadataSourceSnapshot(manual ? MetadataProvenanceOrigin.Manual : MetadataProvenanceOrigin.ExternalProvider, manual ? null : providerCode, new DateTimeOffset(observedTicks, TimeSpan.Zero), sourceGenres.Select(x => x.Name).ToArray(), sourceTags.Select(x => x.Name).ToArray(), sourceStudios.Select(x => new NormalizedStudio(x.ExternalId, x.Name)).ToArray(), sourceCredits.Select(x => new NormalizedPerson(x.p.ExternalId, x.p.Name, x.p.ProfileArtworkLocator, manual ? null : providerCode, x.p.Id)).DistinctBy(x => x.ProviderId).ToArray(), sourceCredits.Select(x => new NormalizedCredit(x.p.ExternalId, x.p.Name, x.c.CreditType, x.c.Character, x.c.Job, x.c.Ordinal, x.p.ProfileArtworkLocator)).ToArray()));
        }
        return new RichMetadataSnapshot(target, genres, tags, studios, people, credits.Select(x => new NormalizedCredit(x.p.ExternalId, x.p.Name, x.c.CreditType, x.c.Character, x.c.Job, x.c.Ordinal, x.p.ProfileArtworkLocator)).ToArray(), sources);
    }

    public async ValueTask ApplyAsync(RichMetadataWrite write, CancellationToken cancellationToken = default)
    {
        var id = write.Target.WorkId?.Value ?? throw new InvalidOperationException("Rich movie metadata requires a Work target.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var provider = write.Provenance.Provider?.Value ?? "unknown";
        db.RichGenres.RemoveRange(db.RichGenres.Where(x => x.TargetId == id && x.ProviderCode == provider));
        db.RichTags.RemoveRange(db.RichTags.Where(x => x.TargetId == id && x.ProviderCode == provider));
        db.RichStudios.RemoveRange(db.RichStudios.Where(x => x.TargetId == id && x.ProviderCode == provider));
        db.RichCredits.RemoveRange(db.RichCredits.Where(x => x.TargetId == id && x.ProviderCode == provider));
        await db.SaveChangesAsync(cancellationToken);
        var observed = write.Provenance.ObservedAt.UtcTicks;
        var genres = write.Metadata.Genres.Where(x => !string.IsNullOrWhiteSpace(x)).Select((name, ordinal) => new RichGenreRecord { Id = Guid.CreateVersion7(), TargetId = id, Name = name.Trim(), NormalizedName = name.Trim().ToUpperInvariant(), Ordinal = ordinal, ProviderCode = provider, ObservedUtcTicks = observed }).DistinctBy(x => x.NormalizedName).ToArray();
        var tags = write.Metadata.Tags.Where(x => !string.IsNullOrWhiteSpace(x)).Select((name, ordinal) => new RichTagRecord { Id = Guid.CreateVersion7(), TargetId = id, Name = name.Trim(), NormalizedName = name.Trim().ToUpperInvariant(), Ordinal = ordinal, ProviderCode = provider, ObservedUtcTicks = observed }).DistinctBy(x => x.NormalizedName).ToArray();
        var studios = write.Metadata.Studios.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select((studio, ordinal) => new RichStudioRecord { Id = Guid.CreateVersion7(), TargetId = id, ExternalId = studio.ProviderId, Name = studio.Name.Trim(), Ordinal = ordinal, ProviderCode = provider, ObservedUtcTicks = observed }).DistinctBy(x => x.ExternalId).ToArray();
        var personInputs = write.Metadata.People.Where(x => !string.IsNullOrWhiteSpace(x.ProviderId)).GroupBy(x => (provider, x.ProviderId)).Select(g => g.First()).ToArray();
        var existingPeople = await db.RichPersons.Where(x => x.ProviderCode == provider && personInputs.Select(p => p.ProviderId).Contains(x.ExternalId)).ToDictionaryAsync(x => x.ExternalId, cancellationToken);
        var people = personInputs.Select(x => existingPeople.TryGetValue(x.ProviderId, out var existing) ? existing : new RichPersonRecord { Id = Guid.CreateVersion7(), ProviderCode = provider, ExternalId = x.ProviderId, Name = x.Name, ProfileArtworkLocator = x.ProfileArtworkLocator }).ToArray();
        foreach (var person in people) if (personInputs.Single(x => x.ProviderId == person.ExternalId) is { } input) { person.Name = input.Name; if (input.ProfileArtworkLocator is not null) person.ProfileArtworkLocator = input.ProfileArtworkLocator; }
        db.RichGenres.AddRange(genres); db.RichTags.AddRange(tags); db.RichStudios.AddRange(studios); db.RichPersons.AddRange(people.Where(x => x.Id != Guid.Empty && db.Entry(x).State == EntityState.Detached));
        var personByExternalId = people.ToDictionary(x => x.ExternalId);
        db.RichCredits.AddRange(write.Metadata.Credits.Where(x => personByExternalId.ContainsKey(x.ProviderPersonId)).Select(x => new RichCreditRecord { Id = Guid.CreateVersion7(), TargetId = id, PersonId = personByExternalId[x.ProviderPersonId].Id, CreditType = x.CreditType, Character = x.Character, Job = x.Job, Ordinal = x.Ordinal, ProviderCode = provider, ObservedUtcTicks = observed }));
        await db.SaveChangesAsync(cancellationToken);
    }
    public async ValueTask ClearProviderAsync(EnrichmentTarget target, string providerCode, CancellationToken cancellationToken = default)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); var id = target.WorkId?.Value ?? throw new InvalidOperationException("Rich movie metadata requires a Work target."); db.RichGenres.RemoveRange(db.RichGenres.Where(x => x.TargetId == id && x.ProviderCode == providerCode)); db.RichTags.RemoveRange(db.RichTags.Where(x => x.TargetId == id && x.ProviderCode == providerCode)); db.RichStudios.RemoveRange(db.RichStudios.Where(x => x.TargetId == id && x.ProviderCode == providerCode)); db.RichCredits.RemoveRange(db.RichCredits.Where(x => x.TargetId == id && x.ProviderCode == providerCode)); await db.SaveChangesAsync(cancellationToken); }

    public async ValueTask<IReadOnlyCollection<PersonLibraryCredit>> GetCreditsForPersonAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await (from c in db.RichCredits.AsNoTracking()
                          join p in db.RichPersons.AsNoTracking() on c.PersonId equals p.Id
                          where c.PersonId == personId
                          orderby c.TargetId, c.Ordinal, c.CreditType
                          select new { c, p }).ToArrayAsync(cancellationToken);
        return rows.Select(x => new PersonLibraryCredit(x.p.Id, new WorkId(x.c.TargetId), x.p.Name, x.c.CreditType, x.c.Character, x.c.Job, x.c.Ordinal, x.c.ProviderCode is "manual" or "unknown" ? MetadataProvenanceOrigin.Manual : MetadataProvenanceOrigin.ExternalProvider, x.c.ProviderCode is "manual" or "unknown" ? null : x.c.ProviderCode, new DateTimeOffset(x.c.ObservedUtcTicks, TimeSpan.Zero))).ToArray();
    }

    public async ValueTask<PersonMetadataSnapshot?> GetPersonAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.RichPersons.AsNoTracking().SingleOrDefaultAsync(x => x.Id == personId, cancellationToken);
        return record is null ? null : new PersonMetadataSnapshot(Person(record), record.ProfileObservedUtcTicks is long ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);
    }

    public async ValueTask SetPersonProfileAsync(Guid personId, NormalizedPerson profile, DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.RichPersons.SingleAsync(x => x.Id == personId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(profile.Name)) record.Name = profile.Name;
        if (profile.ProfileArtworkLocator is not null) record.ProfileArtworkLocator = profile.ProfileArtworkLocator;
        record.Biography = profile.Biography;
        record.Birthday = profile.Birthday?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        record.Deathday = profile.Deathday?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        record.PlaceOfBirth = profile.PlaceOfBirth;
        record.ProfileObservedUtcTicks = observedAt.UtcTicks;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static NormalizedPerson Person(RichPersonRecord person) => new(person.ExternalId, person.Name, person.ProfileArtworkLocator, person.ProviderCode is "manual" or "unknown" ? null : person.ProviderCode, person.Id, person.Biography, ParseDate(person.Birthday), ParseDate(person.Deathday), person.PlaceOfBirth);
    private static DateOnly? ParseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;

    public async ValueTask SetManualCollectionAsync(EnrichmentTarget target, RichMetadataCollectionType collectionType, IReadOnlyCollection<string> values, DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var id = target.WorkId?.Value ?? throw new InvalidOperationException("Rich movie metadata requires a Work target.");
        const string provider = "unknown";
        if (collectionType == RichMetadataCollectionType.Genres) db.RichGenres.RemoveRange(db.RichGenres.Where(x => x.TargetId == id && x.ProviderCode == provider));
        if (collectionType == RichMetadataCollectionType.Tags) db.RichTags.RemoveRange(db.RichTags.Where(x => x.TargetId == id && x.ProviderCode == provider));
        if (collectionType == RichMetadataCollectionType.Studios) db.RichStudios.RemoveRange(db.RichStudios.Where(x => x.TargetId == id && x.ProviderCode == provider));
        await db.SaveChangesAsync(cancellationToken);
        var ticks = observedAt.UtcTicks;
        if (collectionType == RichMetadataCollectionType.Genres) db.RichGenres.AddRange(values.Where(x => !string.IsNullOrWhiteSpace(x)).Select((x, i) => new RichGenreRecord { Id = Guid.CreateVersion7(), TargetId = id, Name = x.Trim(), NormalizedName = x.Trim().ToUpperInvariant(), Ordinal = i, ProviderCode = provider, ObservedUtcTicks = ticks }).DistinctBy(x => x.NormalizedName));
        if (collectionType == RichMetadataCollectionType.Tags) db.RichTags.AddRange(values.Where(x => !string.IsNullOrWhiteSpace(x)).Select((x, i) => new RichTagRecord { Id = Guid.CreateVersion7(), TargetId = id, Name = x.Trim(), NormalizedName = x.Trim().ToUpperInvariant(), Ordinal = i, ProviderCode = provider, ObservedUtcTicks = ticks }).DistinctBy(x => x.NormalizedName));
        if (collectionType == RichMetadataCollectionType.Studios) db.RichStudios.AddRange(values.Where(x => !string.IsNullOrWhiteSpace(x)).Select((x, i) => new RichStudioRecord { Id = Guid.CreateVersion7(), TargetId = id, Name = x.Trim(), ExternalId = "local:" + x.Trim().ToUpperInvariant(), Ordinal = i, ProviderCode = provider, ObservedUtcTicks = ticks }).DistinctBy(x => x.ExternalId));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask ClearManualCollectionAsync(EnrichmentTarget target, RichMetadataCollectionType collectionType, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var id = target.WorkId?.Value ?? throw new InvalidOperationException("Rich movie metadata requires a Work target.");
        const string provider = "unknown";
        if (collectionType == RichMetadataCollectionType.Genres) db.RichGenres.RemoveRange(db.RichGenres.Where(x => x.TargetId == id && x.ProviderCode == provider));
        if (collectionType == RichMetadataCollectionType.Tags) db.RichTags.RemoveRange(db.RichTags.Where(x => x.TargetId == id && x.ProviderCode == provider));
        if (collectionType == RichMetadataCollectionType.Studios) db.RichStudios.RemoveRange(db.RichStudios.Where(x => x.TargetId == id && x.ProviderCode == provider));
        await db.SaveChangesAsync(cancellationToken);
    }
}
