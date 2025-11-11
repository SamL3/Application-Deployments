using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DevApp.Options;

public sealed class HostAvailabilityOptions
{
    [Range(1, int.MaxValue)] public int ScanIntervalSeconds { get; init; } = 60;
    public List<HostEntry> Hosts { get; init; } = new();
}

public sealed class HostEntry
{
    [Required] public string Host { get; init; } = "";
    public string? Root { get; init; }
}