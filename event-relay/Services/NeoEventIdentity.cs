using System.Globalization;

namespace Pusharoo.EventRelay.Services;

public static class NeoEventIdentity
{
    public static string Create(
        string network,
        uint blockIndex,
        string transactionHash,
        int originalNotificationOrdinal
    )
    {
        if (
            network is not ("neo3:mainnet" or "neo3:testnet")
            || !NeoApplicationLogParser.IsHash(transactionHash, 32)
            || originalNotificationOrdinal < 0
        )
            throw new ArgumentException("Invalid chain event identity input.");

        return $"{network}:{blockIndex.ToString(CultureInfo.InvariantCulture)}:{transactionHash.ToLowerInvariant()}:{originalNotificationOrdinal.ToString(CultureInfo.InvariantCulture)}";
    }
}
