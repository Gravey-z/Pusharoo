using backend.Models;

namespace backend.Services;

public sealed class ProjectCollaboratorInputValidator(NeoWalletAddressValidator addressValidator)
{
    public const int MaxCollaborators = 25;
    private static readonly HashSet<string> SupportedNetworks = new(StringComparer.Ordinal)
    {
        "neo3:mainnet",
        "neo3:testnet"
    };

    public ProjectCollaboratorInputValidationResult ValidateAdd(AddProjectCollaboratorRequest request)
    {
        var common = ValidateCommon(request.WalletAddress, request.Role, request.AllowedNetworks, request.ExpectedGrantRevision);
        return !common.IsValid || request.ExpectedGrantRevision == 0
            ? common
            : Fail("Expected grant revision must be 0 when adding a collaborator.");
    }

    public ProjectCollaboratorInputValidationResult ValidateUpdate(
        string walletAddress,
        UpdateProjectCollaboratorRequest request)
    {
        var common = ValidateCommon(walletAddress, request.Role, request.AllowedNetworks, request.ExpectedGrantRevision);
        return !common.IsValid || request.ExpectedGrantRevision > 0
            ? common
            : Fail("Expected grant revision must be a positive integer.");
    }

    public ProjectCollaboratorInputValidationResult ValidateRemove(
        string walletAddress,
        RemoveProjectCollaboratorRequest request)
    {
        var address = addressValidator.Validate(walletAddress);
        if (!address.IsValid)
        {
            return Fail(address.Error);
        }

        return request.ExpectedGrantRevision > 0
            ? new ProjectCollaboratorInputValidationResult(true, string.Empty, address.WalletAddress, address.ScriptHash, "deployer", [])
            : Fail("Expected grant revision must be a positive integer.");
    }

    private ProjectCollaboratorInputValidationResult ValidateCommon(
        string walletAddress,
        string role,
        IReadOnlyList<string>? requestedNetworks,
        long expectedGrantRevision)
    {
        var address = addressValidator.Validate(walletAddress);
        if (!address.IsValid)
        {
            return Fail(address.Error);
        }

        if (!string.Equals(role?.Trim(), "deployer", StringComparison.Ordinal))
        {
            return Fail("Only the deployer collaborator role is supported.");
        }

        if (expectedGrantRevision < 0)
        {
            return Fail("Expected grant revision cannot be negative.");
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

        return new ProjectCollaboratorInputValidationResult(
            true,
            string.Empty,
            address.WalletAddress,
            address.ScriptHash,
            "deployer",
            networks);
    }

    private static ProjectCollaboratorInputValidationResult Fail(string error)
        => new(false, error, string.Empty, string.Empty, string.Empty, []);
}

public sealed record ProjectCollaboratorInputValidationResult(
    bool IsValid,
    string Error,
    string WalletAddress,
    string ScriptHash,
    string Role,
    List<string> AllowedNetworks);
