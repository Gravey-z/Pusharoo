namespace Pusharoo.EventRelay.Options;

public sealed class PusharooApiOptions
{
    public const string SectionName = "PusharooApi";

    public string ServiceToken { get; init; } = string.Empty;
}
