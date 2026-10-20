using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using static Pusharoo.EventRelay.Services.WebhookSubscriptionValidator;

namespace Pusharoo.EventRelay.Services;

public sealed class SubscriptionManagementService(
    IWebhookSubscriptionRepository subscriptions,
    IWebhookDeliveryRepository deliveries,
    WebhookDeliveryService webhookDelivery,
    WebhookSubscriptionValidator validator,
    WebhookSecretProtector secretProtector,
    RelayEntitlementService entitlements,
    IOptions<NeoRpcOptions> neoRpcOptions,
    IOptions<EventRelayOptions> eventRelayOptions,
    TimeProvider? timeProvider = null,
    RelayPackageStore? packageStore = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<SubscriptionResponse>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        await entitlements.Lifecycle.EnsureAsync(projectId, cancellationToken);
        var items = await subscriptions.GetByProjectIdAsync(projectId, cancellationToken);
        var latestDeliveries = await deliveries.GetLatestBySubscriptionIdsAsync(
            items.Select(subscription => subscription.Id).ToArray(),
            cancellationToken
        );
        var response = items
            .Select(subscription =>
                ToResponse(subscription, latestDeliveries.GetValueOrDefault(subscription.Id))
            )
            .ToArray();

        return response;
    }

    public async Task<SubscriptionManagementResult<SubscriptionResponse>> CreateAsync(
        string projectId,
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken
    )
    {
        var entitlement = await entitlements.GetAsync(
            projectId,
            request.Network.Trim(),
            cancellationToken
        );

        if (
            entitlement.Status != "active"
            || entitlement.PeriodEndsAt <= clock.GetUtcNow().UtcDateTime
        )
        {
            return SubscriptionManagementResult<SubscriptionResponse>.Invalid(
                "Relay access is not active for this network."
            );
        }

        var activeSubscriptions = (
            await subscriptions.GetByProjectIdAsync(projectId, cancellationToken)
        ).Count(item =>
            item.IsEnabled && (entitlement.Plan == "paid" || item.Network == request.Network.Trim())
        );

        if (request.IsEnabled && activeSubscriptions >= entitlement.MaxActiveSubscriptions)
        {
            return SubscriptionManagementResult<SubscriptionResponse>.Invalid(
                "The Relay active-webhook limit has been reached."
            );
        }

        var validation = await validator.ValidateAsync(request, cancellationToken);

        if (validation is not null)
        {
            return SubscriptionManagementResult<SubscriptionResponse>.Invalid(validation);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var subscription = new WebhookSubscriptionDocument
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = projectId.Trim(),
            Name = request.Name.Trim(),
            ContractHash = NormalizeHash(request.ContractHash),
            Network = request.Network.Trim(),
            EventName = NormalizeOptional(request.EventName),
            WebhookUrl = request.WebhookUrl.Trim(),
            Secret = secretProtector.Protect(request.Secret),
            Headers = NormalizeHeaders(request.Headers),
            IsEnabled = request.IsEnabled,
            PayloadVersion = request.PayloadVersion,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt =
                string.Equals(request.Network, "neo3:testnet", StringComparison.Ordinal)
                && entitlement.Plan == "free_beta"
                    ? now.AddDays(
                        Math.Max(1, eventRelayOptions.Value.TestnetSubscriptionRetentionDays)
                    )
                    : null,
        };

        var admissionError = await entitlements.Lifecycle.MutateSubscriptionAsync(
            subscription,
            true,
            false,
            cancellationToken
        );
        if (admissionError is not null)
            return SubscriptionManagementResult<SubscriptionResponse>.Invalid(admissionError);

        return SubscriptionManagementResult<SubscriptionResponse>.Success(
            ToResponse(subscription, null)
        );
    }

    public async Task<SubscriptionUsageResponse> UsageAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var entitlement = await entitlements.GetAsync(
            projectId,
            neoRpcOptions.Value.Network,
            cancellationToken
        );
        var active = (await subscriptions.GetByProjectIdAsync(projectId, cancellationToken)).Count(
            x =>
                x.IsEnabled
                && (entitlement.Plan == "paid" || x.Network == neoRpcOptions.Value.Network)
        );

        return new SubscriptionUsageResponse(
            entitlement.Plan,
            entitlement.Status,
            entitlement.PeriodStart,
            entitlement.PeriodEndsAt,
            entitlement.GraceEndsAt,
            entitlement.MaxActiveSubscriptions,
            active,
            entitlement.MaxEvents,
            entitlement.EventsUsed,
            Math.Max(0, entitlement.MaxEvents - entitlement.EventsUsed),
            RelayPackageCompatibility.PackageId(entitlement),
            entitlement.PurchasedPeriodId,
            entitlement.PackageTerms,
            RelayPackageCompatibility.NeedsReconciliation(
                entitlement,
                clock.GetUtcNow().UtcDateTime
            ),
            packageStore is null
                ? []
                : (await packageStore.PeriodsAsync(projectId, cancellationToken))
                    .Where(x => x.StartsAt > clock.GetUtcNow().UtcDateTime)
                    .ToArray(),
            await entitlements.Lifecycle.TransitionsAsync(projectId, cancellationToken)
        );
    }

    public async Task<SubscriptionManagementResult<SubscriptionResponse>> UpdateAsync(
        string projectId,
        string subscriptionId,
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken
    )
    {
        var existing = await GetProjectSubscriptionAsync(
            projectId,
            subscriptionId,
            cancellationToken
        );

        if (existing is null)
        {
            return SubscriptionManagementResult<SubscriptionResponse>.NotFound();
        }

        var entitlement = await entitlements.GetAsync(
            projectId,
            request.Network.Trim(),
            cancellationToken
        );

        if (
            request.IsEnabled
            && (
                !existing.IsEnabled
                || !string.Equals(
                    existing.Network,
                    request.Network.Trim(),
                    StringComparison.Ordinal
                )
            )
        )
        {
            var activeSubscriptions = (
                await subscriptions.GetByProjectIdAsync(projectId, cancellationToken)
            ).Count(item =>
                item.Id != existing.Id
                && item.IsEnabled
                && (entitlement.Plan == "paid" || item.Network == request.Network.Trim())
            );

            if (
                entitlement.Status != "active"
                || entitlement.PeriodEndsAt <= clock.GetUtcNow().UtcDateTime
                || activeSubscriptions >= entitlement.MaxActiveSubscriptions
            )
            {
                return SubscriptionManagementResult<SubscriptionResponse>.Invalid(
                    "This network's relay allowance does not permit another active webhook."
                );
            }
        }

        var validation = await validator.ValidateAsync(request, cancellationToken);

        if (validation is not null)
        {
            return SubscriptionManagementResult<SubscriptionResponse>.Invalid(validation);
        }

        var updated = new WebhookSubscriptionDocument
        {
            Id = existing.Id,
            ProjectId = existing.ProjectId,
            Name = request.Name.Trim(),
            ContractHash = NormalizeHash(request.ContractHash),
            Network = request.Network.Trim(),
            EventName = NormalizeOptional(request.EventName),
            WebhookUrl = request.WebhookUrl.Trim(),
            Secret = string.IsNullOrWhiteSpace(request.Secret)
                ? existing.Secret
                : secretProtector.Protect(request.Secret),
            Headers = NormalizeHeaders(request.Headers),
            IsEnabled = request.IsEnabled,
            PayloadVersion = request.PayloadVersion,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
            ExpiresAt =
                string.Equals(request.Network, "neo3:testnet", StringComparison.Ordinal)
                && entitlement.Plan == "free_beta"
                    ? existing.ExpiresAt
                        ?? existing.CreatedAt.AddDays(
                            Math.Max(1, eventRelayOptions.Value.TestnetSubscriptionRetentionDays)
                        )
                    : null,
            AdmissionRevision = existing.AdmissionRevision,
        };

        var admissionError = await entitlements.Lifecycle.MutateSubscriptionAsync(
            updated,
            false,
            false,
            cancellationToken
        );
        if (admissionError is not null)
            return SubscriptionManagementResult<SubscriptionResponse>.Conflict(admissionError);

        return SubscriptionManagementResult<SubscriptionResponse>.Success(
            ToResponse(
                updated,
                await deliveries.GetLatestBySubscriptionAsync(updated.Id, cancellationToken)
            )
        );
    }

    public async Task<SubscriptionManagementResult<bool>> DeleteAsync(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var existing = await GetProjectSubscriptionAsync(
            projectId,
            subscriptionId,
            cancellationToken
        );

        if (existing is null)
        {
            return SubscriptionManagementResult<bool>.NotFound();
        }

        var admissionError = await entitlements.Lifecycle.MutateSubscriptionAsync(
            existing,
            false,
            true,
            cancellationToken
        );
        if (admissionError is not null)
            return SubscriptionManagementResult<bool>.Conflict(admissionError);
        await deliveries.DeleteBySubscriptionIdsAsync([subscriptionId], cancellationToken);

        return SubscriptionManagementResult<bool>.Success(true);
    }

    public async Task<
        SubscriptionManagementResult<IReadOnlyList<WebhookDeliveryDocument>>
    > GetDeliveriesAsync(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var existing = await GetProjectSubscriptionAsync(
            projectId,
            subscriptionId,
            cancellationToken
        );

        if (existing is null)
        {
            return SubscriptionManagementResult<IReadOnlyList<WebhookDeliveryDocument>>.NotFound();
        }

        var history = await deliveries.GetBySubscriptionAsync(subscriptionId, cancellationToken);
        var entitlement = await entitlements.GetAsync(
            projectId,
            existing.Network,
            cancellationToken
        );

        if (
            string.Equals(existing.Network, "neo3:testnet", StringComparison.Ordinal)
            || entitlement.Plan == "paid"
        )
        {
            history = history
                .Where(item => item.DeliveredAt >= clock.GetUtcNow().UtcDateTime.AddDays(-7))
                .ToList();
        }

        return SubscriptionManagementResult<IReadOnlyList<WebhookDeliveryDocument>>.Success(
            history
        );
    }

    public async Task<SubscriptionManagementResult<WebhookDeliveryDocument>> TestAsync(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var subscription = await GetProjectSubscriptionAsync(
            projectId,
            subscriptionId,
            cancellationToken
        );

        if (subscription is null)
            return SubscriptionManagementResult<WebhookDeliveryDocument>.NotFound();

        var delivery = await webhookDelivery.QueueTestAsync(subscription, cancellationToken);

        return SubscriptionManagementResult<WebhookDeliveryDocument>.Success(delivery);
    }

    public async Task<SubscriptionManagementResult<WebhookDeliveryDocument?>> RedeliverAsync(
        string projectId,
        string subscriptionId,
        string deliveryId,
        CancellationToken cancellationToken
    )
    {
        var subscription = await GetProjectSubscriptionAsync(
            projectId,
            subscriptionId,
            cancellationToken
        );
        var delivery = await deliveries.GetByIdAsync(deliveryId, cancellationToken);

        if (subscription is null || delivery?.SubscriptionId != subscriptionId)
            return SubscriptionManagementResult<WebhookDeliveryDocument?>.NotFound();

        if (delivery.Status is not ("succeeded" or "dead_letter" or "cancelled"))
            return SubscriptionManagementResult<WebhookDeliveryDocument?>.Conflict(
                "This delivery is still queued; wait for its outcome before redelivering."
            );

        if (string.IsNullOrWhiteSpace(delivery.PayloadJson))
            return SubscriptionManagementResult<WebhookDeliveryDocument?>.Gone(
                "Stored payload expired. Block replay only queues missing events; retained event identities skip this delivery."
            );

        await webhookDelivery.RedeliverAsync(subscription, delivery, cancellationToken);

        return SubscriptionManagementResult<WebhookDeliveryDocument?>.Success(
            await deliveries.GetLatestBySubscriptionAsync(subscriptionId, cancellationToken)
        );
    }

    private async Task<WebhookSubscriptionDocument?> GetProjectSubscriptionAsync(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var subscription = await subscriptions.GetByIdAsync(subscriptionId, cancellationToken);

        return
            subscription is not null
            && string.Equals(subscription.ProjectId, projectId, StringComparison.Ordinal)
            ? subscription
            : null;
    }

    private static SubscriptionResponse ToResponse(
        WebhookSubscriptionDocument subscription,
        WebhookDeliveryDocument? latestDelivery
    )
    {
        return new SubscriptionResponse(
            subscription.Id,
            subscription.ProjectId ?? string.Empty,
            subscription.Name,
            subscription.ContractHash,
            subscription.Network,
            subscription.EventName,
            subscription.WebhookUrl,
            subscription.Headers,
            subscription.IsEnabled,
            subscription.CreatedAt,
            subscription.UpdatedAt,
            latestDelivery,
            subscription.PayloadVersion,
            subscription.PauseReason
        );
    }

    public Task<RelayTransitionResponse> TransitionsAsync(string projectId, CancellationToken ct) =>
        entitlements.Lifecycle.TransitionsAsync(projectId, ct);

    public Task SetDowngradeSelectionAsync(
        string projectId,
        SetRelayDowngradeSelectionRequest request,
        CancellationToken ct
    ) => entitlements.Lifecycle.SetDowngradeSelectionAsync(projectId, request, ct);
}
