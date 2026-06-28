using System.Text.Json;

namespace Pusharoo.EventRelay.Models;

public sealed record ObservedNeoEvent(
    string Id,
    string Network,
    uint BlockIndex,
    string TransactionHash,
    string ContractHash,
    string EventName,
    JsonElement State,
    DateTime ObservedAt);
