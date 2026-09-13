namespace Hispania.Media.Domain.Enrichment;

public sealed record MetadataValue
{
    private MetadataValue(
        MetadataValueKind kind,
        string? textValue,
        long? integerValue,
        decimal? decimalValue,
        bool? booleanValue,
        DateOnly? dateValue,
        DateTimeOffset? dateTimeOffsetValue)
    {
        Kind = kind;
        TextValue = textValue;
        IntegerValue = integerValue;
        DecimalValue = decimalValue;
        BooleanValue = booleanValue;
        DateValue = dateValue;
        DateTimeOffsetValue = dateTimeOffsetValue;
    }

    public MetadataValueKind Kind { get; }

    public string? TextValue { get; }

    public long? IntegerValue { get; }

    public decimal? DecimalValue { get; }

    public bool? BooleanValue { get; }

    public DateOnly? DateValue { get; }

    public DateTimeOffset? DateTimeOffsetValue { get; }

    public static MetadataValue Text(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException("metadata-value.text.required", "A text MetadataValue is required.");
        }

        return new(MetadataValueKind.Text, value, null, null, null, null, null);
    }

#pragma warning disable CA1720 // Integer and Decimal are the accepted domain value-kind factory names.
    public static MetadataValue Integer(long value) =>
        new(MetadataValueKind.Integer, null, value, null, null, null, null);

    public static MetadataValue Decimal(decimal value) =>
        new(MetadataValueKind.Decimal, null, null, value, null, null, null);
#pragma warning restore CA1720

    public static MetadataValue Boolean(bool value) =>
        new(MetadataValueKind.Boolean, null, null, null, value, null, null);

    public static MetadataValue Date(DateOnly value) =>
        new(MetadataValueKind.Date, null, null, null, null, value, null);

    public static MetadataValue DateTimeOffset(DateTimeOffset value) =>
        new(MetadataValueKind.DateTimeOffset, null, null, null, null, null, value);
}
