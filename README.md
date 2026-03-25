# Intune Wipe Monitor

**Automated device lifecycle management after Intune wipe (factory reset) actions.**

Monitors Microsoft Intune for completed device wipe operations and orchestrates the cleanup of device objects from Active Directory, SCCM, and Intune — ensuring no device is removed until the wipe is confirmed as done.

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                    AZURE                            │
│                                                     │
│  ┌──────────────────────────────────────────────┐   │
│  │  Web App (Blazor Server)                     │   │
│  │  ┌──────────┐ ┌──────────┐ ┌─────────────┐  │   │
│  │  │Dashboard │ │Graph API │ │ SignalR Hub  │  │   │
│  │  │   UI     │ │ Polling  │ │  (Gateway)  │  │   │
│  │  └──────────┘ └──────────┘ └──────┬──────┘  │   │
│  └───────────────────────────────────┼──────────┘   │
│       │              │               │              │
│  ┌────┴────┐  ┌──────┴─────┐   ┌────┴──────┐       │
│  │App      │  │ Key Vault  │   │   App     │       │
│  │Config   │  │ (secrets)  │   │  Insights │       │
│  └─────────┘  └────────────┘   └───────────┘       │
│     VNet + Private Endpoints        │               │
└─────────────────────────────────────┼───────────────┘
                                      │ SignalR (WSS)
┌─────────────────────────────────────┼───────────────┐
│                ON-PREMISES          │               │
│  ┌──────────────────────────────────┴────────────┐  │
│  │  Agent (Windows Service)                      │  │
│  │  ┌────────────────┐  ┌─────────────────────┐  │  │
│  │  │ AD Cleanup     │  │ SCCM Cleanup        │  │  │
│  │  │ (LDAP)         │  │ (AdminService REST) │  │  │
│  │  └────────────────┘  └─────────────────────┘  │  │
│  └───────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

## How It Works

1. **Polling** — A background service queries the Microsoft Graph API every 60 minutes (configurable):
   ```
   GET /beta/deviceManagement/remoteActionAudits?$filter=action eq 'factoryReset'
   ```
2. **Detection** — Devices with `actionState == "done"` are flagged as ready for cleanup
3. **Approval** — An operator reviews and approves the cleanup from the Blazor dashboard
4. **Cleanup** — The SignalR hub sends commands to the on-prem agent:
   - **Active Directory** → LDAP delete of the computer object
   - **SCCM** → AdminService REST API delete of the device record
   - **Intune** → Graph API delete (directly from the cloud)
   - **Entra ID** → Automatically cleaned up after AD sync
5. **Auditing** — Every action is tracked with custom Application Insights events

## Projects

| Project | Description |
|---|---|
| `Intune.WipeMonitor` | ASP.NET Core Blazor Server web app (Azure) |
| `Intune.WipeMonitor.Agent` | Worker Service / Windows Service (on-premises) |
| `Intune.WipeMonitor.Shared` | Shared models, contracts, enums, CMTrace formatter |

## Application Insights Custom Events

All cleanup operations emit structured custom events for auditing and monitoring:

| Event Name | When |
|---|---|
| `DeviceCleanup.Approved` | Operator approves a device cleanup |
| `DeviceCleanup.Skipped` | Operator skips a device |
| `DeviceCleanup.ADDeletion` | AD object deletion attempted (emitted by both cloud and agent) |
| `DeviceCleanup.SCCMDeletion` | SCCM device deletion attempted (emitted by both cloud and agent) |
| `DeviceCleanup.IntuneDeletion` | Intune device deletion attempted |
| `DeviceCleanup.Completed` | Full cleanup completed successfully |
| `DeviceCleanup.Failed` | Cleanup failed on at least one target |
| `Agent.Connected` | On-prem agent connected to the hub |
| `Agent.Disconnected` | On-prem agent disconnected |
| `WipePoll.Completed` | Graph API polling cycle completed |
| `WipePoll.WipeDetected` | New completed wipe detected |

**Example KQL queries:**

