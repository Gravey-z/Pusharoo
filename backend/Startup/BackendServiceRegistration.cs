using System.Net;
using System.Threading.RateLimiting;
using backend.Options;
using backend.Repositories;
using backend.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

namespace backend.Startup;

public static class BackendServiceRegistration
{
    public static WebApplicationBuilder AddPusharooBackend(this WebApplicationBuilder builder)
    {
        ConfigureRequestLimits(builder);
        var services = builder.Services;
        var configuration = builder.Configuration;
        services.AddBackendOptions(configuration);
        AddPersistenceAndDeployments(services);
        AddFaucet(services, configuration);
        var authConfiguration =
            configuration.GetSection(WalletAuthOptions.SectionName).Get<WalletAuthOptions>()
            ?? new();

        AddWalletSecurity(services, authConfiguration);
        AddProjectServices(services);
        AddHttpServices(services, configuration, authConfiguration);
        return builder;
    }

    public static string[] GetFrontendCorsOrigins(this IConfiguration configuration)
    {
        return configuration
                .GetSection("Cors:AllowedOrigins")
                .Get<string[]>()
                ?.Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            ?? [];
    }

    private static void ConfigureRequestLimits(WebApplicationBuilder builder)
    {
        const long maxUploadRequestBytes = 10 * 1024 * 1024;

        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = maxUploadRequestBytes
        );

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxUploadRequestBytes;
            options.ValueLengthLimit = 8 * 1024;
            options.MultipartHeadersLengthLimit = 16 * 1024;
        });
    }

    private static void AddPersistenceAndDeployments(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MongoDbContext>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IArtifactRepository, ArtifactRepository>();
        services.AddScoped<IDeploymentRepository, DeploymentRepository>();
        services.AddScoped<ProjectService>();
        services.AddScoped<ArtifactService>();
        services.AddSingleton<ArtifactValidator>();
        services.AddScoped<DeploymentService>();
        services.AddScoped<DeploymentWorkflowService>();
        services.AddScoped<DeploymentAttemptValidator>();
        services.AddScoped<DeploymentLifecycleService>();
        services.AddScoped<DeploymentRecoveryService>();
        services.AddHttpClient<NeoRpcClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
    }

    private static void AddFaucet(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<FaucetRpcService>();
        services.AddSingleton<FaucetClientIpResolver>();
        services.AddScoped<FaucetService>();

        if (configuration.GetValue<bool>($"{FaucetRelayerOptions.SectionName}:Enabled"))
        {
            services.AddHostedService<FaucetRelayerWorker>();
        }
    }

    private static void AddWalletSecurity(
        IServiceCollection services,
        WalletAuthOptions authConfiguration
    )
    {
        services.AddScoped<NeoDeploymentVerificationService>();
        services.AddSingleton<NeoWalletSignatureVerifier>();
        services.AddSingleton<NeoWalletAddressValidator>();
        services.AddSingleton<DeploymentDataService>();
        services.AddSingleton<WalletSignatureRequestValidator>();
        services.AddSingleton<ProjectOwnershipService>();
        services.AddSingleton<WalletAuthService>();
        services.AddSingleton<WalletAuthCookies>();
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentWalletSessionAccessor>();

        services
            .AddAuthentication(WalletSessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, WalletSessionAuthenticationHandler>(
                WalletSessionAuthenticationHandler.SchemeName,
                _ => { }
            );

        services.AddAuthorization();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.Name = "Pusharoo.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy =
                authConfiguration.AllowInsecureLocalhost
                && Uri.TryCreate(authConfiguration.PublicOrigin, UriKind.Absolute, out var origin)
                && origin.Scheme == Uri.UriSchemeHttp
                && origin.IsLoopback
                    ? CookieSecurePolicy.None
                    : CookieSecurePolicy.Always;
        });

        if (!string.IsNullOrWhiteSpace(authConfiguration.DataProtectionKeyRingPath))
        {
            Directory.CreateDirectory(authConfiguration.DataProtectionKeyRingPath);
            services
                .AddDataProtection()
                .SetApplicationName("Pusharoo.Api")
                .PersistKeysToFileSystem(
                    new DirectoryInfo(authConfiguration.DataProtectionKeyRingPath)
                );
        }
    }

    private static void AddProjectServices(IServiceCollection services)
    {
        services.AddScoped<ProjectAuthorizedDeployerInputValidator>();
        services.AddScoped<ProjectAuthorizedDeployerService>();
        services.AddScoped<ProjectAuthorizationService>();

        services
            .AddHttpClient<RelayGatewayService>(client => client.Timeout = TimeSpan.FromSeconds(25))
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }
            );

        services.AddScoped<DeploymentAuthorizationService>();
        services.AddHostedService<ProjectOwnershipMigrationService>();
    }

    private static void AddHttpServices(
        IServiceCollection services,
        IConfiguration configuration,
        WalletAuthOptions authConfiguration
    )
    {
        var allowedCorsOrigins = configuration.GetFrontendCorsOrigins();
        if (allowedCorsOrigins.Length > 0)
        {
            services.AddCors(options =>
                options.AddPolicy(
                    "Frontend",
                    policy =>
                    {
                        policy.WithOrigins(allowedCorsOrigins).AllowAnyHeader().AllowAnyMethod();
                    }
                )
            );
        }

        services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new BsonValueJsonConverter());
                options.JsonSerializerOptions.Converters.Add(new BsonDocumentJsonConverter());
            });

        services.AddOpenApi();
        AddRateLimiting(services);
        ConfigureForwardedHeaders(services, configuration, authConfiguration);
    }

    private static void AddRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(
                "WalletAuthChallenge",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 8,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }
                    )
            );
            options.AddPolicy(
                "WalletAuthLogin",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 16,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }
                    )
            );
            options.AddPolicy(
                "FaucetIp",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context
                            .RequestServices.GetRequiredService<FaucetClientIpResolver>()
                            .Resolve(context)
                            ?.ToString()
                            ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 12,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }
                    )
            );
        });
    }

    private static void ConfigureForwardedHeaders(
        IServiceCollection services,
        IConfiguration configuration,
        WalletAuthOptions authConfiguration
    )
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            foreach (
                var address in configuration
                    .GetSection("Faucet:TrustedProxyAddresses")
                    .Get<string[]>()
                    ?? []
            )
            {
                if (IPAddress.TryParse(address, out var parsed))
                    options.KnownProxies.Add(parsed);
            }

            foreach (
                var range in configuration.GetSection("Faucet:TrustedProxyNetworks").Get<string[]>()
                    ?? []
            )
            {
                if (!System.Net.IPNetwork.TryParse(range, out var parsed))
                    continue;

                options.KnownIPNetworks.Add(parsed);

                if (
                    parsed.BaseAddress.AddressFamily
                    == System.Net.Sockets.AddressFamily.InterNetwork
                )
                    options.KnownIPNetworks.Add(
                        new System.Net.IPNetwork(
                            parsed.BaseAddress.MapToIPv6(),
                            parsed.PrefixLength + 96
                        )
                    );
            }

            foreach (var address in authConfiguration.TrustedProxyAddresses)
            {
                if (IPAddress.TryParse(address, out var parsed))
                    options.KnownProxies.Add(parsed);
            }
        });
    }
}
