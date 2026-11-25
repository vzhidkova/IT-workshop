using System.Reflection;
using LogHive;
using Npgsql;
using LogHive.Services;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("PostgreSql")
        ?? throw new InvalidOperationException(
            "Не найдена строка подключения PostgreSql");

    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddControllers();

builder.Services.AddHttpClient<NotificationService>();

builder.Services.AddHostedService<NotificationBackgroundService>();

var jwtSettings = builder.Configuration.GetSection("Jwt");

var secretKey = jwtSettings["SecretKey"]
    ?? throw new InvalidOperationException("Не задан Jwt:SecretKey");

var issuer = jwtSettings["Issuer"]
    ?? throw new InvalidOperationException("Не задан Jwt:Issuer");

var audience = jwtSettings["Audience"]
    ?? throw new InvalidOperationException("Не задан Jwt:Audience");

if (Encoding.UTF8.GetByteCount(secretKey) < 32)
{
    throw new InvalidOperationException(
        "Секретный ключ JWT должен содержать минимум 32 байта");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,

            ValidateAudience = true,
            ValidAudience = audience,

            ValidateLifetime = true,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(secretKey)),

            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);

    options.IncludeXmlComments(xmlPath);

    // Настройка JWT в Swagger
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Введите JWT-токен"
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});


var app = builder.Build();

await RootUserSeeder.SeedAsync(
    app.Configuration,
    app.Services.GetRequiredService<NpgsqlDataSource>()
);

app.MapGet("/db-test", async (NpgsqlDataSource dataSource) =>
{
    await using var connection =
        await dataSource.OpenConnectionAsync();

    await using var command = new NpgsqlCommand(
        "SELECT current_database(), current_user",
        connection);

    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();

    return Results.Ok(new
    {
        Status = "Подключение успешно"
    });
});

// Swagger UI
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
