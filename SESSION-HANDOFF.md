# Intune Wipe Monitor — Session Handoff v2

> Documento per riprendere la sessione su un'altra macchina con GitHub Copilot CLI.
> Generato il: 2026-03-26T10:11 UTC

---

## Stato attuale — Cosa funziona

| Componente | Stato | Note |
|---|---|---|
| **Web App Blazor** | ✅ Live | https://intune-wipemonitor-app.azurewebsites.net |
| **Entra ID Auth** | ✅ | Ruolo WipeMonitor-Admin richiesto |
| **Graph API Polling** | ✅ | 10 wipe actions recuperate ogni 60 min |
| **Autopilot Cleanup** | ✅ | Primo step cleanup via Graph API |
| **SignalR Hub** | ✅ | `/hub/cleanup` con API key auth |
| **Teams Notifications** | ✅ | Adaptive Card via Power Automate webhook |
| **Azure SQL → SQLite** | ✅ | `/home/wipemonitor.db` persistente |
| **Key Vault** | ✅ | Private endpoint, Graph secret |
| **App Configuration** | ✅ | Private endpoint, tutte le config |
| **App Insights** | ✅ | Custom events per audit |
| **Serilog + CMTrace** | ✅ | File log format CMTrace |
| **IaC Bicep** | ✅ | `Deploy/main.bicep` + `Deploy-Infrastructure.ps1` |
| **Security hardening** | ✅ | HTTPS-only, TLS 1.2, LDAP/OData injection fixes |

## ✅ Agent On-Prem — Deployato e connesso

Agent deployato il **2026-03-26** su `HVHOST` come Windows Service.

| Dettaglio | Valore |
|---|---|
| Servizio | `Intune.WipeMonitor.Agent` (Automatic) |
| Binari | `C:\Services\WipeMonitor.Agent\` |
| AgentId | `AGENT-HVHOST` |
| Hub | Connesso ✅ (`Connesso al Hub` nei log) |
| AD | ❌ Non configurato (Server/SearchBase vuoti) |
| SCCM | ❌ Non configurato (AdminServiceUrl vuoto) |
| Log CMTrace | `C:\Services\WipeMonitor.Agent\logs\wipemonitor-agent-*.log` |
| API Key | Nuova chiave generata e configurata su agent + Azure App Settings |

### ❌ Da completare: Configurazione AD e SCCM

Editare `C:\Services\WipeMonitor.Agent\appsettings.json` sulla VM e riavviare il servizio:

```json
{
  "Agent": {
    "ActiveDirectory": {
      "Server": "<DC_HOSTNAME>",
      "SearchBase": "<DC=domain,DC=com>",
      "Port": 389
    },
    "Sccm": {
      "AdminServiceUrl": "https://<SCCM_SERVER>/AdminService"
    }
  }
}
```

```powershell
# Dopo aver aggiornato appsettings.json:
sc.exe stop "Intune.WipeMonitor.Agent"
sc.exe start "Intune.WipeMonitor.Agent"

# Verificare nei log:
Get-Content C:\Services\WipeMonitor.Agent\logs\wipemonitor-agent-*.log -Tail 20
# Aspettarsi: AD "✅ raggiungibile", SCCM "✅ raggiungibile"
```

---

## Architettura

```
CLEANUP PIPELINE (4 step in sequenza dopo approvazione):

① Autopilot   → Graph API DELETE windowsAutopilotDeviceIdentities (cloud)
② AD          → SignalR → Agent → LDAP Delete computer object (on-prem)
③ SCCM        → SignalR → Agent → AdminService REST DELETE (on-prem)
④ Intune       → Graph API DELETE managedDevices (cloud)
   Entra ID   → Automatico dopo AD sync
