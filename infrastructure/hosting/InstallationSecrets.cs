using System.Security.AccessControl;
using System.Security.Principal;

namespace Hispania.Hosting;

// Internal host infrastructure, not a public module SDK or a credential inventory.
public interface IInstallationSecretProvider
{
    string Resolve(string reference);
}

public sealed class InstallationSecrets(string root) : IInstallationSecretProvider
{
    public static void ValidateReference(string reference)
    {
        var parts = reference.Split(':');
        ProductionConfiguration.Require(parts.Length == 2 && parts[0] is "env" or "file" && parts[1].Length is > 0 and <= 128 && parts[1].All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'),
            "secret reference", "use env:NAME or file:NAME with uppercase opaque names");
    }

    public string Resolve(string reference)
    {
        ValidateReference(reference);
        var parts = reference.Split(':');
        string? value;
        if (parts[0] == "env") value = Environment.GetEnvironmentVariable("HISPANIA_SECRET_" + parts[1]);
        else
        {
            var path = Path.Combine(root, parts[1] + ".secret");
            if (!File.Exists(path)) throw Missing(reference);
            ValidateProtectedFile(path);
            if (new FileInfo(path).Length > 65536) throw new DeploymentException(3, "Secret file exceeds the supported size; reprovision bounded material.");
            value = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        if (string.IsNullOrEmpty(value)) throw Missing(reference);
        if (value.Length > 65536) throw new DeploymentException(3, "Secret exceeds the supported size; reprovision bounded material.");
        return value;
    }

    private static DeploymentException Missing(string reference) => new(3, $"Missing secret: {reference}; provision it for the service account.");

    public static void ValidateProtectedFile(string path)
    {
        if (!File.Exists(path)) throw new DeploymentException(3, "Missing private material; provision the referenced file in secrets/.");
        for (var item = new FileInfo(path) as FileSystemInfo; item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new DeploymentException(3, "Private material cannot use symbolic links or reparse points.");
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            var current = WindowsIdentity.GetCurrent().User!;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            if (!current.Equals(owner) && !system.Equals(owner) && !administrators.Equals(owner))
                throw new DeploymentException(3, "Private file owner must be the service identity or an operating-system administrator.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0 &&
                    !current.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference) && !administrators.Equals(rule.IdentityReference))
                    throw new DeploymentException(3, "Private file ACL grants another identity access; restrict it to the service identity, SYSTEM and Administrators.");
        }
        else
        {
            LinuxFileOwnership.Validate(path);
            var mode = File.GetUnixFileMode(path);
            if (mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite) && mode != UnixFileMode.UserRead)
                throw new DeploymentException(3, "Private file permissions must be 0600 or 0400; remove group/other access.");
            if (File.GetUnixFileMode(Path.GetDirectoryName(path)!) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                throw new DeploymentException(3, "Private file directory permissions must be 0700; restrict secrets/ to its service owner.");
        }
    }
}
