using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed partial class ApprovedHardware
{
    private const int MaxBytes = 64 * 1024;
    private readonly JsonSchema _schema;

    public ApprovedHardware()
    {
        using var stream = typeof(ApprovedHardware).Assembly.GetManifestResourceStream("Pulse.HardwareProfile.V1")
            ?? throw new InvalidOperationException("The hardware profile v1 schema is missing.");
        using var reader = new StreamReader(stream);
        _schema = JsonSchema.FromText(reader.ReadToEnd());
    }

    public (HardwareProfile Profile, Dictionary<string, DiskHardware> Disks) Read(string path, string contentRoot)
    {
        try
        {
            using var stream = File.OpenRead(Path.GetFullPath(path, contentRoot));
            var input = new byte[MaxBytes + 1];
            var length = stream.ReadAtLeast(input, input.Length, throwOnEndOfStream: false);
            if (length > MaxBytes)
            {
                throw InvalidProfile();
            }

            using var json = JsonDocument.Parse(input.AsMemory(0, length), new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = json.RootElement;
            if (!_schema.Evaluate(root).IsValid)
            {
                throw InvalidProfile();
            }

            var machine = root.GetProperty("machine");
            var cpu = root.GetProperty("cpu");
            var memory = root.GetProperty("memory");
            var logical = Positive(cpu, "logicalCpuCount")!.Value;
            var physical = Positive(cpu, "physicalCoreCount");
            if (physical > logical)
            {
                throw InvalidProfile();
            }

            var profile = new HardwareProfile(Label(machine, "manufacturer"), Label(machine, "model"),
                new HardwareCpu(Label(cpu, "architecture")!, Label(cpu, "model"), physical, logical),
                Positive(memory, "osVisibleBytes")!.Value, ReadModules(memory));

            var disks = new Dictionary<string, DiskHardware>(StringComparer.Ordinal);
            if (root.TryGetProperty("disks", out var entries))
            {
                foreach (var entry in entries.EnumerateObject())
                {
                    if (disks.Count == 32 || !PublicCatalog.IsAlias(entry.Name))
                    {
                        throw InvalidProfile();
                    }

                    var disk = entry.Value;
                    var status = disk.GetProperty("status").GetString()!;
                    disks.Add(entry.Name, new DiskHardware(status, Positive(disk, "capacityBytes"),
                        Label(disk, "model"), Boolean(disk, "rotational")));
                }
            }

            return (profile, disks);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or JsonException)
        {
            throw InvalidProfile();
        }
    }

    private static MemoryModule[]? ReadModules(JsonElement memory)
    {
        if (!memory.TryGetProperty("modules", out var modules))
        {
            return null;
        }

        if (modules.GetArrayLength() > 32)
        {
            throw InvalidProfile();
        }

        return modules.EnumerateArray().Select(module => new MemoryModule(
            Positive(module, "capacityBytes")!.Value, Label(module, "type"),
            Positive(module, "ratedSpeedMtPerSecond"), Positive(module, "configuredSpeedMtPerSecond"))).ToArray();
    }

    private static string? Label(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var element))
        {
            return null;
        }

        var value = element.GetString()!.Trim();
        return DisplayLabel().IsMatch(value) && !IPAddress.TryParse(value, out _) && !Guid.TryParse(value, out _)
            && !EmbeddedIdentifier().IsMatch(value)
            ? value : throw InvalidProfile();
    }

    private static long? Positive(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var element))
        {
            return null;
        }

        return element.TryGetInt64(out var value) && value > 0 ? value : throw InvalidProfile();
    }

    private static bool? Boolean(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var element) ? element.GetBoolean() : null;

    private static InvalidOperationException InvalidProfile() =>
        new("Dashboard hardware inventory must be reviewed and conform to the hardware profile v1 contract and public field limits.");

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9 .()+_@,-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayLabel();

    [GeneratedRegex(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b|\b(?:[a-f0-9]{2}-){5}[a-f0-9]{2}\b|\b[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedIdentifier();
}
