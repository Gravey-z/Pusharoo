using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using Pusharoo.EventRelay.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection(MongoDbOptions.SectionName));
builder.Services.Configure<NeoRpcOptions>(builder.Configuration.GetSection(NeoRpcOptions.SectionName));
builder.Services.Configure<EventRelayOptions>(builder.Configuration.GetSection(EventRelayOptions.SectionName));

builder.Services.AddSingleton<MongoDbContext>();
builder.Services.AddScoped<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();
builder.Services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
builder.Services.AddScoped<IEventCheckpointRepository, EventCheckpointRepository>();
builder.Services.AddHttpClient<NeoRpcClient>();
builder.Services.AddHttpClient<WebhookDeliveryService>();
builder.Services.AddHostedService<NeoEventMonitorService>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();
