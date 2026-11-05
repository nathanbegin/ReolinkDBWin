namespace ReolinkDBWin.Core.Reolink;

public sealed record ReolinkDeviceInfo
{
    public string? Name { get; init; }
    public string? Model { get; init; }
    public string? Type { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? HardwareVersion { get; init; }
    public string? Serial { get; init; }
    public int ChannelCount { get; init; } = 1;

    public bool IsDoorbell =>
        (Model?.Contains("doorbell", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Type?.Contains("bell", StringComparison.OrdinalIgnoreCase) ?? false);

    public override string ToString() =>
        $"{Name ?? "Reolink"} — {Model ?? "modèle inconnu"}" + (FirmwareVersion is null ? "" : $" (firmware {FirmwareVersion})");
}
