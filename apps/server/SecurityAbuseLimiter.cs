using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hispania.Platform.Contracts.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.Server;

// Process-local token buckets. Saturation fails closed rather than evicting active limits.
public sealed class SecurityAbuseLimiter
{
    private sealed class Bucket(double tokens, long timestamp)
    {
        public double Tokens = tokens;
        public long Timestamp = timestamp;
    }
    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Bucket deniedAudit;
    private const int MaximumBuckets = 4096;
    private const int ExpirySeconds = 600;
    public SecurityAbuseLimiter(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        deniedAudit = new(100, this.clock.GetTimestamp());
    }

    // Aggregate denial events still count in metrics; cap disk amplification process-wide.
    public bool TryRecordDeniedAudit()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            deniedAudit.Tokens = Math.Min(100, deniedAudit.Tokens + clock.GetElapsedTime(deniedAudit.Timestamp, now).TotalSeconds);
            deniedAudit.Timestamp = now;
            if (deniedAudit.Tokens < 1) return false;
            deniedAudit.Tokens--; return true;
        }
    }

    public int Acquire(string address, string? username = null)
    {
        var account = username is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username.Length <= 64 ? username.Trim().ToLowerInvariant() : "invalid")));
        var key = address + ":" + account;
        var burst = username is null ? 20 : 5;
        var refill = username is null ? 1d / 3 : 1d / 10;
        lock (gate)
        {
            var now = clock.GetTimestamp();
            foreach (var expired in buckets.Where(pair => clock.GetElapsedTime(pair.Value.Timestamp, now).TotalSeconds >= ExpirySeconds).Select(pair => pair.Key).ToArray()) buckets.Remove(expired);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                if (buckets.Count >= MaximumBuckets) return ExpirySeconds;
                buckets.Add(key, bucket = new(burst, now));
            }
            bucket.Tokens = Math.Min(burst, bucket.Tokens + clock.GetElapsedTime(bucket.Timestamp, now).TotalSeconds * refill);
            bucket.Timestamp = now;
            if (bucket.Tokens >= 1) { bucket.Tokens--; return 0; }
            return Math.Max(1, (int)Math.Ceiling((1 - bucket.Tokens) / refill));
        }
    }

    public static IResult? CheckAccount(HttpContext http, string? username)
    {
        var retry = http.RequestServices.GetRequiredService<SecurityAbuseLimiter>().Acquire(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", username ?? "invalid");
        return retry == 0 ? null : Rejected(http, retry);
    }
    public static IResult Rejected(HttpContext http, int retry)
    {
        http.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        http.RequestServices.GetService<IPlatformMetricsV1>().BestEffort(sink => sink.Increment("hispania.security.authentication.rate_limited"));
        return Results.Json(new { error = "authentication_rate_limited" }, statusCode: 429);
    }
}