```

## Risorse Azure (IntuneWipeMonitor-RG, westus2)

| Risorsa | Nome | Note |
|---|---|---|
| App Service | `intune-wipemonitor-app` | .NET 10, Managed Identity, HTTPS-only, TLS 1.2 |
| Key Vault | `wipemonitor-kv` | Private endpoint, RBAC |
| App Configuration | `wipemonitor-appconfig` | Private endpoint |
| Application Insights | `wipemonitor-insights` | Custom events |
| VNet | `wipemonitor-vnet` | 10.0.0.0/16 |

### App Registrations

| App | Client ID | Scopo |
|---|---|---|
| `Intune.WipeMonitor` | `f3baa699-2f8c-46e0-8e99-925fddb69030` | Graph API (wipe polling, Autopilot/Intune delete) |
| `Intune.WipeMonitor.Portal` | `42c85dd0-f652-459f-bf41-caf7ec741c00` | Dashboard auth (Entra ID + WipeMonitor-Admin role) |

### Tenant & Subscription
- Tenant: `d6dbad84-5922-4700-a049-c7068c37c884`
- Subscription: `74c8c33a-f447-4f1e-890c-f6f71833c8be`

## SignalR Auth

Il Hub usa una policy `AgentOrUser` che accetta:
- **OpenIdConnect** (Entra ID) — per browser/dashboard
- **AgentApiKey** — per l'agent on-prem, via header `X-Api-Key` o query `?api_key=`

API Key configurata in: `WipeMonitor:AgentApiKey` (App Settings sul Web App) e nell'agent `appsettings.json`

## Teams Notifications

- **Canale**: Device Lifecycle (Team ID: `92eca928-359a-4832-8084-cfb13daa43ce`)
- **Metodo**: Power Automate Workflows webhook (Adaptive Card)
- **Webhook URL**: configurato in `WipeMonitor__TeamsWebhookUrl` app setting
- **Test OK**: notifica arrivata nel canale ✅
- **Eventi**: approvazione richiesta + risultato cleanup

## Struttura progetto

```
Deploy/
├── main.bicep                    # IaC infrastruttura Azure
├── main.parameters.json          # Parametri Bicep
├── Deploy-Infrastructure.ps1     # Provisioning end-to-end
└── Export-DeviceActions.ps1      # Export wipe actions (Device Code Flow)

src/
├── Intune.WipeMonitor/           # Web App (Blazor Server) — DEPLOYED
│   ├── Auth/AgentApiKeyAuthHandler.cs  # API key auth per agent
│   ├── Hubs/CleanupHub.cs        # SignalR Hub
│   ├── Services/
│   │   ├── GraphWipeMonitorService.cs  # Polling + Autopilot/Intune delete
│   │   ├── CleanupOrchestrator.cs      # Pipeline 4 step
│   │   ├── CleanupTelemetryService.cs  # App Insights events
│   │   ├── TeamsNotificationService.cs # Adaptive Card webhook
│   │   └── WipeMonitorBackgroundService.cs
│   └── Components/Pages/         # Dashboard, Approvals, History, Status
│
├── Intune.WipeMonitor.Agent/     # Agent On-Prem — DEPLOYED su HVHOST
│   ├── CleanupAgentWorker.cs     # SignalR client + X-Api-Key header
│   ├── AgentSettings.cs          # Config con ApiKey
│   └── Services/                 # AD (LDAP) + SCCM (AdminService)
│
└── Intune.WipeMonitor.Shared/    # Modelli, contratti, CMTrace formatter
```

## Problemi risolti in questa sessione

1. **Serilog crash startup** → Usare `(context, configuration)` callback senza `services`
2. **SQLite DateTimeOffset ORDER BY** → Migrato a Azure SQL → poi tornato a SQLite con converter
3. **App Config private endpoint DNS** → `WEBSITE_DNS_SERVER=168.63.129.16`
4. **SignalR typed hub non supporta client results** → `IHubContext<CleanupHub>` con `InvokeAsync<T>`
5. **SignalR AllowAnonymous** → Sostituito con API key auth `AgentOrUser` policy
6. **OData injection SCCM/Autopilot** → `Uri.EscapeDataString` + `Guid.TryParse`
7. **Graph 401** → Service Principal mancante + wrong permission IDs
8. **ChannelMessage.Send non esiste come Application** → Webhook approach

## Note per la prossima sessione

- Configurare AD Server e SCCM AdminServiceUrl nell'agent, poi testare ciclo completo
- Testare: approvazione → cleanup → Teams notification → log CMTrace
- Considerare auto-update dell'agent (endpoint `/api/agent/update` che serve lo zip)

