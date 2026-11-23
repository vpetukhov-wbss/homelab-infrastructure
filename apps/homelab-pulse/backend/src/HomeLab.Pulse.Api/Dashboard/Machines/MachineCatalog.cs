namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed record MachineSource(string Alias, string Source);

internal sealed record ApprovedMachine(
    PublicNode Node, HardwareProfile? Hardware, IReadOnlyDictionary<string, DiskHardware> DiskHardware,
    IReadOnlyList<MachineSource> Filesystems, IReadOnlyList<MachineSource> Temperatures, IReadOnlyList<MachineSource> Fans,
    IReadOnlyList<MachineSource> Disks, bool IncludeLoad, bool IncludeUptime);

internal sealed class MachineCatalog
{
    public MachineCatalog(PublicCatalog catalog, IConfiguration config, ApprovedHardware inventory, IWebHostEnvironment environment)
    {
        Machines = catalog.Nodes.Select(node =>
        {
            var section = config.GetSection($"Dashboard:PublicNodes:{node.AgentId}:Machine");
            HardwareProfile? hardware = null;
            Dictionary<string, DiskHardware> disks = new(StringComparer.Ordinal);
            if (section["HardwareProfilePath"] is { } path)
            {
                (hardware, disks) = inventory.Read(path, environment.ContentRootPath);
            }

            return new ApprovedMachine(node, hardware, disks, Sources(section, "Filesystems"), Sources(section, "Temperatures"),
                Sources(section, "Fans"), Sources(section, "Disks"), Enabled(section, "IncludeLoad"), Enabled(section, "IncludeUptime"));
        }).ToArray();
        Aliases = Machines.Select(machine => machine.Node.Alias).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<ApprovedMachine> Machines { get; }

    public IReadOnlySet<string> Aliases { get; }

    private static MachineSource[] Sources(IConfigurationSection section, string key)
    {
        var sources = section.GetSection(key).GetChildren().Take(17)
            .Select(item => item.Value is { } source && PublicCatalog.IsAlias(item.Key) && PublicCatalog.IsAlias(source)
                && !string.Equals(item.Key, source, StringComparison.Ordinal)
                    ? new MachineSource(item.Key, source) : throw InvalidCatalog())
            .OrderBy(item => item.Alias, StringComparer.Ordinal).ToArray();
        return sources.Length <= 16 && sources.Select(item => item.Source).Distinct(StringComparer.Ordinal).Count() == sources.Length
            ? sources : throw InvalidCatalog();
    }

    private static bool Enabled(IConfigurationSection section, string key) =>
        section[key] is not { } setting ? false : bool.TryParse(setting, out var result) ? result : throw InvalidCatalog();

    private static InvalidOperationException InvalidCatalog() =>
        new("Dashboard machine sources require distinct, valid aliases and source selectors, and valid readout options.");
}
