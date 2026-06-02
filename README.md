# Azure DNS DDNS Updater
## Dynamic DNS Updater for Azure DNS

A Docker-compatible .NET 10 console application for updating DNS A records in Azure DNS with multi-architecture support.

## Features
- Periodically updates DNS A records for multiple hostnames in Azure DNS
- Secure authentication via Docker secrets or environment variables
- Supports managed identity, environment credential, or client secret authentication
- Multi-architecture support: linux/amd64, linux/arm64, linux/arm/v7
- Designed to run on Raspberry Pi and other ARM devices

## Security Model

Important: Authentication secrets are provided via Docker secrets (recommended for production) or environment variables. Do not commit secrets into appsettings.json or source control.

## Configuration

| Setting | Description | Default | Environment Variable |
|---------|-------------|---------|----------------------|
| AuthMode | Azure auth mode: Default, Environment, ManagedIdentity, ClientSecret | Default | DDNS__AuthMode |
| TenantId | Microsoft Entra tenant ID (ClientSecret mode) | - | DDNS__TenantId |
| ClientId | Service principal client ID (ClientSecret mode) | - | DDNS__ClientId |
| ClientSecret | Service principal client secret (ClientSecret mode) | - | DDNS__ClientSecret or Docker secret ddns_azure_client_secret |
| ClientSecretFile | Secret file path for client secret | /run/secrets/ddns_azure_client_secret | DDNS__ClientSecretFile |
| ManagedIdentityClientId | User-assigned managed identity client ID | - | DDNS__ManagedIdentityClientId |
| SubscriptionId | Azure subscription ID containing DNS zone | - | DDNS__SubscriptionId |
| ResourceGroupName | Resource group containing DNS zone | - | DDNS__ResourceGroupName |
| ZoneName | Azure DNS zone name | - | DDNS__ZoneName |
| Hostnames | Array of hostnames to update (@, relative labels, or FQDN in zone) | [] | DDNS__Hostnames__0, DDNS__Hostnames__1, etc. |
| UpdateIntervalSeconds | Update interval in seconds | 300 | DDNS__UpdateIntervalSeconds |
| TtlSeconds | DNS TTL for A records | 300 | DDNS__TtlSeconds |
| IpLookupUrls | Public IP endpoints | api.ipify.org, ifconfig.me/ip, checkip.amazonaws.com | DDNS__IpLookupUrls__0, DDNS__IpLookupUrls__1, etc. |

## Quick Start

### Option 1: Docker Swarm Stack with Secrets

1. Initialize Docker Swarm and create secrets:

```bash
docker swarm init
echo "your_tenant_id" | docker secret create ddns_azure_tenant_id -
echo "your_client_id" | docker secret create ddns_azure_client_id -
echo "your_client_secret" | docker secret create ddns_azure_client_secret -
```

2. Deploy with Docker Compose stack:

```bash
# Edit docker-compose.yml to configure your subscription, zone, and hostnames
docker stack deploy -c docker-compose.yml ddns
```

Or deploy with Docker service directly:

```bash
docker service create \
  --name azure-ddns \
  --secret ddns_azure_tenant_id \
  --secret ddns_azure_client_id \
  --secret ddns_azure_client_secret \
  --env DDNS__AuthMode=ClientSecret \
  --env DDNS__SubscriptionId=00000000-0000-0000-0000-000000000000 \
  --env DDNS__ResourceGroupName=dns-rg \
  --env DDNS__ZoneName=example.com \
  --env DDNS__Hostnames__0=@ \
  --env DDNS__Hostnames__1=www \
  --env DDNS__UpdateIntervalSeconds=300 \
  --restart-condition on-failure \
  dntim/azure-ddns:latest
```

### Option 2: Docker Compose with Environment Variables

1. Edit docker-compose.yml:
- Comment out the secrets section
- Uncomment TenantId, ClientId, and ClientSecret environment variables
- Optionally uncomment the Watchtower service for automatic updates

2. Deploy with Docker Compose:

```bash
docker compose up -d
```

Or deploy with Docker run:

```bash
docker run -d \
  --name azure-ddns \
  --restart unless-stopped \
  -e DDNS__AuthMode=ClientSecret \
  -e DDNS__TenantId=your_tenant_id \
  -e DDNS__ClientId=your_client_id \
  -e DDNS__ClientSecret=your_client_secret \
  -e DDNS__SubscriptionId=your_subscription_id \
  -e DDNS__ResourceGroupName=dns-rg \
  -e DDNS__ZoneName=example.com \
  -e DDNS__Hostnames__0=@ \
  -e DDNS__Hostnames__1=www \
  -e DDNS__UpdateIntervalSeconds=300 \
  dntim/azure-ddns:latest
```

### Option 3: Managed Identity (Azure-hosted)

For Azure-hosted deployments (for example Container Apps or AKS with workload identity), set:

```bash
DDNS__AuthMode=ManagedIdentity
DDNS__SubscriptionId=<subscription>
DDNS__ResourceGroupName=<resource-group>
DDNS__ZoneName=<zone>
DDNS__Hostnames__0=@
```

Ensure the managed identity has DNS Zone Contributor role on the target zone.

## Building Multi-Architecture Images

Build and push a manifest list with the same architecture coverage as the original project:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64,linux/arm/v7 \
  -t dntim/azure-ddns:latest \
  --push .
```

## Monitoring

View logs:

```bash
# For Docker Swarm stack
docker service logs -f ddns_azure-ddns

# For Docker Compose
docker compose logs -f azure-ddns

# For regular container
docker logs -f azure-ddns
```

## Managing Secrets

```bash
# List secrets
docker secret ls

# Rotate credentials
docker secret rm ddns_azure_tenant_id ddns_azure_client_id ddns_azure_client_secret
echo "new_tenant_id" | docker secret create ddns_azure_tenant_id -
echo "new_client_id" | docker secret create ddns_azure_client_id -
echo "new_client_secret" | docker secret create ddns_azure_client_secret -
docker service update --force ddns_ddns-updater
```

## Platform Support

Architectures: linux/amd64, linux/arm64, linux/arm/v7
Platforms: Intel/AMD x64, ARM64 (Raspberry Pi 4+), ARMv7
Runtimes: Docker, Podman, containerd, Kubernetes

## CI/CD

Use Docker Buildx in GitHub Actions to build and publish multi-architecture images to Docker Hub on every push to main.

## License

MIT
