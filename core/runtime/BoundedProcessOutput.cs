using System.Text;

namespace Hispania.Runtime;

public static class BoundedProcessOutput
{
    public const int MaximumCharacters = 16384;
    // Drain arbitrary lines with a fixed buffer; ReadLine would allocate an unbounded line first.
    public static async Task DrainAsync(TextReader reader, StringBuilder capture)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(capture);
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                lock (capture)
                {
                    var available = MaximumCharacters - capture.Length;
                    if (available > 0) capture.Append(buffer, 0, Math.Min(available, read));
                }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
