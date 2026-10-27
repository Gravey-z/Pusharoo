using System.Text.Json;
using backend.Models;

namespace backend.Services;

public sealed partial class NeoDeploymentVerificationService
{
    public async Task<NeoDeploymentVerificationResult> VerifyRecoverySignerAsync(
        string network,
        string transactionId,
        string signerScriptHash,
        CancellationToken cancellationToken
    )
    {
        var key = ToConfigurationNetworkKey(network);
        if (
            key is null
            || !options.Networks.TryGetValue(key, out var configured)
            || string.IsNullOrWhiteSpace(configured.Endpoint)
        )
            return Fail("No Neo RPC endpoint is configured for this network.");
        try
        {
            var transaction = await neoRpc.SendAsync(
                configured.Endpoint,
                "getrawtransaction",
                [transactionId],
                cancellationToken
            );
            return HasPrimarySigner(transaction, signerScriptHash)
                ? NeoDeploymentVerificationResult.Valid
                : Fail("The recovery wallet must be the deployment transaction's first signer.");
        }
        catch (Exception error)
            when (error is HttpRequestException or InvalidOperationException or JsonException)
        {
            return Fail(
                "Could not verify the recovery signer against Neo RPC. Retry when the node is available."
            );
        }
    }
}
