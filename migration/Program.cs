using System.Text.Json;
using MongoDB.Driver;
using Pusharoo.Migration;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine(
        "Pusharoo.Migration inventory|apply|verify|release-lock --database NAME [--mapping FILE] [--output FILE] [--writers-stopped --backup-verified --writes-disabled] [--expected-token TOKEN]"
    );
    Console.WriteLine(
        "Connection string: PUSHAROO_MIGRATION_CONNECTION. No database defaults or application startup/index side effects. Apply is offline only."
    );
    return 0;
}

try
{
    var command = args[0];
    var allowed = new HashSet<string>
    {
        "--database",
        "--mapping",
        "--output",
        "--writers-stopped",
        "--backup-verified",
        "--writes-disabled",
        "--expected-token",
    };
    var values = new Dictionary<string, string>();
    for (var i = 1; i < args.Length; i++)
    {
        var name = args[i];
        if (!allowed.Contains(name) || values.ContainsKey(name))
            throw new MigrationBlockedException("Unknown or repeated argument.");
        var flag = name is "--writers-stopped" or "--backup-verified" or "--writes-disabled";
        if (!flag && (i + 1 == args.Length || args[i + 1].StartsWith("--")))
            throw new MigrationBlockedException("Missing argument value.");
        values[name] = flag ? "true" : args[++i];
    }
    if (
        !values.TryGetValue("--database", out var databaseName)
        || string.IsNullOrWhiteSpace(databaseName)
    )
        throw new MigrationBlockedException("An explicit database name is required.");
    var connection = Environment.GetEnvironmentVariable("PUSHAROO_MIGRATION_CONNECTION");
    if (string.IsNullOrWhiteSpace(connection))
        throw new MigrationBlockedException(
            "Set PUSHAROO_MIGRATION_CONNECTION without putting credentials in arguments or reports."
        );
    var repository = new MigrationRepository(new MongoClient(connection).GetDatabase(databaseName));
    var runner = new MigrationRunner(repository, new());
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    var ct = cancellation.Token;
    var decisions = values.TryGetValue("--mapping", out var file)
        ? JsonSerializer.Deserialize<MigrationDecisions>(
            await File.ReadAllTextAsync(file, ct),
            MigrationRepository.JsonOptions
        ) ?? throw new MigrationBlockedException("Mapping file is empty.")
        : null;
    var options = new MigrationApplyOptions(
        values.ContainsKey("--writers-stopped"),
        values.ContainsKey("--backup-verified"),
        values.ContainsKey("--writes-disabled")
    );
    object output;
    var exitCode = 0;
    if (command == "inventory")
    {
        var report = await runner.InventoryAsync(decisions, ct);
        var state = await repository.GetStateAsync(ct);
        output = new
        {
            database = databaseName,
            report,
            migrationState = state?.ToString(),
        };
        exitCode = report.ReadyToApply ? 0 : 2;
    }
    else if (command == "apply")
    {
        var result = await runner.ApplyAsync(decisions, options, ct);
        output = new { database = databaseName, result };
        exitCode = result.Status == "ready" ? 0 : 2;
    }
    else if (command == "verify")
    {
        var report = await runner.InventoryAsync(null, ct);
        if (
            !report.ReadyToApply
            || report.Projects.Any(project =>
                project.SchemaVersion != 1 || project.MigrationState != "ready"
            )
        )
            throw new MigrationBlockedException("Projects are not all verified ready.");
        await runner.VerifyBackfillAsync(report.Plans, ct);
        await runner.VerifyIndexesAsync(true, ct);
        var state = await repository.GetStateAsync(ct);
        if (
            state is null
            || state.GetValue("status", "").AsString != "ready"
            || state.Contains("leaseToken")
        )
            throw new MigrationBlockedException(
                "The migration is not complete or still has an owner."
            );
        output = new
        {
            database = databaseName,
            status = "ready",
            report,
        };
    }
    else if (command == "release-lock")
    {
        if (
            !options.WritersStopped
            || !options.BackupVerified
            || !options.WritesDisabled
            || !values.TryGetValue("--expected-token", out var token)
        )
            throw new MigrationBlockedException(
                "Releasing a stale lock requires offline acknowledgements and the exact expected token."
            );
        await repository.ReleaseStaleLockAsync(token, ct);
        output = new
        {
            status = "blocked",
            message = "Lock released. Inspect and resume; workspace writes remain blocked.",
        };
    }
    else
        throw new MigrationBlockedException("Unknown command.");
    var json = JsonSerializer.Serialize(output, MigrationRepository.JsonOptions);
    if (values.TryGetValue("--output", out var path))
        await File.WriteAllTextAsync(path, json + Environment.NewLine, ct);
    else
        Console.WriteLine(json);
    return exitCode;
}
catch (MigrationBlockedException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(
        "Migration interrupted. Keep writes disabled and resume the established mapping."
    );
    return 3;
}
catch (Exception error)
    when (error is MongoException or JsonException or IOException or FormatException)
{
    Console.Error.WriteLine(
        $"Migration stopped ({error.GetType().Name}). Keep writes disabled; inspect database/index state and the recorded journal before retrying."
    );
    return 3;
}
