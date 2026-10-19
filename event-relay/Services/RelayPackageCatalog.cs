using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayPackageCatalog
{
    public const string BonusPolicyVersion = "current-starter-pro-v1";
    private readonly RelayPackageTerms[] packages;
    public string Revision { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<RelayPackageTerms> Packages => Array.AsReadOnly(packages);

    public RelayPackageCatalog(IOptions<RelayPackageOptions> options)
    {
        var value = options.Value;
        var errors = Validate(value).ToArray();
        if (errors.Length != 0) throw new OptionsValidationException(RelayPackageOptions.SectionName, typeof(RelayPackageOptions), errors);
        Revision = value.Revision;
        packages = Effective(value).OrderBy(x => x.PackageId, StringComparer.Ordinal).Select(x => x with { }).ToArray();
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Revision, Packages = packages, BonusPolicyVersion }))));
    }

    public RelayPackageTerms Get(string id) => packages.FirstOrDefault(x => x.PackageId == id) is { } terms
        ? terms with { } : throw new ArgumentException("Unknown Relay package ID.", nameof(id));

    public static RelayPackageTerms[] Effective(RelayPackageOptions options) => options.Packages.Length == 0
        ? [new(), new() { PackageId = "pro", Name = "Pro", GasDatoshis = 1000000000, MaxActiveSubscriptions = 15, MaxEvents = 50000 }]
        : options.Packages;

    public static IEnumerable<string> Validate(RelayPackageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Revision) || options.Revision.Length > 80) yield return "Relay package revision is required (at most 80 characters).";
        var entries = Effective(options);
        if (entries.Length != 2 || entries.Select(x => x?.PackageId).Distinct(StringComparer.Ordinal).Count() != 2
            || entries.Any(x => x is null || x.PackageId is not ("starter" or "pro")))
            yield return "Relay catalog must contain exactly one starter and one pro; unknown/duplicate IDs are invalid.";
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 80 || string.IsNullOrWhiteSpace(entry.Version) || entry.Version.Length > 80
                || entry.GasDatoshis <= 0 || entry.Days is < 1 or > 365 || entry.GraceDays is < 0 or > 30
                || entry.MaxActiveSubscriptions is < 1 or > 1000 || entry.MaxEvents is < 1 or > 100000)
                yield return "Invalid package name/version/price/duration/limits; at most 100000 event receipts are supported.";
        }
        var starter = entries.FirstOrDefault(x => x?.PackageId == "starter");
        var pro = entries.FirstOrDefault(x => x?.PackageId == "pro");
        if (starter is not null && pro is not null && (pro.GasDatoshis <= starter.GasDatoshis || pro.Days != starter.Days
            || pro.MaxEvents < starter.MaxEvents || pro.MaxActiveSubscriptions < starter.MaxActiveSubscriptions))
            yield return "Pro must cost more, have the same duration, and include at least Starter's limits.";
    }

    public static RelayPackageTerms LegacyTerms(EventRelayOptions settings, long? quotedAmount = null)
    {
        var standard = settings.PaidPlanDays == 30 && settings.PaidGraceDays == 3 && settings.PaidMaxActiveSubscriptions == 5
            && settings.PaidMaxEvents == 10000 && (quotedAmount ?? settings.PaidPlanGasDatoshis) == 500000000;
        return new() { PackageId = standard ? "starter" : "legacy_custom", Version = "legacy-snapshot-v1", Name = standard ? "Starter" : "Legacy paid Relay",
            GasDatoshis = quotedAmount ?? settings.PaidPlanGasDatoshis, Days = settings.PaidPlanDays, GraceDays = settings.PaidGraceDays,
            MaxActiveSubscriptions = settings.PaidMaxActiveSubscriptions, MaxEvents = settings.PaidMaxEvents };
    }
}

public sealed class RelayPackageOptionsValidator : IValidateOptions<RelayPackageOptions>
{
    public ValidateOptionsResult Validate(string? name, RelayPackageOptions options)
    {
        var errors = RelayPackageCatalog.Validate(options).ToArray();
        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
