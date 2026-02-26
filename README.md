# Interop Gateway Colombia

WS independiente para intermediar interoperabilidad Minsalud desde infraestructura en Colombia.

## Objetivo

- Recibir alto volumen de solicitudes desde servidores cloud.
- Reenviar a APIM/FHIR Minsalud desde un servidor colombiano.
- Mantener compatibilidad con rutas actuales (`/Composition/...`, `/Patient/...`, etc.).

## Arquitectura

- `ASP.NET Core 8` con `HttpClientFactory` y pooling de conexiones.
- Rate limiter global (`token bucket`) para proteger el servicio.
- Reintentos automaticos para errores transitorios (`408`, `429`, `5xx`).
- Seguridad por API key configurable.
- Token OAuth gestionado opcionalmente en el gateway.

## Rutas soportadas

Se permite proxy para estos recursos raiz:

- `Composition`
- `Patient`
- `Practitioner`
- `Organization`
- `CodeSystem`
- `DocumentReference`

## Configuracion

Archivo: `src/InteropGateway.Api/appsettings.json`

- `Gateway.UpstreamBaseUrl`: URL base global (solo si no usas `Gateway.Clients`).
- `Gateway.ForwardClientAuthorization`: reenvia header `Authorization` del cliente.
- `Gateway.ForwardClientSubscriptionKey`: reenvia `Ocp-Apim-Subscription-Key`.
- `Gateway.UpstreamSubscriptionKey`: si se define, sobreescribe la subscription key.
- `Gateway.ManagedToken`: habilita token OAuth centralizado (opcional).
- `Gateway.Clients`: configuracion multi-cliente con URL y credenciales por cliente.
- `Security.RequireApiKey`: activa autenticacion de entrada por API key.
- `Security.ApiKeyHeaderName`: header que valida el gateway.
- `Security.ApiKeys`: llaves validas.

Por defecto se valida `Ocp-Apim-Subscription-Key` para que puedas migrar sin tocar codigo cliente.

### Modo multi-cliente (recomendado para tu caso)

Cada cliente define:

- `ClientId`
- `InboundApiKey` (llave que llega al gateway)
- `UpstreamBaseUrl` (URL especifica de Minsalud para ese cliente)
- `UpstreamSubscriptionKey` (opcional, para no depender de la del request)
- `ManagedToken` (opcional, OAuth por cliente)

El gateway toma la API key entrante y enruta automaticamente al `UpstreamBaseUrl` del cliente correcto.

## Ejecucion local

```powershell
cd C:\Repositorios\ws-interoperabilidad-colombia
dotnet run --project .\src\InteropGateway.Api\InteropGateway.Api.csproj
```

Para validar comportamiento con `appsettings.json` (sin perfil de desarrollo):

```powershell
dotnet run --no-launch-profile --project .\src\InteropGateway.Api\InteropGateway.Api.csproj
```

Health checks:

- `GET /health/live`
- `GET /health/ready`

## Integracion con SaludSystem10 (sin romper contratos actuales)

En `interop_minsalud_config`, cambia `apim_url` para que apunte a este gateway en Colombia.

Ejemplo:

```sql
UPDATE interop_minsalud_config
SET apim_url = 'https://TU-GATEWAY-COLOMBIA'
WHERE activo = 1;
```

Con esto, los metodos existentes (`EnviarRdaPaciente`, `EnviarRdaHospitalizacion`, etc.) siguen funcionando,
pero ya no salen directo desde servidores cloud a Minsalud.

Notas importantes:

- Si cada cliente tiene URL distinta de ministerio, el gateway lo soporta por `Gateway.Clients`.
- Si cada cliente tiene `subscription key` distinta, configurala en `UpstreamSubscriptionKey`.
- Si cada cliente usa OAuth distinto, habilita `ManagedToken` dentro de cada cliente.

## Recomendaciones de despliegue

- Desplegar detras de Nginx/Traefik o Application Gateway.
- Habilitar HTTPS obligatorio.
- Restringir acceso por IP de servidores cloud.
- Rotar API keys y secretos OAuth periodicamente.
- Escalar horizontalmente si el volumen aumenta.
