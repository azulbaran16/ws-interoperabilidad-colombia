using System.Net;
using System.Net.Http.Headers;
using InteropGateway.Api.Configuration;
using Microsoft.Extensions.Options;

namespace InteropGateway.Api.Services;

public sealed class ProxyDispatcher(
    IHttpClientFactory httpClientFactory,
    IOptions<GatewayOptions> gatewayOptions,
    IOptions<SecurityOptions> securityOptions,
    OAuthTokenProvider tokenProvider,
    ILogger<ProxyDispatcher> logger)
{
    public const string HttpClientName = "interop-upstream";
    public const string SelectedClientIdContextKey = "__selected_gateway_client_id";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host"
    };

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly GatewayOptions _gatewayOptions = gatewayOptions.Value;
    private readonly SecurityOptions _securityOptions = securityOptions.Value;
    private readonly OAuthTokenProvider _tokenProvider = tokenProvider;
    private readonly ILogger<ProxyDispatcher> _logger = logger;
    private readonly Random _random = new();

    public async Task ProxyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var normalizedPath = path.Trim('/');
        var target = ResolveTarget(context);
        if (target is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "No se pudo resolver la configuración de cliente para esta solicitud."
            }, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "Ruta no encontrada." }, cancellationToken);
            return;
        }

        if (!IsAllowedRoute(normalizedPath, target.AllowedRootResources))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new { error = "Ruta no permitida por la configuración del gateway." },
                cancellationToken);
            return;
        }

        var upstreamUrl = BuildUpstreamUrl(target.UpstreamBaseUrl, path, context.Request.QueryString.Value);
        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var upstreamUri))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { error = "No fue posible construir la URL de salida." },
                cancellationToken);
            return;
        }

        var payload = await ReadRequestBodyAsync(context.Request, cancellationToken);
        var incomingHeaders = SnapshotHeaders(context.Request.Headers);
        var correlationId = ResolveCorrelationId(context);
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        HttpResponseMessage? upstreamResponse = null;
        Exception? transportException = null;

        for (var attempt = 1; attempt <= _gatewayOptions.Retry.MaxAttempts; attempt++)
        {
            using var upstreamRequest = BuildUpstreamRequest(
                context.Request.Method,
                upstreamUri,
                incomingHeaders,
                payload,
                context.Request.ContentType,
                context.Request.Scheme,
                remoteIp,
                correlationId,
                target);

            try
            {
                await ApplyCredentialOverridesAsync(upstreamRequest, target, cancellationToken);

                var client = _httpClientFactory.CreateClient(HttpClientName);
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, target.RequestTimeoutSeconds));

                upstreamResponse = await client.SendAsync(
                    upstreamRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (ShouldRetry(upstreamResponse.StatusCode) &&
                    attempt < _gatewayOptions.Retry.MaxAttempts)
                {
                    upstreamResponse.Dispose();
                    await DelayRetryAsync(attempt, cancellationToken);
                    continue;
                }

                break;
            }
            catch (Exception ex) when (ShouldRetryException(ex, cancellationToken) &&
                                       attempt < _gatewayOptions.Retry.MaxAttempts)
            {
                transportException = ex;
                await DelayRetryAsync(attempt, cancellationToken);
            }
            catch (Exception ex)
            {
                transportException = ex;
                break;
            }
        }

        if (upstreamResponse is null)
        {
            _logger.LogError(transportException, "Error enviando a upstream {Upstream}", upstreamUri);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = "No se pudo contactar el upstream de interoperabilidad.",
                    detail = transportException?.Message,
                    correlationId
                },
                cancellationToken);
            return;
        }

        using (upstreamResponse)
        {
            await CopyToDownstreamAsync(context, upstreamResponse, correlationId, cancellationToken);
        }
    }

    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var targets = BuildReadinessTargets();
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.UpstreamBaseUrl))
                return new ReadinessResult(false, $"UpstreamBaseUrl vacío para {target.ClientId}.");

            if (!Uri.TryCreate(target.UpstreamBaseUrl, UriKind.Absolute, out _))
                return new ReadinessResult(false, $"UpstreamBaseUrl inválido para {target.ClientId}.");

            if (!target.ManagedToken.Enabled)
                continue;

            try
            {
                var token = await _tokenProvider.GetBearerTokenAsync(
                    target.ManagedToken,
                    target.ClientId,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(token))
                    return new ReadinessResult(false, $"ManagedToken habilitado pero sin token para {target.ClientId}.");
            }
            catch (Exception e)
            {
                return new ReadinessResult(false, $"Error validando token para {target.ClientId}: {e.Message}");
            }
        }

        return new ReadinessResult(true, "Proxy listo.");
    }

    private bool IsAllowedRoute(string normalizedPath, string[] allowedRootResources)
    {
        var root = normalizedPath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return allowedRootResources.Any(x =>
            string.Equals(x, root, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildUpstreamUrl(string upstreamBaseUrl, string path, string? queryString)
    {
        var baseUrl = upstreamBaseUrl.TrimEnd('/');
        var cleanPath = (path ?? string.Empty).TrimStart('/');
        var query = queryString ?? string.Empty;
        return $"{baseUrl}/{cleanPath}{query}";
    }

    private HttpRequestMessage BuildUpstreamRequest(
        string method,
        Uri upstreamUri,
        IDictionary<string, string[]> incomingHeaders,
        byte[]? payload,
        string? contentType,
        string scheme,
        string remoteIp,
        string correlationId,
        GatewayTarget target)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), upstreamUri);
        if (payload is { Length: > 0 })
        {
            request.Content = new ByteArrayContent(payload);
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            else
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
        }

        foreach (var header in incomingHeaders)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;

            var isSecurityHeader = string.Equals(
                header.Key,
                _securityOptions.ApiKeyHeaderName,
                StringComparison.OrdinalIgnoreCase);

            if (isSecurityHeader &&
                !string.Equals(header.Key, "Ocp-Apim-Subscription-Key", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!target.ForwardClientAuthorization &&
                string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!target.ForwardClientSubscriptionKey &&
                string.Equals(header.Key, "Ocp-Apim-Subscription-Key", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        request.Headers.Remove("X-Forwarded-For");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", remoteIp);

        request.Headers.Remove("X-Forwarded-Proto");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", scheme);

        request.Headers.Remove("X-Correlation-Id");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

        return request;
    }

    private async Task ApplyCredentialOverridesAsync(
        HttpRequestMessage request,
        GatewayTarget target,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(target.UpstreamSubscriptionKey))
        {
            request.Headers.Remove("Ocp-Apim-Subscription-Key");
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", target.UpstreamSubscriptionKey);
        }

        var managedToken = await _tokenProvider.GetBearerTokenAsync(
            target.ManagedToken,
            target.ClientId,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(managedToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managedToken);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 408 || code == 429 || code >= 500;
    }

    private static bool ShouldRetryException(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        return exception is HttpRequestException ||
               exception is TaskCanceledException;
    }

    private async Task DelayRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseDelay = _gatewayOptions.Retry.BaseDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var jitter = _random.Next(0, 100);
        var computed = (int)Math.Min(_gatewayOptions.Retry.MaxDelayMs, baseDelay + jitter);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, computed)), cancellationToken);
    }

    private static async Task<byte[]?> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsDelete(request.Method))
            return null;

        if ((request.ContentLength ?? 0) == 0 && !request.Body.CanRead)
            return null;

        request.EnableBuffering();
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, cancellationToken);
        request.Body.Position = 0;
        return ms.Length == 0 ? null : ms.ToArray();
    }

    private static IDictionary<string, string[]> SnapshotHeaders(IHeaderDictionary headers)
    {
        return headers.ToDictionary(
            h => h.Key,
            h => h.Value.Select(v => v ?? string.Empty).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var existing) &&
            !string.IsNullOrWhiteSpace(existing.ToString()))
        {
            return existing.ToString();
        }

        return Guid.NewGuid().ToString("N");
    }

    private static async Task CopyToDownstreamAsync(
        HttpContext context,
        HttpResponseMessage upstreamResponse,
        string correlationId,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)upstreamResponse.StatusCode;

        foreach (var header in upstreamResponse.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        context.Response.Headers["X-Correlation-Id"] = correlationId;
        context.Response.Headers.Remove("transfer-encoding");

        await upstreamResponse.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private GatewayTarget? ResolveTarget(HttpContext context)
    {
        if (_gatewayOptions.Clients.Length == 0)
        {
            return new GatewayTarget(
                ClientId: "default",
                UpstreamBaseUrl: _gatewayOptions.UpstreamBaseUrl,
                UpstreamSubscriptionKey: _gatewayOptions.UpstreamSubscriptionKey,
                ForwardClientAuthorization: _gatewayOptions.ForwardClientAuthorization,
                ForwardClientSubscriptionKey: _gatewayOptions.ForwardClientSubscriptionKey,
                AllowedRootResources: _gatewayOptions.AllowedRootResources,
                ManagedToken: _gatewayOptions.ManagedToken,
                RequestTimeoutSeconds: _gatewayOptions.RequestTimeoutSeconds);
        }

        var selectedClientId = context.Items.TryGetValue(SelectedClientIdContextKey, out var selectedObj)
            ? Convert.ToString(selectedObj)
            : null;

        var client = _gatewayOptions.Clients.FirstOrDefault(c =>
            string.Equals(c.ClientId, selectedClientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
            return null;

        var allowed = client.AllowedRootResources.Length > 0
            ? client.AllowedRootResources
            : _gatewayOptions.AllowedRootResources;

        return new GatewayTarget(
            ClientId: client.ClientId,
            UpstreamBaseUrl: client.UpstreamBaseUrl,
            UpstreamSubscriptionKey: client.UpstreamSubscriptionKey,
            ForwardClientAuthorization: client.ForwardClientAuthorization,
            ForwardClientSubscriptionKey: client.ForwardClientSubscriptionKey,
            AllowedRootResources: allowed,
            ManagedToken: client.ManagedToken,
            RequestTimeoutSeconds: _gatewayOptions.RequestTimeoutSeconds);
    }

    private IEnumerable<GatewayTarget> BuildReadinessTargets()
    {
        if (_gatewayOptions.Clients.Length == 0)
        {
            yield return new GatewayTarget(
                ClientId: "default",
                UpstreamBaseUrl: _gatewayOptions.UpstreamBaseUrl,
                UpstreamSubscriptionKey: _gatewayOptions.UpstreamSubscriptionKey,
                ForwardClientAuthorization: _gatewayOptions.ForwardClientAuthorization,
                ForwardClientSubscriptionKey: _gatewayOptions.ForwardClientSubscriptionKey,
                AllowedRootResources: _gatewayOptions.AllowedRootResources,
                ManagedToken: _gatewayOptions.ManagedToken,
                RequestTimeoutSeconds: _gatewayOptions.RequestTimeoutSeconds);
            yield break;
        }

        foreach (var client in _gatewayOptions.Clients)
        {
            var allowed = client.AllowedRootResources.Length > 0
                ? client.AllowedRootResources
                : _gatewayOptions.AllowedRootResources;

            yield return new GatewayTarget(
                ClientId: client.ClientId,
                UpstreamBaseUrl: client.UpstreamBaseUrl,
                UpstreamSubscriptionKey: client.UpstreamSubscriptionKey,
                ForwardClientAuthorization: client.ForwardClientAuthorization,
                ForwardClientSubscriptionKey: client.ForwardClientSubscriptionKey,
                AllowedRootResources: allowed,
                ManagedToken: client.ManagedToken,
                RequestTimeoutSeconds: _gatewayOptions.RequestTimeoutSeconds);
        }
    }

    private sealed record GatewayTarget(
        string ClientId,
        string UpstreamBaseUrl,
        string? UpstreamSubscriptionKey,
        bool ForwardClientAuthorization,
        bool ForwardClientSubscriptionKey,
        string[] AllowedRootResources,
        ManagedTokenOptions ManagedToken,
        int RequestTimeoutSeconds);
}

public sealed record ReadinessResult(bool IsReady, string Message);
