using System.Globalization;
using System.Text.Json;
using Hispania.Library.Contracts;

namespace Hispania.Library.FFprobe;

internal static class FfprobeJsonParser
{
    public static ProbeOutcome Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return Invalid();
            var name = StringValue(format, "format_name");
            if (string.IsNullOrWhiteSpace(name)) return Invalid();
            var observations = new List<ProbeStreamObservation>();
            foreach (var stream in streams.EnumerateArray()) observations.Add(ParseStream(stream));
            return new ProbeSucceeded(new ProbeResult(name, ParseDuration(StringValue(format, "duration")), observations));
        }
        catch (JsonException) { return Invalid(); }
        catch (FormatException) { return Invalid(); }
        catch (OverflowException) { return Invalid(); }
    }
    private static ProbeStreamObservation ParseStream(JsonElement stream)
    {
        var index = ParseRequiredInt(StringValue(stream, "index")); var kind = StringValue(stream, "codec_type"); var codec = StringValue(stream, "codec_name");
        return kind switch
        {
            "video" => new VideoStreamObservation(index, codec, ParseNullableInt(StringValue(stream,"width")), ParseNullableInt(StringValue(stream,"height")), ParseRational(StringValue(stream,"avg_frame_rate")), ParseNullablePositiveInt(StringValue(stream,"bits_per_raw_sample")) ?? ParseNullablePositiveInt(StringValue(stream,"bits_per_sample")), ParseNullableLong(StringValue(stream,"bit_rate"))),
            "audio" => new AudioStreamObservation(index, codec, ParseNullableInt(StringValue(stream,"sample_rate")), ParseNullableInt(StringValue(stream,"channels")), ParseNullablePositiveInt(StringValue(stream,"bits_per_raw_sample")) ?? ParseNullablePositiveInt(StringValue(stream,"bits_per_sample")), ParseNullableLong(StringValue(stream,"bit_rate"))),
            _ => new UnsupportedStreamObservation(index, kind, codec)
        };
    }
    private static TimeSpan? ParseDuration(string? value) { if (Unknown(value)) return null; if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)) throw new FormatException(); return TimeSpan.FromTicks(decimal.ToInt64(seconds * TimeSpan.TicksPerSecond)); }
    private static ObservedRational? ParseRational(string? value) { if (Unknown(value) || value == "0/0") return null; var parts = value!.Split('/'); if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) || denominator == 0) throw new FormatException(); return new ObservedRational(numerator, denominator); }
    private static int ParseRequiredInt(string? value) => ParseNullableInt(value) ?? throw new FormatException();
    private static int? ParseNullableInt(string? value) => Unknown(value) ? null : int.Parse(value!, CultureInfo.InvariantCulture);
    private static int? ParseNullablePositiveInt(string? value)
    {
        var parsed = ParseNullableInt(value);
        return parsed is 0 ? null : parsed;
    }
    private static long? ParseNullableLong(string? value) => Unknown(value) ? null : long.Parse(value!, CultureInfo.InvariantCulture);
    private static string? StringValue(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText() : null;
    private static bool Unknown(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase);
    private static ProbeFailed Invalid() => new(new ProbeFailure(ProbeFailureKind.InvalidOutput));
}
