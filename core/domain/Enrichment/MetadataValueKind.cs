namespace Hispania.Media.Domain.Enrichment;

#pragma warning disable CA1720 // Integer and Decimal are the accepted domain value-kind names.
public enum MetadataValueKind
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTimeOffset
}
#pragma warning restore CA1720
