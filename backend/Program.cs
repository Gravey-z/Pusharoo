using backend.Options;
using backend.Repositories;
using backend.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

const long maxUploadRequestBytes = 10 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxUploadRequestBytes);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadRequestBytes;
    options.ValueLengthLimit = 8 * 1024;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection(MongoDbOptions.SectionName));
builder.Services.Configure<NeoRpcOptions>(builder.Configuration.GetSection(NeoRpcOptions.SectionName));
builder.Services.Configure<WalletSignatureOptions>(builder.Configuration.GetSection(WalletSignatureOptions.SectionName));
builder.Services.Configure<FaucetOptions>(builder.Configuration.GetSection(FaucetOptions.SectionName));
builder.Services.Configure<FaucetRelayerOptions>(builder.Configuration.GetSection(FaucetRelayerOptions.SectionName));
builder.Services.AddSingleton<MongoDbContext>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IArtifactRepository, ArtifactRepository>();
builder.Services.AddScoped<IDeploymentRepository, DeploymentRepository>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ArtifactService>();
builder.Services.AddSingleton<ArtifactValidator>();
builder.Services.AddScoped<DeploymentService>();
builder.Services.AddScoped<DeploymentWorkflowService>();
builder.Services.AddHttpClient<NeoRpcClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<FaucetRpcService>();
builder.Services.AddScoped<FaucetService>();
if (builder.Configuration.GetValue<bool>($"{FaucetRelayerOptions.SectionName}:Enabled"))
{
    builder.Services.AddHostedService<FaucetRelayerWorker>();
}
builder.Services.AddScoped<NeoDeploymentVerificationService>();
builder.Services.AddSingleton<NeoWalletSignatureVerifier>();
builder.Services.AddSingleton<NeoWalletAddressValidator>();
builder.Services.AddSingleton<DeploymentDataService>();
builder.Services.AddSingleton<WalletSignatureRequestValidator>();
builder.Services.AddSingleton<ProjectCreationSignatureValidator>();
builder.Services.AddSingleton<ProjectManagementSignatureValidator>();
builder.Services.AddSingleton<ProjectOwnershipService>();
builder.Services.AddSingleton<SignatureNonceService>();
builder.Services.AddScoped<ProjectAuthorizedDeployerInputValidator>();
builder.Services.AddScoped<ProjectAuthorizedDeployerSignatureValidator>();
builder.Services.AddScoped<ProjectAuthorizedDeployerService>();
builder.Services.AddScoped<ProjectAuthorizationService>();
builder.Services.AddScoped<DeploymentAuthorizationService>();
builder.Services.AddHostedService<ProjectOwnershipMigrationService>();
var allowedCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?.Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out _))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray() ?? [];
if (allowedCorsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(allowedCorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    }));
}
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new BsonValueJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new BsonDocumentJsonConverter());
    });
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("FaucetIp", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    foreach (var address in builder.Configuration.GetSection("Faucet:TrustedProxyAddresses").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(address, out var parsed)) options.KnownProxies.Add(parsed);
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (builder.Configuration.GetValue<bool>("Https:RedirectEnabled"))
{
    app.UseHttpsRedirection();
}
app.UseForwardedHeaders();
if (allowedCorsOrigins.Length > 0)
{
    app.UseCors("Frontend");
}
app.UseRateLimiter();
app.MapControllers();

app.Run();
