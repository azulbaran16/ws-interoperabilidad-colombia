using System.ComponentModel.DataAnnotations;

namespace InteropGateway.Api.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string UpstreamBaseUrl { get; set; } = string.Empty;

    public bool ForwardClientAuthorization { get; set; } = true;

    public bool ForwardClientSubscriptionKey { get; set; } = true;

    public string? UpstreamSubscriptionKey { get; set; }

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 40;

    [Range(16, 10000)]
    public int MaxConnectionsPerServer { get; set; } = 1024;

    [Range(30, 3600)]
    public int PooledConnectionLifetimeSeconds { get; set; } = 600;

    public string[] AllowedRootResources { get; set; } =
    [
        "Composition",
        "Patient",
        "Practitioner",
        "Organization",
        "CodeSystem",
        "DocumentReference"
    ];

    public RetryOptions Retry { get; set; } = new();

    public RateLimiterOptions RateLimiter { get; set; } = new();

    public ManagedTokenOptions ManagedToken { get; set; } = new();

    public GatewayClientOptions[] Clients { get; set; } = [];

    public void Validate()
    {
        if (AllowedRootResources.Length == 0)
            throw new InvalidOperationException("Gateway.AllowedRootResources debe tener al menos una ruta habilitada.");

        if (Clients.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(UpstreamBaseUrl))
                throw new InvalidOperationException("Gateway.UpstreamBaseUrl es obligatorio cuando Gateway.Clients está vacío.");

            ValidateAbsoluteUrl(UpstreamBaseUrl, "Gateway.UpstreamBaseUrl");
        }
        else
        {
            var duplicateClientIds = Clients
                .GroupBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            if (duplicateClientIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Hay ClientId duplicados en Gateway.Clients: {string.Join(", ", duplicateClientIds)}");
            }

            for (var i = 0; i < Clients.Length; i++)
            {
                var client = Clients[i];
                var prefix = $"Gateway.Clients[{i}]";
                if (string.IsNullOrWhiteSpace(client.ClientId))
                    throw new InvalidOperationException($"{prefix}.ClientId es obligatorio.");
                if (string.IsNullOrWhiteSpace(client.InboundApiKey))
                    throw new InvalidOperationException($"{prefix}.InboundApiKey es obligatorio.");
                if (string.IsNullOrWhiteSpace(client.UpstreamBaseUrl))
                    throw new InvalidOperationException($"{prefix}.UpstreamBaseUrl es obligatorio.");

                ValidateAbsoluteUrl(client.UpstreamBaseUrl, $"{prefix}.UpstreamBaseUrl");
            }
        }

        Retry.MaxAttempts = Math.Max(1, Retry.MaxAttempts);
        Retry.BaseDelayMs = Math.Max(10, Retry.BaseDelayMs);
        Retry.MaxDelayMs = Math.Max(Retry.BaseDelayMs, Retry.MaxDelayMs);

        RateLimiter.TokenLimit = Math.Max(1, RateLimiter.TokenLimit);
        RateLimiter.TokensPerPeriod = Math.Max(1, RateLimiter.TokensPerPeriod);
        RateLimiter.ReplenishmentPeriodSeconds = Math.Max(1, RateLimiter.ReplenishmentPeriodSeconds);
        RateLimiter.QueueLimit = Math.Max(0, RateLimiter.QueueLimit);
    }

    private static void ValidateAbsoluteUrl(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{name} debe ser una URL absoluta válida.");
        }
    }
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int BaseDelayMs { get; set; } = 200;
    public int MaxDelayMs { get; set; } = 1500;
}

public sealed class RateLimiterOptions
{
    public int TokenLimit { get; set; } = 2000;
    public int TokensPerPeriod { get; set; } = 2000;
    public int ReplenishmentPeriodSeconds { get; set; } = 1;
    public int QueueLimit { get; set; } = 5000;
}

public sealed class ManagedTokenOptions
{
    public bool Enabled { get; set; } = false;
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string GrantType { get; set; } = "client_credentials";
}

public sealed class GatewayClientOptions
{
    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string InboundApiKey { get; set; } = string.Empty;

    [Required]
    public string UpstreamBaseUrl { get; set; } = string.Empty;

    public bool ForwardClientAuthorization { get; set; } = true;

    public bool ForwardClientSubscriptionKey { get; set; } = true;

    public string? UpstreamSubscriptionKey { get; set; }

    public string[] AllowedRootResources { get; set; } = [];

    public ManagedTokenOptions ManagedToken { get; set; } = new();
}
