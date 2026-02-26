# Despliegue completo en VPS (1 WS para todos los clientes)

Esta guia deja **un solo gateway central** en Colombia para todos los clientes.

Modelo:

1. Cada WCF cliente envia su request al mismo dominio del gateway.
2. El gateway identifica el cliente por API key de entrada.
3. Segun el cliente, enruta a su URL de ministerio (y su subscription key).
4. El token se puede seguir obteniendo en el WCF (recomendado en tu caso).

## 1) Arquitectura

`WCF Cliente A/B/C -> Gateway Unico (VPS CO) -> APIM Minsalud por cliente`

## 2) Requisitos

- VPS Ubuntu 22.04/24.04.
- Dominio (ejemplo: `interop-central.tudominio.com`).
- SSH con sudo.
- Puertos 80/443 abiertos.

## 3) Preparar servidor

```bash
sudo apt update && sudo apt upgrade -y
sudo timedatectl set-timezone America/Bogota
sudo apt install -y curl wget gnupg ca-certificates lsb-release ufw nginx
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'
sudo ufw enable
```

## 4) Instalar .NET 8 Runtime

```bash
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-runtime-8.0
dotnet --info
```

## 5) Publicar aplicacion

```bash
sudo mkdir -p /opt/interop-gateway
sudo chown -R $USER:$USER /opt/interop-gateway
cd /opt/interop-gateway
git clone <URL_REPO> source
cd source
dotnet publish ./src/InteropGateway.Api/InteropGateway.Api.csproj -c Release -o /opt/interop-gateway/current
```

## 6) Configuracion multi-cliente (clave)

Archivo:

`/etc/interop-gateway/appsettings.Production.json`

```json
{
  "Gateway": {
    "UpstreamBaseUrl": "",
    "ForwardClientAuthorization": true,
    "ForwardClientSubscriptionKey": true,
    "UpstreamSubscriptionKey": "",
    "RequestTimeoutSeconds": 40,
    "MaxConnectionsPerServer": 1024,
    "PooledConnectionLifetimeSeconds": 600,
    "AllowedRootResources": [
      "Composition",
      "Patient",
      "Practitioner",
      "Organization",
      "CodeSystem",
      "DocumentReference"
    ],
    "Retry": {
      "MaxAttempts": 3,
      "BaseDelayMs": 200,
      "MaxDelayMs": 1500
    },
    "RateLimiter": {
      "TokenLimit": 2000,
      "TokensPerPeriod": 2000,
      "ReplenishmentPeriodSeconds": 1,
      "QueueLimit": 5000
    },
    "Clients": [
      {
        "ClientId": "cliente-a",
        "InboundApiKey": "APIKEY-ENTRADA-CLIENTE-A",
        "UpstreamBaseUrl": "https://URL-MINSALUD-CLIENTE-A",
        "ForwardClientAuthorization": true,
        "ForwardClientSubscriptionKey": false,
        "UpstreamSubscriptionKey": "SUBKEY-MINSALUD-CLIENTE-A",
        "ManagedToken": {
          "Enabled": false,
          "TenantId": "",
          "ClientId": "",
          "ClientSecret": "",
          "Scope": "",
          "GrantType": "client_credentials"
        }
      },
      {
        "ClientId": "cliente-b",
        "InboundApiKey": "APIKEY-ENTRADA-CLIENTE-B",
        "UpstreamBaseUrl": "https://URL-MINSALUD-CLIENTE-B",
        "ForwardClientAuthorization": true,
        "ForwardClientSubscriptionKey": false,
        "UpstreamSubscriptionKey": "SUBKEY-MINSALUD-CLIENTE-B",
        "ManagedToken": {
          "Enabled": false,
          "TenantId": "",
          "ClientId": "",
          "ClientSecret": "",
          "Scope": "",
          "GrantType": "client_credentials"
        }
      }
    ]
  },
  "Security": {
    "RequireApiKey": true,
    "ApiKeyHeaderName": "Ocp-Apim-Subscription-Key",
    "ApiKeys": []
  },
  "AllowedHosts": "*"
}
```

Notas:

- `Gateway.Clients[]` es lo que habilita 1 WS para todos.
- `InboundApiKey` identifica el cliente en el gateway.
- `UpstreamBaseUrl` y `UpstreamSubscriptionKey` quedan aislados por cliente.
- `ManagedToken.Enabled=false` si el token lo saca el WCF.

## 7) systemd

`/etc/systemd/system/interop-gateway.service`:

```ini
[Unit]
Description=Interop Gateway Central
After=network.target

[Service]
WorkingDirectory=/opt/interop-gateway/current
ExecStart=/usr/bin/dotnet /opt/interop-gateway/current/InteropGateway.Api.dll
Restart=always
RestartSec=5
User=www-data
Group=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable interop-gateway
sudo systemctl start interop-gateway
sudo systemctl status interop-gateway
```

## 8) Nginx

`/etc/nginx/sites-available/interop-gateway`:

```nginx
server {
    listen 80;
    server_name interop-central.tudominio.com;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 90s;
        proxy_connect_timeout 15s;
        proxy_send_timeout 90s;
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/interop-gateway /etc/nginx/sites-enabled/interop-gateway
sudo nginx -t
sudo systemctl reload nginx
```

## 9) SSL

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d interop-central.tudominio.com
sudo certbot renew --dry-run
```

## 10) Integracion WCF por cliente

En cada cliente:

- `apim_url` del WCF debe ser el mismo gateway:
  - `https://interop-central.tudominio.com`
- El WCF sigue enviando:
  - `Authorization: Bearer <token del cliente>`
  - `Ocp-Apim-Subscription-Key: <InboundApiKey del cliente en gateway>`

El gateway usa esa key para elegir destino del cliente.

## 11) Pruebas

```bash
curl -i https://interop-central.tudominio.com/health/live
curl -i https://interop-central.tudominio.com/health/ready
```

Prueba cliente A:

```bash
curl -i -X POST "https://interop-central.tudominio.com/Composition/\$consultar-rda-paciente" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer TOKEN_CLIENTE_A" \
  -H "Ocp-Apim-Subscription-Key: APIKEY-ENTRADA-CLIENTE-A" \
  -d '{"resourceType":"Parameters","parameter":[]}'
```

## 12) Operacion diaria

Actualizar:

```bash
cd /opt/interop-gateway/source
git pull
dotnet publish ./src/InteropGateway.Api/InteropGateway.Api.csproj -c Release -o /opt/interop-gateway/current
sudo systemctl restart interop-gateway
```

Logs:

```bash
journalctl -u interop-gateway -f
```

## 13) Recomendaciones fuertes

- Usar API key de entrada distinta por cliente.
- Rotar llaves periodicamente.
- Restringir acceso por IP (solo tus servidores cloud).
- Monitorear 401/403/429/5xx.
- Respaldar archivo de configuracion productivo.
