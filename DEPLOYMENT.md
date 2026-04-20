# PolymtkCp deployment guide (Azure App Service + GitHub Actions)

End-to-end setup for the existing **`polymtkcp`** App Service in resource group **`polymtkcp-rg`** (Mexico Central). Covers required configuration, Azure Key Vault + Blob Storage for ASP.NET Data Protection, RBAC for managed identity, and a GitHub Actions workflow.

---

## 1. Configuration keys

The app reads these from `IConfiguration` (App Service "Application settings" or environment variables; **never** commit them).

| Key | Required | Source | Notes |
|---|---|---|---|
| `Supabase__Url` | ✅ | Supabase project | `https://<ref>.supabase.co` |
| `Supabase__PublishableKey` | ✅ | Supabase project → Settings → API Keys | `sb_publishable_…` |
| `Supabase__SecretKey` | ✅ for watcher | Supabase project → Settings → API Keys | `sb_secret_…`. Bypasses RLS — backend only. Watcher won't start without it |
| `Polygon__RpcUrl` | ❌ | Public RPC | Defaults to `https://1rpc.io/matic` if unset/empty |
| `DataProtection__BlobStorageUri` | ✅ in Production | Azure Storage | Full blob URI ending in `keys.xml`, e.g. `https://polymtkcpdpkeys.blob.core.windows.net/dpkeys/keys.xml` |
| `DataProtection__KeyVaultKeyId` | ✅ in Production | Azure Key Vault | Full key URI, e.g. `https://polymtkcp-kv.vault.azure.net/keys/dp-kek` |
| `Watcher__Enabled` | ❌ | App setting | Default `true`. Set `false` to disable the background poller in this slot |
| `ASPNETCORE_ENVIRONMENT` | ✅ | App setting | `Production` (App Service default) |

> **App Service uses double underscores (`__`) instead of `:`** to express nested keys. `Supabase:Url` in `appsettings.json` becomes `Supabase__Url` in App Service.

If `DataProtection__BlobStorageUri` or `DataProtection__KeyVaultKeyId` is missing while `ASPNETCORE_ENVIRONMENT=Production`, **app startup throws** by design. In Development, the app falls back to local file-system Data Protection with a warning.

---

## 2. Provision the supporting Azure resources

All commands assume the Azure CLI is logged in (`az login`) and the default subscription is set. Replace names if you'd like, but stay inside `polymtkcp-rg`.

### 2.1 Variables

```powershell
$RG          = "polymtkcp-rg"
$LOCATION    = "mexicocentral"
$APP         = "polymtkcp"
$STORAGE     = "polymtkcpdpkeys"   # must be globally unique, lowercase, 3-24 chars
$CONTAINER   = "dpkeys"
$KV          = "polymtkcp-kv"      # must be globally unique
$KEY_NAME    = "dp-kek"
```

### 2.2 Storage account + container (holds the Data Protection key ring)

```powershell
az storage account create `
  --name $STORAGE `
  --resource-group $RG `
  --location $LOCATION `
  --sku Standard_LRS `
  --kind StorageV2 `
  --allow-blob-public-access false `
  --min-tls-version TLS1_2

az storage container create `
  --name $CONTAINER `
  --account-name $STORAGE `
  --auth-mode login
```

The blob URI used by the app is:

```
https://<STORAGE>.blob.core.windows.net/<CONTAINER>/keys.xml
```

You don't need to create `keys.xml` — Data Protection writes it on first run.

### 2.3 Key Vault + KEK

```powershell
az keyvault create `
  --name $KV `
  --resource-group $RG `
  --location $LOCATION `
  --enable-rbac-authorization true `
  --enable-purge-protection true
```

Because the vault uses RBAC (not access policies), your own user account starts with **no** data-plane permissions — even if you're a subscription Owner. Grant yourself `Key Vault Crypto Officer` on the vault before creating the key:

```powershell
$ME    = az ad signed-in-user show --query id -o tsv
$KV_ID = az keyvault show --name $KV --resource-group $RG --query id -o tsv

az role assignment create `
  --assignee-object-id $ME `
  --assignee-principal-type User `
  --role "Key Vault Crypto Officer" `
  --scope $KV_ID
# Wait ~30s for RBAC propagation before the next command.
```

```powershell
az keyvault key create `
  --vault-name $KV `
  --name $KEY_NAME `
  --kty RSA `
  --size 2048 `
  --ops wrapKey unwrapKey
```

The Key Vault key URI used by the app is:

```
https://<KV>.vault.azure.net/keys/<KEY_NAME>
```

(no version suffix — Data Protection always uses the current version)

### 2.4 Enable the App Service system-assigned managed identity

```powershell
az webapp identity assign --name $APP --resource-group $RG
$APP_PRINCIPAL_ID = az webapp identity show --name $APP --resource-group $RG --query principalId -o tsv
```

### 2.5 Grant the managed identity RBAC

