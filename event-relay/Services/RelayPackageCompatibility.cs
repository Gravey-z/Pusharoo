using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public static class RelayPackageCompatibility
{
    public static string PackageId(RelayEntitlementDocument value) =>
        value.PackageId
        ?? value.PackageTerms?.PackageId
        ?? (
            value.Plan != "paid" ? value.Plan
            : value.MaxActiveSubscriptions == 5 && value.MaxEvents == 10000 ? "starter"
            : "legacy_custom"
        );

    public static bool NeedsReconciliation(RelayEntitlementDocument value, DateTime now) =>
        value.Plan == "paid"
        && (
            PackageId(value) is "legacy_custom" or "legacy_carryover"
            || value.PackageTerms is null
            || value.PeriodStart > now
        );
}
