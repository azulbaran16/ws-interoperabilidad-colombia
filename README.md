# Interop Gateway Colombia

Standalone gateway service that brokers traffic between cloud-hosted clients and the Colombian Ministry of Health (Minsalud) interoperability platform (RDA / Vulcano), running on infrastructure inside Colombia.

## What it solves

The Colombian RDA endpoint expects traffic originating from Colombian infrastructure and works best with predictable connection pooling and rate limiting. This gateway sits between any number of cloud-hosted clients and Minsalud:

- Accepts a high volume of inbound requests from cloud servers.
- Forwards them to the Minsalud APIM/FHIR endpoints from a Colombian server.
- Preserves the existing route contracts (`/Composition/...`, `/Patient/...`, etc.) so client code does not change.

## Stack

- **ASP.NET Core 8** with `HttpClientFactory` and connection pooling.
- Global token-bucket **rate limiter** to protect the upstream service.
- **Automatic retries** for transient errors (`408`, `429`, `5xx`).
- Configurable **API key authentication** at the gateway boundary.
- Optional **OAuth 2.0 token management** centralized at the gateway.

## Supported routes

The gateway proxies these FHIR root resources:

- `Composition`
- `Patient`
- `Practitioner`
- `Organization`
- `CodeSystem`
- `DocumentReference`

## Configuration

File: `src/InteropGateway.Api/appsettings.json`

| Key | Description |
|---|---|
| `Gateway.UpstreamBaseUrl` | Single upstream URL (only when `Gateway.Clients` is not used). |
| `Gateway.ForwardClientAuthorization` | Forward the inbound `Authorization` header. |
| `Gateway.ForwardClientSubscriptionKey` | Forward `Ocp-Apim-Subscription-Key`. |
| `Gateway.UpstreamSubscriptionKey` | Override the subscription key when defined. |
| `Gateway.ManagedToken` | Enable a centrally managed OAuth token. |
| `Gateway.Clients` | Multi-tenant configuration (per-client URL and credentials). |
| `Security.RequireApiKey` | Toggle inbound API-key authentication. |
| `Security.ApiKeyHeaderName` | Header used to validate inbound API keys. |
| `Security.ApiKeys` | Valid inbound API keys. |

By default the gateway accepts `Ocp-Apim-Subscription-Key` so existing clients can migrate without code changes.

### Multi-tenant mode

Each client defines:

- `ClientId`
- `InboundApiKey`
- `UpstreamBaseUrl`
- `UpstreamSubscriptionKey` (optional)
- `ManagedToken` (optional, OAuth per client)

The gateway uses the inbound API key to route the request to the matching `UpstreamBaseUrl`.

## Running locally

```powershell
dotnet run --project ./src/InteropGateway.Api/InteropGateway.Api.csproj
```

Health checks:

- `GET /health/live`
- `GET /health/ready`

## Drop-in integration with an existing system

Point the existing `apim_url` configuration at the gateway. Existing methods (`SendRdaPatient`, `SendRdaHospitalization`, etc.) keep working without modification.

```sql
UPDATE interop_minsalud_config
SET apim_url = 'https://your-gateway-colombia'
WHERE active = 1;
```

## Deployment recommendations

- Run behind Nginx / Traefik / Application Gateway.
- Enforce HTTPS.
- Restrict access by source IP for cloud servers.
- Rotate API keys and OAuth secrets on a schedule.
- Scale horizontally as traffic grows.

## Status

Used as a working reference while integrating a hospital information system with RDA Minsalud in production.
