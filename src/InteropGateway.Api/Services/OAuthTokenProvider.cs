using System.Text.Json;
using System.Collections.Concurrent;
using InteropGateway.Api.Configuration;
using Microsoft.Extensions.Options;

namespace InteropGateway.Api.Services;

public sealed class OAuthTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<GatewayOptions> gatewayOptions,
    TimeProvider timeProvider,
    ILogger<OAuthTokenProvider> logger)
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly GatewayOptions _gatewayOptions = gatewayOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OAuthTokenProvider> _logger = logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedToken> _cachedTokens = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<string?> GetBearerTokenAsync(
        ManagedTokenOptions tokenOptions,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!tokenOptions.Enabled)
            return null;

        var now = _timeProvider.GetUtcNow();
        if (_cachedTokens.TryGetValue(cacheKey, out var cachedToken) &&
            cachedToken.ExpiresAtUtc > now.AddSeconds(30))
        {
            return cachedToken.Token;
        }

        var refreshLock = _refreshLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cachedTokens.TryGetValue(cacheKey, out cachedToken) &&
                cachedToken.ExpiresAtUtc > now.AddSeconds(30))
            {
                return cachedToken.Token;
            }

            ValidateSettings(tokenOptions);
            var token = await RequestTokenAsync(tokenOptions, cancellationToken);
            _cachedTokens[cacheKey] = token;
            return token.Token;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static void ValidateSettings(ManagedTokenOptions tokenOptions)
    {
        if (string.IsNullOrWhiteSpace(tokenOptions.TenantId) ||
            string.IsNullOrWhiteSpace(tokenOptions.ClientId) ||
            string.IsNullOrWhiteSpace(tokenOptions.ClientSecret) ||
            string.IsNullOrWhiteSpace(tokenOptions.Scope))
        {
            throw new InvalidOperationException("ManagedToken está habilitado pero faltan parámetros OAuth por cliente.");
        }
    }

    private async Task<CachedToken> RequestTokenAsync(
        ManagedTokenOptions tokenOptions,
        CancellationToken cancellationToken)
    {
        var tokenUrl = $"https://login.microsoftonline.com/{tokenOptions.TenantId}/oauth2/v2.0/token";
        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = tokenOptions.GrantType,
            ["client_id"] = tokenOptions.ClientId,
            ["client_secret"] = tokenOptions.ClientSecret,
            ["scope"] = tokenOptions.Scope
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(payload)
        };

        var client = _httpClientFactory.CreateClient(ProxyDispatcher.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, _gatewayOptions.RequestTimeoutSeconds));

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"No se pudo obtener token OAuth: {(int)response.StatusCode} {response.ReasonPhrase}. Detalle: {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement))
            throw new InvalidOperationException("Respuesta OAuth inválida: access_token ausente.");

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Respuesta OAuth inválida: access_token vacío.");

        var expiresIn = 3600;
        if (document.RootElement.TryGetProperty("expires_in", out var expiresInElement))
        {
            if (expiresInElement.ValueKind == JsonValueKind.String &&
                int.TryParse(expiresInElement.GetString(), out var parsedAsString))
            {
                expiresIn = parsedAsString;
            }
            else if (expiresInElement.TryGetInt32(out var parsedAsInt))
            {
                expiresIn = parsedAsInt;
            }
        }

        var expiresAt = _timeProvider.GetUtcNow().AddSeconds(Math.Max(60, expiresIn - 30));
        _logger.LogInformation("Token OAuth renovado ({ClientId}). Expira UTC: {ExpiresAtUtc}", tokenOptions.ClientId, expiresAt);
        return new CachedToken(token, expiresAt);
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAtUtc);
}
