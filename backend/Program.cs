using backend.Options;
using backend.Repositories;
using backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection(MongoDbOptions.SectionName));
builder.Services.AddSingleton<MongoDbContext>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IArtifactRepository, ArtifactRepository>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ArtifactService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new BsonValueJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new BsonDocumentJsonConverter());
    });
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.MapControllers();

app.Run();
