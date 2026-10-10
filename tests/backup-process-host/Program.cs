using System.Diagnostics;
using Hispania.Server;

if (args.Length < 2) return 2;
using var listener = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(args[0]));
await BackupInstallation.TryCommandAsync(args[1..], default);
return Environment.ExitCode;

internal sealed class ListenerObserver(string checkpoint) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    public void OnNext(DiagnosticListener value) { if (value.Name == "Hispania.Backup") value.Subscribe(this); }
    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != "Checkpoint" || value.Value as string != checkpoint) return;
        Console.WriteLine("checkpoint:" + checkpoint); Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); // Parent terminates this isolated test process.
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
