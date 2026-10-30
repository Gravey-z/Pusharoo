using Microsoft.Extensions.Options;
using Pusharoo.Retention;

namespace backend.Options;

public sealed class UnusedProjectCleanupOptions
{
    public const string SectionName = "UnusedProjectCleanup";

    public string Mode { get; set; } = "disabled";

    public int RetentionDays { get; set; } = UnusedProjectRetentionPolicy.RetentionDays;

    public int PollIntervalSeconds { get; set; } = 86400;
}

public sealed class UnusedProjectCleanupOptionsValidator
    : IValidateOptions<UnusedProjectCleanupOptions>
{
    public ValidateOptionsResult Validate(string? name, UnusedProjectCleanupOptions options)
    {
        var failures = new List<string>();
        if (options.Mode is not ("disabled" or "dry-run" or "enabled"))
            failures.Add("UnusedProjectCleanup:Mode must be disabled, dry-run or enabled.");
        if (options.RetentionDays != UnusedProjectRetentionPolicy.RetentionDays)
            failures.Add("UnusedProjectCleanup:RetentionDays must be 30 for policy v1.");
        if (options.PollIntervalSeconds is < 10 or > 86400)
            failures.Add("UnusedProjectCleanup:PollIntervalSeconds must be between 10 and 86400.");
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
