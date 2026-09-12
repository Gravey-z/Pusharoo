using backend.Models;

namespace backend.Services;

/// <summary>
/// Describes deployment workflows that are safe to expose. Collaborator broadcasts
/// stay disabled until prepared intents can prove the submitted invocation used the
/// authorized artifact and target contract.
/// </summary>
public sealed class DeploymentCapabilityService
{
    public const string CollaboratorDeploymentUnavailableReason =
        "Collaborator deployment is being prepared and is not available yet. Pusharoo must verify a prepared transaction used the authorized artifact before it can safely enable collaborator broadcasts.";

    public const string UnboundRecoveryUnavailableReason =
        "Unbound transaction recovery is not available yet. Resume an authorized submitted attempt instead.";

    public DeploymentCapabilitiesResponse GetCapabilities()
        => new(
            CollaboratorDeploymentsEnabled: false,
            CollaboratorDeploymentUnavailableReason,
            UnboundRecoveryEnabled: false,
            UnboundRecoveryUnavailableReason);

    public DeploymentCapabilityResult CanStartDeployment(bool isOwner)
        => isOwner || GetCapabilities().CollaboratorDeploymentsEnabled
            ? DeploymentCapabilityResult.Available
            : new DeploymentCapabilityResult(false, CollaboratorDeploymentUnavailableReason);

    public DeploymentCapabilityResult CanRecoverUnboundTransaction()
        => GetCapabilities().UnboundRecoveryEnabled
            ? DeploymentCapabilityResult.Available
            : new DeploymentCapabilityResult(false, UnboundRecoveryUnavailableReason);
}

public sealed record DeploymentCapabilityResult(bool IsAvailable, string Reason)
{
    public static DeploymentCapabilityResult Available { get; } = new(true, string.Empty);
}