```kql
// All cleanup actions in the last 24h
customEvents
| where name startswith "DeviceCleanup"
| where timestamp > ago(24h)
| project timestamp, name, 
    DeviceName = tostring(customDimensions.DeviceName),
    Result = tostring(customDimensions.Result),
    Error = tostring(customDimensions.ErrorMessage)
| order by timestamp desc

// Failed deletions
customEvents
| where name in ("DeviceCleanup.ADDeletion", "DeviceCleanup.SCCMDeletion")
| where customDimensions.Result == "Failed"
| project timestamp, name,
    DeviceName = tostring(customDimensions.DeviceName),
    Error = tostring(customDimensions.ErrorMessage)
```

## Logging — CMTrace Format

Both the web app and the agent write log files in **CMTrace format** (`<![LOG[...]LOG]!>`), compatible with:
- **CMTrace.exe** (SCCM toolkit)
- **OneTrace** (Configuration Manager)

Logs are written to `logs/wipemonitor-web-YYYYMMDD.log` and `logs/wipemonitor-agent-YYYYMMDD.log` with automatic daily rolling and 30-day retention.

## Azure Infrastructure

| Resource | Purpose |
|---|---|
| App Service (B1 Linux) | Hosts the Blazor web app |
| Key Vault | Stores the Graph API client secret (private endpoint) |
| App Configuration | Stores all application settings with KV references (private endpoint) |
| Application Insights | Telemetry, custom events, and monitoring |
| VNet | Network isolation with two subnets |
| Private Endpoints | Key Vault and App Config accessible only via VNet |
| Managed Identity | Web app authenticates to Key Vault and App Config without secrets |

## Prerequisites

### Azure (Web App)
- An **App Registration** in Entra ID with:
  - `DeviceManagementManagedDevices.ReadWrite.All` (Application permission)
  - `DeviceManagementRBAC.Read.All` (Application permission)
- Client secret stored in Key Vault

### On-Premises (Agent)
- Windows machine with:
  - Network access to Active Directory (LDAP)
  - Network access to SCCM AdminService
  - Outbound HTTPS to Azure (for SignalR + App Insights)
- Domain account with permissions to delete computer objects in AD
- SCCM account with permissions to delete device records

## Configuration

### Web App (via Azure App Configuration)

| Key | Description |
|---|---|
| `WipeMonitor:PollingIntervalMinutes` | Polling interval (default: 60) |
| `WipeMonitor:RequireApproval` | Require manual approval (default: true) |
| `WipeMonitor:CleanupIntune` | Also delete from Intune (default: true) |
| `Graph:TenantId` | Azure AD tenant ID |
| `Graph:ClientId` | App Registration client ID |
| `Graph:ClientSecret` | Key Vault reference to the client secret |

### Agent (`appsettings.json`)

```json
{
  "ApplicationInsights": {
    "ConnectionString": "<connection-string>"
  },
  "Agent": {
    "AgentId": "AGENT-01",
    "HubUrl": "https://intune-wipemonitor-app.azurewebsites.net/hub/cleanup",
    "HeartbeatIntervalSeconds": 60,
    "ActiveDirectory": {
      "Server": "dc01.contoso.com",
      "SearchBase": "DC=contoso,DC=com",
      "Port": 389
    },
    "Sccm": {
      "AdminServiceUrl": "https://sccm-server.contoso.com/AdminService"
    }
  }
}
```

## Getting Started

### Build

```bash
dotnet build
```

### Run locally (development)

```bash
# Web App
cd src/Intune.WipeMonitor
dotnet run

# Agent (separate terminal)
cd src/Intune.WipeMonitor.Agent
dotnet run
```

### Install Agent as Windows Service

```powershell
sc.exe create "Intune.WipeMonitor.Agent" binpath="C:\Agent\Intune.WipeMonitor.Agent.exe"
sc.exe start "Intune.WipeMonitor.Agent"
```

### Deploy Web App to Azure

```bash
cd src/Intune.WipeMonitor
dotnet publish -c Release -o publish
Compress-Archive -Path publish/* -DestinationPath publish.zip
az webapp deploy --name intune-wipemonitor-app -g IntuneWipeMonitor-RG --src-path publish.zip --type zip
```

## License

MIT