The app must be able to **read/write blobs** in the DP container and **wrap/unwrap** with the Key Vault key. The Storage account and Key Vault must use **RBAC** (not access policies) — the commands above did that.

```powershell
$STORAGE_ID = az storage account show --name $STORAGE --resource-group $RG --query id -o tsv
$KV_ID      = az keyvault show       --name $KV      --resource-group $RG --query id -o tsv

# Read/write the DP keys.xml blob
az role assignment create `
  --assignee-object-id $APP_PRINCIPAL_ID `
  --assignee-principal-type ServicePrincipal `
  --role "Storage Blob Data Contributor" `
  --scope $STORAGE_ID

# Wrap/unwrap the DP keys with the KEK
az role assignment create `
  --assignee-object-id $APP_PRINCIPAL_ID `
  --assignee-principal-type ServicePrincipal `
  --role "Key Vault Crypto User" `
  --scope $KV_ID
```

> **Why these roles?** `Storage Blob Data Contributor` is the minimum role that lets `DefaultAzureCredential` write `keys.xml`. `Key Vault Crypto User` allows `wrapKey` / `unwrapKey` without granting management-plane rights. Avoid `Owner` / `Contributor` here.

---

## 3. Apply settings to the App Service

You can do this in the Portal (App Service → Configuration → Application settings) or with the CLI:

```powershell
az webapp config appsettings set --name $APP --resource-group $RG --settings `
  ASPNETCORE_ENVIRONMENT=Production `
  Supabase__Url="https://<ref>.supabase.co" `
  Supabase__PublishableKey="sb_publishable_..." `
  Supabase__SecretKey="sb_secret_..." `
  DataProtection__BlobStorageUri="https://$STORAGE.blob.core.windows.net/$CONTAINER/keys.xml" `
  DataProtection__KeyVaultKeyId="https://$KV.vault.azure.net/keys/$KEY_NAME" `
  Watcher__Enabled=true
```

**Mark sensitive settings as slot settings if you use deployment slots.** Otherwise a swap can accidentally promote test credentials.

After saving, restart the app:

```powershell
az webapp restart --name $APP --resource-group $RG
```

### 3.1 About the Supabase keys

The app uses Supabase's new API keys: `Supabase__PublishableKey` (the `sb_publishable_…` key, used by the per-request client) and `Supabase__SecretKey` (the `sb_secret_…` key, used by the watcher — bypasses RLS).

Why these instead of the legacy JWT-based `anon` / `service_role` keys:

- **Independent rotation.** The new keys can be revoked individually without rotating the JWT secret (which would invalidate every user session).
- **Browser self-protection.** A `sb_secret_…` key sent from a browser is auto-rejected by Supabase with HTTP 401, so accidental leakage in client code fails closed.
- **Multiple secret keys.** You can issue a separate secret key per backend component (watcher vs. future executor) and revoke one without disturbing the other.

**Compatibility note:** when sending a `sb_secret_…` key in the `Authorization: Bearer …` header, its value must exactly match the `apikey` header (otherwise PostgREST rejects it because the value isn't a JWT). The watcher already sets both to the same value, so no change is required.

Find both keys in the Supabase dashboard at [Project → Settings → API Keys](https://supabase.com/dashboard/project/_/settings/api-keys).

### 3.2 Per-environment keys

Issue one publishable + one secret key per environment from the API Keys page (e.g. `azure_prod`, `local_dev`). This way you can revoke a leaked dev key without touching production. The Description field is a good place to record where each key is used (e.g. `polymtkcp.azurewebsites.net`).

---

## 4. Local development

The app reads config from `IConfiguration`, so User Secrets is the cleanest way to keep keys out of the repo. Run from the repo root (or omit `--project PolymtkCp` if you `cd PolymtkCp` first):

```powershell
dotnet user-secrets init --project PolymtkCp   # one-time; idempotent

# REQUIRED — app won't start without these (use the local_dev keys)
dotnet user-secrets set "Supabase:Url" "https://<ref>.supabase.co" --project PolymtkCp
dotnet user-secrets set "Supabase:PublishableKey" "sb_publishable_<local_dev>..." --project PolymtkCp

# OPTIONAL — only if you want the TraderWatcher polling locally
dotnet user-secrets set "Supabase:SecretKey" "sb_secret_<local_dev>..." --project PolymtkCp

