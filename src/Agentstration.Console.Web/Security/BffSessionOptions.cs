namespace Agentstration.Console.Web.Security;

public sealed class BffSessionOptions
{
    public const string SectionName = "Agentstration:BffSession";

    public int IdleTimeoutMinutes { get; set; } = 30;
    public int AbsoluteLifetimeHours { get; set; } = 12;
    public int RememberedAbsoluteLifetimeDays { get; set; } = 30;
    public int MaximumSessions { get; set; } = 10_000;

    public bool Validate() =>
        IdleTimeoutMinutes is >= 1 and <= 1_440 &&
        AbsoluteLifetimeHours is >= 1 and <= 168 &&
        RememberedAbsoluteLifetimeDays is >= 1 and <= 90 &&
        MaximumSessions is >= 100 and <= 100_000;
}
