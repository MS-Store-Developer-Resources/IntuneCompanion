namespace IntuneCompanion.Models;

public enum IntuneAppType
{
    Win32,
    Store
}

public sealed record IntuneApp
{
    public required string AppId { get; init; }
    public required string DisplayName { get; init; }
    public required string UserSid { get; init; }
    public required IntuneAppType AppType { get; init; }

    public override string ToString() => $"{DisplayName} ({AppId})";
}
