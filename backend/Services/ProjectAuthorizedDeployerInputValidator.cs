using backend.Models;

namespace backend.Services;

public sealed class ProjectAuthorizedDeployerInputValidator(NeoWalletAddressValidator addressValidator)
{
    public const int MaxAuthorizedDeployers = 25;
    private static readonly HashSet<string> SupportedNetworks = new(StringComparer.Ordinal)
    {
        "neo3:mainnet",
        "neo3:testnet"
    };

    public ProjectAuthorizedDeployerInputValidationResult ValidateAdd(AddProjectAuthorizedDeployerRequest request)
        => ValidateWithNetworks(request.WalletAddress, request.AllowedNetworks);

    public ProjectAuthorizedDeployerInputValidationResult ValidateUpdate(
        string walletAddress,
        UpdateProjectAuthorizedDeployerRequest request)
        => ValidateWithNetworks(walletAddress, request.AllowedNetworks);

    public ProjectAuthorizedDeployerInputValidationResult ValidateRemove(string walletAddress)
    {
        var address = addressValidator.Validate(walletAddress);
        return address.IsValid
            ? new ProjectAuthorizedDeployerInputValidationResult(true, string.Empty, address.WalletAddress, address.ScriptHash, [])
            : Fail(address.Error);
    }

    private ProjectAuthorizedDeployerInputValidationResult ValidateWithNetworks(
        string walletAddress,
        IReadOnlyList<string>? requestedNetworks)
    {
        var address = addressValidator.Validate(walletAddress);
        if (!address.IsValid)
        {
            return Fail(address.Error);
        }

        if (requestedNetworks is null || requestedNetworks.Count is 0 or > 2)
        {
            return Fail("Choose at least one supported deployment network.");
        }

        if (requestedNetworks.Any(network => string.IsNullOrWhiteSpace(network) || network.Trim().Length > 32))
        {
            return Fail("One or more deployment networks are invalid.");
        }

        var networks = requestedNetworks
            .Select(network => network.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(network => network, StringComparer.Ordinal)
            .ToList();
        if (networks.Count == 0 || networks.Any(network => !SupportedNetworks.Contains(network)))
        {
            return Fail("Allowed networks must be Neo N3 TestNet, MainNet, or both.");
        }

        return new ProjectAuthorizedDeployerInputValidationResult(
            true,
            string.Empty,
            address.WalletAddress,
            address.ScriptHash,
            networks);
    }

    private static ProjectAuthorizedDeployerInputValidationResult Fail(string error)
        => new(false, error, string.Empty, string.Empty, []);
}

public sealed record ProjectAuthorizedDeployerInputValidationResult(
    bool IsValid,
    string Error,
    string WalletAddress,
    string ScriptHash,
    List<string> AllowedNetworks);