# OPTIONAL — only if you want Polymarket-credential ciphertext encrypted by Azure KV in dev.
# Leave unset for normal dev: Data Protection falls back to %LOCALAPPDATA% with no KEK
# and logs a warning. Sufficient for local testing.
# dotnet user-secrets set "DataProtection:BlobStorageUri" "https://<acct>.blob.core.windows.net/dpkeys/keys.xml" --project PolymtkCp
# dotnet user-secrets set "DataProtection:KeyVaultKeyId"  "https://<vault>.vault.azure.net/keys/dp-kek"          --project PolymtkCp
```

Run:

```powershell
dotnet run --project PolymtkCp/PolymtkCp.csproj
```

Behavior when each key is missing in `Development`:

| Setting | If missing |
|---|---|
| `Supabase:Url` | Throws at startup |
| `Supabase:PublishableKey` | Throws at startup |
| `Supabase:SecretKey` | App starts; watcher disabled (`TraderWatcher disabled` log line) |
| `DataProtection:*` | App starts; DP keys persist to `%LOCALAPPDATA%`, no KEK; warning logged. Encrypted secrets only readable on this machine — fine for dev |
| `Polygon:RpcUrl` | Defaults to `https://1rpc.io/matic` |

Environment-variable alternative (e.g. for `launchSettings.json` or shell sessions): replace `:` with `__` — `Supabase__PublishableKey`, etc.

> **Heads up:** if you save Polymarket credentials with the file-system DP fallback and later switch to Azure Blob/KV, the existing ciphertext won't be readable (different key ring). Re-save credentials after switching.

---

## 5. Apply Supabase migrations

The app does **not** run migrations on startup. Push them from your workstation or CI:

```bash
supabase link --project-ref <ref>     # one-time
supabase db push
```

Phase 1 schema is `supabase/migrations/20260418060000_initial_schema.sql`; phase-2 credential storage is `supabase/migrations/20260420142640_follower_secrets.sql`.

---

## 6. GitHub Actions deployment

The deployment workflow lives at [`.github/workflows/master_polymtkcp.yml`](.github/workflows/master_polymtkcp.yml). It was generated by Azure Portal's "Deployment Center" wizard and uses **OIDC federated credentials** (no client secrets stored in GitHub) to deploy from the `master` branch to the `polymtkcp` App Service.

### 6.1 What the wizard already created for you

When you connected the App Service to GitHub via the Portal, Azure automatically:

- Created an Entra ID app registration + service principal
- Added a federated credential trusting `repo:<your-repo>:ref:refs/heads/master`
- Granted the SP `Website Contributor` on the App Service
- Wrote three repo secrets (the long IDs in the workflow):
  - `AZUREAPPSERVICE_CLIENTID_…`
  - `AZUREAPPSERVICE_TENANTID_…`
  - `AZUREAPPSERVICE_SUBSCRIPTIONID_…`

You don't need to recreate any of this. **Do not commit any Supabase keys to GitHub** — they live only in App Service settings (section 3).

### 6.2 Trigger

```yaml
on:
  push:
    branches: [master]
  workflow_dispatch:
```

Push to `master` → build → publish → deploy to the `Production` slot of `polymtkcp`. You can also trigger manually from the Actions tab.

### 6.3 Re-creating the workflow from scratch

If you ever need to wire it up again (new repo, lost the federated credential, etc.):

1. Azure Portal → App Service `polymtkcp` → **Deployment Center** → Source: **GitHub** → pick the org / repo / `master` branch → **Save**
2. Azure rewrites `.github/workflows/master_polymtkcp.yml`, the federated credential, and the three secrets

Manual CLI alternative is in [git history](.github/workflows/) if needed; the Portal flow is the supported path.

---

## 7. Verification

1. **App Service log stream** (`az webapp log tail --name $APP --resource-group $RG`) on first request should show **no** `[startup] WARNING: Data Protection running in DEV fallback mode` line. If you see it, the two `DataProtection__*` settings aren't visible to the app.
2. **Storage:** after first credential save, `dpkeys/keys.xml` should appear in the container.
3. **Key Vault:** check the `dp-kek` key shows recent `wrapKey` / `unwrapKey` operations under Monitoring → Metrics.
4. **App:** sign in, go to `/Account/Profile`, save fake credentials, then run the SQL `select version, is_active, updated_at from follower_secrets where follower_id = auth.uid();` in Supabase SQL editor — you should see one active row per follower with `ciphertext` populated.
5. **Watcher startup line** should read `TraderWatcher started …` and **not** `TraderWatcher disabled (...)`.

---

## 8. Operational notes

- **Key Vault KEK rotation** is manual (`az keyvault key rotate --vault-name $KV --name $KEY_NAME` or by creating a new key version). Old DP keys re-wrap automatically at next write. Existing `follower_secrets` ciphertext is not affected.
- **Data Protection key ring** auto-rotates every ~90 days; old keys remain in the ring as decrypt-only. Do not delete `keys.xml`.
- **Disaster recovery:** backups must include both the storage `keys.xml` blob **and** the Key Vault key. Losing either makes every existing `follower_secrets.ciphertext` permanently unreadable. Followers would need to re-enter their credentials.
- **Slot deploys:** if you add a `staging` slot, give it its **own** storage container (or its own `keys.xml` path) and its own KEK — never share Data Protection state across environments.
- **Secret key rotation:** when you rotate the Supabase secret key, update `Supabase__SecretKey` in App Service and restart. The watcher won't emit during the gap.
