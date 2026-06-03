using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vessel.Infrastructure.Files;

internal static class OwnerOnlyFilePermissions
{
    public static void Apply(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsAcl(path);
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new IOException($"Could not restrict file permissions for '{path}'.", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsAcl(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not determine the current Windows user.");
        var security = new FileSecurity();

        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }
}
