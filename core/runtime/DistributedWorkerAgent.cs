using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public sealed record DistributedWorkerAgentOptions(
    Uri CoordinatorBaseUri,
    string NodeId,
    X509Certificate2 ClientCertificate,
    IReadOnlyCollection<string> CoordinatorCertificateSha256Pins,
    DistributedWorkerInventoryV1 Inventory,
    TimeSpan? LeaseDuration = null,
    TimeSpan? PollInterval = null,
    TimeSpan? HeartbeatInterval = null);

/// <summary>Long-polling HTTPS/mTLS worker loop. The executor is capability-agnostic and may invoke local OOP modules.</summary>
public sealed class DistributedWorkerAgent : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DistributedWorkerAgentOptions options;
    private readonly IDistributedCapabilityExecutorV1 executor;
    private readonly HttpClient client;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan heartbeatInterval;
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancellation, Task Task)> active = new(StringComparer.Ordinal);
    private DateTimeOffset lastHeartbeatUtc;

    public DistributedWorkerAgent(DistributedWorkerAgentOptions options, IDistributedCapabilityExecutorV1 executor)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        if (!options.CoordinatorBaseUri.IsAbsoluteUri || options.CoordinatorBaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The coordinator URI must use HTTPS.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.NodeId) || options.NodeId.Length > 128) throw new ArgumentException("A bounded node ID is required.", nameof(options));
        if (!options.ClientCertificate.HasPrivateKey || options.ClientCertificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || options.ClientCertificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new ArgumentException("The worker client certificate must be current and have a private key.", nameof(options));
        if (options.CoordinatorCertificateSha256Pins.Count == 0 || options.CoordinatorCertificateSha256Pins.Any(static pin => pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
            throw new ArgumentException("At least one coordinator SHA-256 certificate pin is required.", nameof(options));
        if (options.Inventory.Capabilities.Count > 256 || options.Inventory.MaximumConcurrentJobs is < 1 or > 1024)
            throw new ArgumentException("Worker inventory exceeds supported bounds.", nameof(options));

        leaseDuration = options.LeaseDuration ?? TimeSpan.FromSeconds(30);
        pollInterval = options.PollInterval ?? TimeSpan.FromMilliseconds(500);
        heartbeatInterval = options.HeartbeatInterval ?? TimeSpan.FromSeconds(5);
        if (leaseDuration < TimeSpan.FromSeconds(5) || leaseDuration > TimeSpan.FromMinutes(5) || heartbeatInterval <= TimeSpan.Zero || heartbeatInterval >= leaseDuration || pollInterval <= TimeSpan.Zero)
            throw new ArgumentException("Worker timing settings are invalid.", nameof(options));

        var handler = new HttpClientHandler();
        handler.ClientCertificateOptions = ClientCertificateOption.Manual;
        handler.ClientCertificates.Add(options.ClientCertificate);
        var pins = options.CoordinatorCertificateSha256Pins.Select(static pin => pin.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
            pins.Contains(Convert.ToHexString(SHA256.HashData(certificate.RawData)));
        client = new HttpClient(handler) { BaseAddress = new Uri(options.CoordinatorBaseUri, "/internal/worker/v1/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            lastHeartbeatUtc = DateTimeOffset.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTimeOffset.UtcNow - lastHeartbeatUtc >= heartbeatInterval) await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                    if (active.Count < options.Inventory.MaximumConcurrentJobs)
                    {
                        var leased = await client.PostAsync(NodePath("jobs/lease"), content: null, cancellationToken).ConfigureAwait(false);
                        if (leased.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            var job = await leased.Content.ReadFromJsonAsync<DistributedLeasedJobV1>(JsonOptions, cancellationToken).ConfigureAwait(false);
                            if (job is not null) StartJob(job, cancellationToken);
                        }
                        else if (leased.StatusCode == System.Net.HttpStatusCode.NoContent) { }
                        else if (leased.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Coordinator revoked or disabled this worker identity.");
                        else leased.EnsureSuccessStatusCode();
                    }
                    await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
                {
                    await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (var item in active.Values) item.Cancellation.Cancel();
            try { await Task.WhenAll(active.Values.Select(static item => item.Task)).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or HttpRequestException) { }
            try { using var _ = await client.PostAsync(NodePath("disconnect"), content: null, CancellationToken.None).ConfigureAwait(false); }
            catch (HttpRequestException) { }
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var request = new DistributedWorkerConnectRequestV1(ProtocolVersion, ProtocolVersion, options.Inventory);
        using var response = await client.PostAsJsonAsync(NodePath("connect"), request, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Coordinator rejected this worker certificate or its capability authorization.");
        response.EnsureSuccessStatusCode();
        var negotiation = await response.Content.ReadFromJsonAsync<DistributedWorkerConnectResponseV1>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Coordinator returned an empty worker negotiation response.");
        if (negotiation.SelectedProtocolVersion != ProtocolVersion) throw new InvalidDataException("Coordinator selected an unsupported worker protocol version.");
        var unapproved = options.Inventory.Capabilities.Except(negotiation.AuthorizedCapabilities).ToArray();
        if (unapproved.Length > 0) throw new UnauthorizedAccessException("Coordinator did not authorize one or more advertised worker capabilities.");
        lastHeartbeatUtc = DateTimeOffset.UtcNow;
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var heartbeat = new DistributedWorkerHeartbeatV1(active.Keys.ToArray());
        using var response = await client.PostAsJsonAsync(NodePath("heartbeat"), heartbeat, JsonOptions, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var instructions = await response.Content.ReadFromJsonAsync<DistributedWorkerHeartbeatResponseV1>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (instructions is not null)
            foreach (var jobId in instructions.CancelledJobIds)
                if (active.TryGetValue(jobId, out var job)) job.Cancellation.Cancel();
        lastHeartbeatUtc = DateTimeOffset.UtcNow;
    }

    private void StartJob(DistributedLeasedJobV1 job, CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (job.Request.TimeoutSeconds is > 0) linked.CancelAfter(TimeSpan.FromSeconds(job.Request.TimeoutSeconds.Value));
        if (!active.TryAdd(job.Lease.JobId, (linked, Task.CompletedTask))) { linked.Dispose(); return; }
        var task = Task.Run(() => ExecuteJobAsync(job, linked), CancellationToken.None);
        active.TryUpdate(job.Lease.JobId, (linked, task), (linked, Task.CompletedTask));
    }

    private async Task ExecuteJobAsync(DistributedLeasedJobV1 job, CancellationTokenSource cancellation)
    {
        var nodePath = NodePath($"jobs/{Uri.EscapeDataString(job.Lease.JobId)}");
        try
        {
            using var started = await client.PostAsJsonAsync(nodePath + "/start", new DistributedWorkerStartV1(job.Lease.Generation), JsonOptions, cancellation.Token).ConfigureAwait(false);
            started.EnsureSuccessStatusCode();
            var result = await executor.ExecuteAsync(job.Request, job.Lease.Generation, cancellation.Token).ConfigureAwait(false);
            using var completed = await client.PostAsJsonAsync(nodePath + "/complete", new DistributedWorkerCompletionV1(job.Lease.Generation, result), JsonOptions, cancellation.Token).ConfigureAwait(false);
            if (completed.StatusCode is not (System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.Conflict)) completed.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (Exception exception)
        {
            try
            {
                using var failed = await client.PostAsJsonAsync(nodePath + "/fail", new DistributedWorkerFailureV1(job.Lease.Generation, SafeFailureCode(exception)), JsonOptions, CancellationToken.None).ConfigureAwait(false);
                failed.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException) { }
        }
        finally
        {
            active.TryRemove(job.Lease.JobId, out _);
            cancellation.Dispose();
        }
    }

    private string NodePath(string suffix) => $"{Uri.EscapeDataString(options.NodeId)}/{suffix}";
    private static string SafeFailureCode(Exception exception) => exception is OperationCanceledException ? "cancelled" : exception.GetType().Name.ToLowerInvariant();
    public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
}
