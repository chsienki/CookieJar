using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace CookieJar.Host;

/// <summary>
/// Manages the shared bearer token used to authenticate HTTP callers. The token
/// lives in <c>%LOCALAPPDATA%\CookieJar\token.txt</c> with an ACL that grants
/// access only to the current user.
/// </summary>
public static class TokenStore
{
    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CookieJar");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "token.txt");

    public static string LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var token = Generate();
        File.WriteAllText(path, token);
        TryRestrictToCurrentUser(path);
        return token;
    }

    public static string Rotate(string? path = null)
    {
        path ??= DefaultPath;
        var token = Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token);
        TryRestrictToCurrentUser(path);
        return token;
    }

    private static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUserWindows(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var rules = security.GetAccessRules(true, true, typeof(NTAccount));
        foreach (FileSystemAccessRule rule in rules)
        {
            security.RemoveAccessRule(rule);
        }

        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        info.SetAccessControl(security);
    }

    private static void TryRestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            RestrictToCurrentUserWindows(path);
        }
        catch
        {
            // Best-effort: if ACL tightening fails (network share, etc.) we still have a token.
        }
    }
}
