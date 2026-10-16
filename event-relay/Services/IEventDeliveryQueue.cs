using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public interface IEventDeliveryQueue
{
    Task QueueAsync(
        WebhookSubscriptionDocument subscription,
        ObservedNeoEvent observedEvent,
        CancellationToken cancellationToken
    );
}
