namespace Hispania.ExtensionDemo.Contracts;

/// <summary>Public, versioned contract used to demonstrate an independently implemented extension.</summary>
public interface IExtensionDemoV1
{
    string GetMessage();
    Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken);
}
