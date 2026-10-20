using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Options;

public sealed class RelayPackageOptions
{
    public const string SectionName = "RelayPackages";
    public string Revision { get; init; } = "launch-v1";

    public RelayPackageTerms[] Packages { get; init; } = [];
}
