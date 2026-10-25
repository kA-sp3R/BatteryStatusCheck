using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeviceBatteryStatus.App;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Windows.Hardware;

namespace DeviceBatteryStatus.App.Testing;

internal static class SmokeProvenance
{
    internal static string SourceCommit(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var match = Regex.Match(informational, @"(?:\+|\.)([0-9a-fA-F]{40})(?:$|[.+])",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "unknown";
    }

    internal static IReadOnlyDictionary<string, string> VerifyApplicationAssemblies()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "package-manifest.json")));
        var hashes = manifest.RootElement.GetProperty("files");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assembly in new[] { typeof(PopupWindow).Assembly, typeof(AppSettings).Assembly, typeof(WindowsHidDiscovery).Assembly })
        {
            var name = assembly.GetName().Name + ".dll";
            var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name));
            if (!string.Equals(Path.GetFullPath(assembly.Location), expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The smoke loaded {name} from outside the inspected package staging directory.");
            var hash = Hash(expectedPath);
            if (!hashes.TryGetProperty(name, out var expected) || hash != expected.GetString())
                throw new InvalidDataException($"Loaded {name} differs from its inspected package payload.");
            result.Add(name, hash);
        }
        return result;
    }

    internal static object VerifyRuntime(string flavor)
    {
        using var process = Process.GetCurrentProcess();
        var coreClr = process.Modules.Cast<ProcessModule>().Single(module =>
            string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase));
        var coreLibrary = typeof(object).Assembly.Location;
        var localCoreClr = Path.Combine(AppContext.BaseDirectory, "coreclr.dll");
        var usesPackagedRuntime = string.Equals(Path.GetFullPath(coreClr.FileName),
            Path.GetFullPath(localCoreClr), StringComparison.OrdinalIgnoreCase);
        if ((flavor == "self-contained") != usesPackagedRuntime)
            throw new InvalidDataException("The separate harness did not use the runtime distribution represented by its package flavor.");
        if (usesPackagedRuntime)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "package-manifest.json")));
            foreach (var file in new[] { coreClr.FileName, coreLibrary })
            {
                var name = Path.GetFileName(file);
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name)), StringComparison.OrdinalIgnoreCase) ||
                    manifest.RootElement.GetProperty("files").GetProperty(name).GetString() != Hash(file))
                    throw new InvalidDataException("The self-contained smoke runtime differs from the actual package.");
            }
        }
        return new { usesPackagedRuntime, coreClrPath = coreClr.FileName, coreClrSha256 = Hash(coreClr.FileName),
            coreLibraryPath = coreLibrary, coreLibrarySha256 = Hash(coreLibrary) };
    }

    internal static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
