using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace Hispania.Library.FFprobe;

internal interface IFfprobeProcessRunner { Task<FfprobeProcessResult> RunAsync(FfprobeOptions options, Stream input, CancellationToken cancellationToken); }
internal sealed record FfprobeProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputLimitExceeded, bool StartFailed);

internal sealed class FfprobeProcessRunner : IFfprobeProcessRunner
{
    public async Task<FfprobeProcessResult> RunAsync(FfprobeOptions options, Stream input, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = CreateStartInfo(options.ExecutablePath) };
            if (!process.Start()) return new(null, string.Empty, string.Empty, false, false, true);
            var stdout = ReadBoundedAsync(process.StandardOutput, options.MaximumStandardOutputBytes);
            var stderr = ReadBoundedAsync(process.StandardError, options.MaximumStandardErrorBytes);
            var feed = FeedInputAsync(input, process, cancellationToken);
            var exited = process.WaitForExitAsync(CancellationToken.None);
            var timeout = Task.Delay(options.Timeout, CancellationToken.None);
            var completed = await Task.WhenAny(exited, timeout, cancellationToken.AsTask()).ConfigureAwait(false);
            if (completed == timeout) { Kill(process); await Task.WhenAll(stdout, stderr, feed).ConfigureAwait(false); return new(null, stdout.Result.Text, stderr.Result.Text, true, stdout.Result.Exceeded || stderr.Result.Exceeded, false); }
            if (cancellationToken.IsCancellationRequested) { Kill(process); await Task.WhenAll(stdout, stderr, feed).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
            cancellationToken.ThrowIfCancellationRequested();
            await exited.ConfigureAwait(false);
            await feed.ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false);
            return new(process.ExitCode, output.Text, error.Text, false, output.Exceeded || error.Exceeded, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException) { return new(null, string.Empty, string.Empty, false, false, true); }
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("HISPANIA_SECRET_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.ArgumentList.Add("-v"); info.ArgumentList.Add("error"); info.ArgumentList.Add("-show_format"); info.ArgumentList.Add("-show_streams"); info.ArgumentList.Add("-of"); info.ArgumentList.Add("json"); info.ArgumentList.Add("pipe:0"); return info;
    }
    private static async Task FeedInputAsync(Stream input, Process process, CancellationToken cancellationToken)
    {
        try
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // ffprobe can stop reading as soon as it has parsed a fast-start MP4's
            // header. The closed pipe is harmless when ffprobe completed successfully.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (process.ExitCode != 0) throw;
        }
    }
    private static async Task<(string Text, bool Exceeded)> ReadBoundedAsync(StreamReader reader, int maximumBytes) { var buffer = new char[4096]; var builder = new StringBuilder(); var bytes = 0; var exceeded = false; int read; while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0) { for (var index = 0; index < read; index++) { var charBytes = Encoding.UTF8.GetByteCount(buffer, index, 1); if (bytes + charBytes > maximumBytes) { exceeded = true; continue; } builder.Append(buffer[index]); bytes += charBytes; } } return (builder.ToString(), exceeded); }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
}

internal static class CancellationTokenExtensions { public static Task AsTask(this CancellationToken token) { if (!token.CanBeCanceled) return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None); return Task.Delay(Timeout.InfiniteTimeSpan, token); } }
