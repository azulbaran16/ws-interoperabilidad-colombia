using System.Net;
using System.Threading.RateLimiting;
using InteropGateway.Api.Configuration;
using InteropGateway.Api.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services
    .AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .ValidateDataAnnotations();

var gatewayOptions = builder.Configuration
    .GetSection(GatewayOptions.SectionName)
    .Get<GatewayOptions>() ?? new GatewayOptions();
gatewayOptions.Validate();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OAuthTokenProvider>();
builder.Services.AddScoped<ProxyDispatcher>();

builder.Services.AddHttpClient(ProxyDispatcher.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, gatewayOptions.RequestTimeoutSeconds / 2)),
        PooledConnectionLifetime = TimeSpan.FromSeconds(Math.Max(30, gatewayOptions.PooledConnectionLifetimeSeconds)),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = Math.Max(16, gatewayOptions.MaxConnectionsPerServer),
        EnableMultipleHttp2Connections = true,
        UseCookies = false
    });

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetTokenBucketLimiter("global", _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = gatewayOptions.RateLimiter.TokenLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = gatewayOptions.RateLimiter.QueueLimit,
            ReplenishmentPeriod = TimeSpan.FromSeconds(gatewayOptions.RateLimiter.ReplenishmentPeriodSeconds),
            TokensPerPeriod = gatewayOptions.RateLimiter.TokensPerPeriod,
            AutoReplenishment = true
        }));
});

var app = builder.Build();

app.UseRateLimiter();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(context.Request.Path.Value, "/", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var security = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
    var clientsConfigured = gatewayOptions.Clients.Length > 0;
    if (!security.RequireApiKey && !clientsConfigured)
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue(security.ApiKeyHeaderName, out var providedValues))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = $"Header {security.ApiKeyHeaderName} requerido." });
        return;
    }

    var provided = providedValues.ToString();
    if (string.IsNullOrWhiteSpace(provided))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = $"Header {security.ApiKeyHeaderName} vacío." });
        return;
    }

    if (clientsConfigured)
    {
        var client = gatewayOptions.Clients.FirstOrDefault(c =>
            string.Equals(c.InboundApiKey, provided, StringComparison.Ordinal));

        if (client is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "API key inválida para cliente." });
            return;
        }

        context.Items[ProxyDispatcher.SelectedClientIdContextKey] = client.ClientId;
        await next();
        return;
    }

    if (security.ApiKeys.Length == 0)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "No hay API keys configuradas para seguridad." });
        return;
    }

    var valid = security.ApiKeys.Any(k => string.Equals(k, provided, StringComparison.Ordinal));
    if (!valid)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "API key inválida." });
        return;
    }

    await next();
});

app.MapGet("/", () => Results.Ok(new
{
    service = "Interop Gateway Colombia",
    status = "running",
    version = "1.0.0"
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async (ProxyDispatcher dispatcher, CancellationToken ct) =>
{
    var readiness = await dispatcher.CheckReadinessAsync(ct);
    if (readiness.IsReady)
    {
        return Results.Ok(new { status = "ready", detail = readiness.Message });
    }

    return Results.Problem(
        title: "Gateway no listo",
        detail: readiness.Message,
        statusCode: StatusCodes.Status503ServiceUnavailable);
});

var methods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" };
app.MapMethods("/{**path}", methods, async (HttpContext context, ProxyDispatcher dispatcher, CancellationToken ct) =>
{
    await dispatcher.ProxyAsync(context, ct);
});

app.Run();
