using System.Globalization;
using System.Text.Json;
using MongoDB.Driver;
using Pusharoo.RetentionInventory;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine(
        "Read-only diagnostic: --database NAME [--take 1..100] [--after PROJECT_ID] [--at UTC_ISO_DATE] [--output NEW_FILE]"
    );
    Console.WriteLine(
        "Connection environment: PUSHAROO_RETENTION_INVENTORY_CONNECTION. This command cannot delete or change records/indexes."
    );
    return 0;
}

try
{
    var values = new Dictionary<string, string>();
    var allowed = new[] { "--database", "--take", "--after", "--at", "--output" };
    for (var i = 0; i < args.Length; i += 2)
    {
        if (
            !allowed.Contains(args[i])
            || i + 1 >= args.Length
            || !values.TryAdd(args[i], args[i + 1])
        )
            throw new ArgumentException("Invalid, missing or repeated inventory argument.");
    }
    if (
        !values.TryGetValue("--database", out var databaseName)
        || string.IsNullOrWhiteSpace(databaseName)
    )
        throw new ArgumentException("An explicit database name is required.");
    var connection = Environment.GetEnvironmentVariable("PUSHAROO_RETENTION_INVENTORY_CONNECTION");
    if (string.IsNullOrWhiteSpace(connection))
        throw new ArgumentException(
            "Set PUSHAROO_RETENTION_INVENTORY_CONNECTION outside source and reports."
        );
    var take = 100;
    if (
        values.TryGetValue("--take", out var count)
        && (!int.TryParse(count, out take) || take is < 1 or > 100)
    )
        throw new ArgumentException("Take must be between 1 and 100.");
    TimeProvider clock = TimeProvider.System;
    if (values.TryGetValue("--at", out var timestamp))
    {
        if (
            !(timestamp.EndsWith('Z') || timestamp.EndsWith("+00:00", StringComparison.Ordinal))
            || !DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var at
            )
            || at.Offset != TimeSpan.Zero
        )
            throw new ArgumentException("At must be an explicit UTC ISO date with Z or +00:00.");
        clock = new InventoryClock(at);
    }
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    var settings = MongoClientSettings.FromConnectionString(connection);
    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
    var reader = new InventoryReader(new MongoClient(settings).GetDatabase(databaseName), clock);
    var report = await reader.ReadAsync(
        take,
        values.GetValueOrDefault("--after"),
        cancellation.Token
    );
    var json = JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }
    );
    if (values.TryGetValue("--output", out var output))
    {
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(json.AsMemory(), cancellation.Token);
    }
    else
        Console.WriteLine(json);
    return 0;
}
catch (ArgumentException)
{
    Console.Error.WriteLine(
        "Invalid inventory arguments or connection configuration. Use --help; credentials are suppressed."
    );
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Inventory cancelled; no database changes performed.");
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(
        $"Inventory failed ({error.GetType().Name}); no successful report or database changes. Check database access, project identities and the output path."
    );
    return 1;
}

sealed class InventoryClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
