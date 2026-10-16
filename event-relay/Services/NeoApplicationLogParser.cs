using System.Text.Json;
using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public static class NeoApplicationLogParser
{
    // Transaction subscriptions support Application only. System/block and Verification triggers are excluded.
    public static IReadOnlyList<ObservedNeoEvent> Extract(
        JsonElement log,
        string network,
        string transactionHash,
        NeoBlock block
    )
    {
        if (!IsHash(block.Hash, 32))
            throw new FormatException("Invalid block hash.");

        return Parse(log, transactionHash)
            .Select(notification => new ObservedNeoEvent(
                NeoEventIdentity.Create(
                    network,
                    block.Index,
                    transactionHash,
                    notification.Ordinal
                ),
                network,
                block.Index,
                transactionHash.ToLowerInvariant(),
                notification.ContractHash,
                notification.EventName,
                notification.State,
                DateTime.UtcNow,
                block.Hash.ToLowerInvariant(),
                block.Timestamp,
                notification.ExecutionIndex,
                notification.NotificationIndex
            ))
            .ToArray();
    }

    public static void Validate(JsonElement log, string transactionHash) =>
        _ = Parse(log, transactionHash);

    private sealed record ParsedNotification(
        string ContractHash,
        string EventName,
        JsonElement State,
        int ExecutionIndex,
        int NotificationIndex,
        int Ordinal
    );

    private static IReadOnlyList<ParsedNotification> Parse(JsonElement log, string transactionHash)
    {
        if (
            !string.Equals(
                RequiredString(log, "txid"),
                transactionHash,
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new FormatException(
                "Application log txid does not match the requested transaction."
            );

        var executions = RequiredArray(log, "executions");

        if (executions.GetArrayLength() == 0)
            throw new FormatException("Application log has no executions.");

        var events = new List<ParsedNotification>();
        var executionIndex = -1;
        var eventIndex = 0;
        var hasApplication = false;

        foreach (var execution in executions.EnumerateArray())
        {
            executionIndex++;
            var notificationIndex = -1;
            var trigger = RequiredString(execution, "trigger");

            if (trigger is not ("Application" or "Verification" or "OnPersist" or "PostPersist"))
                throw new FormatException($"Unknown execution trigger: {trigger}");

            hasApplication |= trigger == "Application";
            var vmState = RequiredString(execution, "vmstate");

            if (vmState is not ("HALT" or "FAULT"))
                throw new FormatException($"Unknown execution state: {vmState}");

            var notifications = RequiredArray(execution, "notifications");

            foreach (var notification in notifications.EnumerateArray())
            {
                // Preserve the original flattened notification position, including excluded executions.
                var index = eventIndex++;
                notificationIndex++;

                if (trigger != "Application" || vmState != "HALT")
                    continue;

                var contract = RequiredString(notification, "contract", "scripthash");

                if (!IsHash(contract, 20))
                    throw new FormatException("Invalid notification contract hash.");

                var name = RequiredString(notification, "eventname", "eventName");
                var state = RequiredObject(notification, "state");
                var stateType = RequiredString(state, "type");

                if (
                    stateType != "Any"
                    && (
                        !state.TryGetProperty("value", out var value)
                        || value.ValueKind == JsonValueKind.Null
                    )
                )
                    throw new FormatException("Notification state is missing its value.");

                if (stateType is "Array" or "Struct" or "Map")
                    _ = RequiredArray(state, "value");

                events.Add(
                    new ParsedNotification(
                        contract.ToLowerInvariant(),
                        name,
                        state.Clone(),
                        executionIndex,
                        notificationIndex,
                        index
                    )
                );
            }
        }

        if (!hasApplication)
            throw new FormatException("Transaction log is missing its Application execution.");

        return events;
    }

    public static bool IsHash(string value, int bytes) =>
        value.Length == 2 + bytes * 2
        && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && value.AsSpan(2).ToArray().All(Uri.IsHexDigit);

    public static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (
            parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object
        )
            throw new FormatException($"Missing or invalid object: {name}");

        return value;
    }

    public static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (
            parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array
        )
            throw new FormatException($"Missing or invalid array: {name}");

        return value;
    }

    public static string RequiredString(JsonElement parent, string name, string? alias = null)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Expected object containing {name}.");

        if (
            !parent.TryGetProperty(name, out var value)
            && (alias is null || !parent.TryGetProperty(alias, out value))
        )
            throw new FormatException($"Missing string: {name}");

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"Invalid string: {name}");

        return value.GetString()!;
    }
}
