using System.Text.Json;
using System.Text.Json.Nodes;
using Pusharoo.EventRelay.Models;
namespace Pusharoo.EventRelay.Services;

public static class WebhookPayloadContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static bool IsTransportHeader(string name) => new[] { "X-Pusharoo-Delivery", "X-Pusharoo-Event", "X-Pusharoo-Signature", "X-Pusharoo-Event-Id", "X-Pusharoo-Payload-Version" }.Contains(name,StringComparer.OrdinalIgnoreCase);
    public static bool IsSupported(int version) => version == 2;
    public static string Create(string deliveryId, WebhookSubscriptionDocument subscription, ObservedNeoEvent observed)
    {
        if (!IsSupported(subscription.PayloadVersion)) throw new InvalidOperationException("Unsupported webhook payload version.");
        return JsonSerializer.Serialize(new PayloadV2(2,observed.Id,deliveryId,subscription.Id,observed.Network,observed.BlockIndex,
            observed.BlockHash,observed.TransactionHash,observed.ContractHash,observed.ExecutionIndex,observed.NotificationIndex,
            observed.EventName,observed.State,observed.BlockTimestamp,observed.ObservedAt,"automatic",false),JsonOptions);
    }
    public static string CreateTest(string deliveryId, string eventId, WebhookSubscriptionDocument subscription)
    {
        var state=JsonSerializer.SerializeToElement(new {test=true,message="Pusharoo test delivery"});
        var name=subscription.EventName??"Pusharoo.Test"; var now=DateTime.UtcNow;
        if (subscription.PayloadVersion != 2) throw new InvalidOperationException("Unsupported webhook payload version.");
        // Tests are synthetic: never invent block metadata or pretend their event IDs identify chain events.
        return JsonSerializer.Serialize(new PayloadV2(2,eventId,deliveryId,subscription.Id,subscription.Network,null,null,null,
            subscription.ContractHash,null,null,name,state,null,now,"test",true),JsonOptions);
    }
    public static int Version(string payload)
    {
        using var document=JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind!=JsonValueKind.Object) throw new FormatException("Webhook payload must be an object.");
        if (!document.RootElement.TryGetProperty("payloadVersion",out var version)) throw new FormatException("Webhook payloadVersion 2 is required.");
        if (version.ValueKind!=JsonValueKind.Number || !version.TryGetInt32(out var number) || !IsSupported(number))
            throw new FormatException("Unsupported stored webhook payload version.");
        return number;
    }
    public static string ForDelivery(string payload, string deliveryId, string eventId, bool manual=false)
    {
        _ = Version(payload);
        var body=JsonNode.Parse(payload) as JsonObject ?? throw new FormatException("Invalid stored webhook payload.");
        if (body["eventId"]?.GetValue<string>()!=eventId)
            throw new FormatException("Stored webhook event identity does not match the delivery.");
        var changed=false;
        if (body["deliveryId"]?.GetValue<string>()!=deliveryId) { body["deliveryId"]=deliveryId; changed=true; }
        if (manual) { body["trigger"]="manual"; changed=true; }
        // Retries preserve exact original bytes; manual redelivery changes the delivery ID.
        return changed ? body.ToJsonString(JsonOptions) : payload;
    }
    private sealed record PayloadV2(int PayloadVersion,string EventId,string DeliveryId,string SubscriptionId,string Network,uint? BlockIndex,
        string? BlockHash,string? TransactionHash,string ContractHash,int? ExecutionIndex,int? NotificationIndex,string EventName,
        JsonElement State,DateTimeOffset? BlockTimestamp,DateTime ObservedAt,string Trigger,bool IsTest);
}
