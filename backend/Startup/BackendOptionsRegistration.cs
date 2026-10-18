using backend.Options;

namespace backend.Startup;

internal static class BackendOptionsRegistration
{
    internal static void AddBackendOptions(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.Configure<MongoDbOptions>(configuration.GetSection(MongoDbOptions.SectionName));

        services.Configure<NeoRpcOptions>(configuration.GetSection(NeoRpcOptions.SectionName));

        services.Configure<WalletSignatureOptions>(
            configuration.GetSection(WalletSignatureOptions.SectionName)
        );

        services.Configure<WalletAuthOptions>(
            configuration.GetSection(WalletAuthOptions.SectionName)
        );

        services.Configure<FaucetOptions>(configuration.GetSection(FaucetOptions.SectionName));

        services.Configure<FaucetRelayerOptions>(
            configuration.GetSection(FaucetRelayerOptions.SectionName)
        );

        services.Configure<RelayGatewayOptions>(
            configuration.GetSection(RelayGatewayOptions.SectionName)
        );
    }
}
