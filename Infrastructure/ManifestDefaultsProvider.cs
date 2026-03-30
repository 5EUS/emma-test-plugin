using System.Text.Json;

namespace EMMA.TestPlugin.Infrastructure;

public static class ManifestDefaultsProvider
{
    public static ManifestDefaults Load()
    {
        var fallback = new ManifestDefaults(
            250,
            512,
            ["api.mangadex.org", "uploads.mangadex.org"],
            []);

        foreach (var manifestPath in EnumerateManifestCandidates())
        {
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;

                var capabilities = root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                    ? caps
                    : default;

                var cpu = capabilities.ValueKind == JsonValueKind.Object
                    && capabilities.TryGetProperty("cpuBudgetMs", out var cpuElement)
                    && cpuElement.TryGetInt32(out var parsedCpu)
                        ? parsedCpu
                        : fallback.CpuBudgetMs;

                var memory = capabilities.ValueKind == JsonValueKind.Object
                    && capabilities.TryGetProperty("memoryMb", out var memElement)
                    && memElement.TryGetInt32(out var parsedMem)
                        ? parsedMem
                        : fallback.MemoryMb;

                var permissions = root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object
                    ? perms
                    : default;

                var domains = ReadStringArray(permissions, "domains", fallback.Domains);
                var paths = ReadStringArray(permissions, "paths", fallback.Paths);

                return new ManifestDefaults(cpu, memory, domains, paths);
            }
            catch
            {
            }
        }

        return fallback;
    }

    private static IEnumerable<string> EnumerateManifestCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "EMMA.TestPlugin.plugin.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "EMMA.TestPlugin.plugin.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "src", "EMMA.TestPlugin", "EMMA.TestPlugin.plugin.json");
    }

    private static string[] ReadStringArray(JsonElement permissions, string propertyName, IReadOnlyList<string> fallback)
    {
        if (permissions.ValueKind != JsonValueKind.Object
            || !permissions.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [.. fallback];
        }

        return [.. element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)];
    }
}

public readonly record struct ManifestDefaults(
    int CpuBudgetMs,
    int MemoryMb,
    string[] Domains,
    string[] Paths);