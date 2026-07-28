namespace Pusharoo.EventRelay.Options;

public sealed class PusharooApiOptions
{
    public const string SectionName = "PusharooApi";

    public string Endpoint { get; init; } = "http://localhost:5000";
}
