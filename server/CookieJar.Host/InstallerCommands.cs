using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CookieJar.Host;

/// <summary>
/// Command-line subcommands invoked by <c>install.ps1</c> / <c>uninstall.ps1</c>
/// or directly by the user. These run interactively (stdin not redirected as
/// native-messaging frames).
/// </summary>
public static class InstallerCommands
{
    private const string HostName = "com.cookiejar.host";

    public static int Dispatch(string[] args)
    {
        return args[0] switch
        {
            "--print-token" => PrintToken(),
            "--rotate-token" => RotateToken(),
            "--write-manifest" => WriteManifest(args),
            "--remove-manifest" => RemoveManifest(),
            "--help" or "-h" => PrintHelp(0),
            _ => PrintHelp(1),
        };
    }

    private static int PrintToken()
    {
        Console.WriteLine(TokenStore.LoadOrCreate());
        return 0;
    }

    private static int RotateToken()
    {
        Console.WriteLine(TokenStore.Rotate());
        return 0;
    }

    private static int WriteManifest(string[] args)
    {
        // --write-manifest <hostExePath> <extensionId> [--browser edge|chrome]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --write-manifest <hostExePath> <extensionId> [--browser edge|chrome]");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Manifest installation is currently Windows-only.");
            return 2;
        }

        var hostPath = Path.GetFullPath(args[1]);
        var extensionId = args[2];
        var browser = "edge";
        for (var i = 3; i < args.Length - 1; i++)
        {
            if (args[i] == "--browser")
            {
                browser = args[i + 1].ToLowerInvariant();
            }
        }

        WriteManifestWindows(hostPath, extensionId, browser);
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteManifestWindows(string hostPath, string extensionId, string browser)
    {
        var manifestDir = browser switch
        {
            "chrome" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data", "NativeMessagingHosts"),
            _ => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "User Data", "NativeMessagingHosts"),
        };
        Directory.CreateDirectory(manifestDir);

        var manifestPath = Path.Combine(manifestDir, $"{HostName}.json");
        var manifest = new JsonObject
        {
            ["name"] = HostName,
            ["description"] = "CookieJar local cookie broker",
            ["path"] = hostPath,
            ["type"] = "stdio",
            ["allowed_origins"] = new JsonArray($"chrome-extension://{extensionId}/"),
        };

        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var registryRoot = browser switch
        {
            "chrome" => @"HKCU\Software\Google\Chrome\NativeMessagingHosts",
            _ => @"HKCU\Software\Microsoft\Edge\NativeMessagingHosts",
        };
        RunReg("add", $@"{registryRoot}\{HostName}", "/ve", "/t", "REG_SZ", "/d", manifestPath, "/f");

        Console.WriteLine($"Wrote {manifestPath}");
        Console.WriteLine($"Registered {registryRoot}\\{HostName}");
    }

    private static int RemoveManifest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        foreach (var browser in new[] { "edge", "chrome" })
        {
            var dir = browser == "chrome"
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "User Data", "NativeMessagingHosts")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Edge", "User Data", "NativeMessagingHosts");

            var path = Path.Combine(dir, $"{HostName}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"Removed {path}");
            }

            var registryRoot = browser == "chrome"
                ? @"HKCU\Software\Google\Chrome\NativeMessagingHosts"
                : @"HKCU\Software\Microsoft\Edge\NativeMessagingHosts";
            RunReg("delete", $@"{registryRoot}\{HostName}", "/f");
        }

        return 0;
    }

    private static void RunReg(params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("reg.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit();
        }
        catch
        {
            // Ignored; install.ps1 handles fallbacks.
        }
    }

    private static int PrintHelp(int exitCode)
    {
        Console.WriteLine("CookieJar.Host -- local cookie broker");
        Console.WriteLine();
        Console.WriteLine("Run with no arguments for native-messaging mode (launched by Edge/Chrome).");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  --print-token                                          Print (or create) the bearer token.");
        Console.WriteLine("  --rotate-token                                         Generate a new bearer token.");
        Console.WriteLine("  --write-manifest <hostExe> <extId> [--browser edge|chrome]");
        Console.WriteLine("                                                         Install native-messaging manifest + registry key.");
        Console.WriteLine("  --remove-manifest                                      Remove the native-messaging manifest + registry key.");
        return exitCode;
    }
}
