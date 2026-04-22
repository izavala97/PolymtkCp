# Deployment

Azure App Service (Linux, .NET 10) + Supabase (Auth + Postgres) + GitHub Actions.
Production URL: <https://polymtkcp.azurewebsites.net>.

## Required secrets (App Service → Configuration)

| Setting | Notes |
|---|---|
| `Supabase__Url` | `https://<ref>.supabase.co` |
| `Supabase__AnonKey` | Public anon key |
| `Supabase__ServiceRoleKey` | Server-side only — needed by `TraderWatcher` and `OrderExecutor` to bypass RLS. **Never** ship to the browser |
| `DataProtection__BlobUri` | `https://<storage>.blob.core.windows.net/<container>/keys.xml` (keyring) |
| `DataProtection__KeyVaultKekUri` | `https://<vault>.vault.azure.net/keys/<keyName>/<version>` (master key wrapping the keyring) |
| `Polygon__RpcUrl` *(optional)* | Defaults to `https://1rpc.io/matic` |
| `Watcher__Enabled` *(optional)* | `false` to disable polling in this slot |
| `Executor__Enabled` *(optional)* | `false` to disable order submission in this slot |

The App Service uses a **system-assigned managed identity** with these RBAC roles:

- **Storage Blob Data Contributor** on the Data Protection container (read/write keyring).
- **Key Vault Crypto User** on the KEK key (wrap/unwrap the keyring).

In dev (no `DataProtection__BlobUri`), keys fall back to `%LOCALAPPDATA%/.../DataProtection-Keys` and the app logs a warning. **Never run prod without both blob + KEK configured** — local keys vanish on restart and every encrypted credential row becomes unreadable.

## Supabase

1. Create a project; copy URL + anon + service-role keys.
2. `npx supabase link --project-ref <ref>` then `npx supabase db push` to apply `supabase/migrations/`.
3. **Auth → URL configuration**: set Site URL to the App Service URL and add it to redirect allow-list (needed for password reset).

## GitHub Actions

`.github/workflows/*.yml` runs on push to `master`:

1. `dotnet restore` → `dotnet build -c Release` → `dotnet test -c Release --no-build` (gates the deploy on 28/28 tests).
2. `dotnet publish PolymtkCp/PolymtkCp.csproj -c Release` → zip → `azure/webapps-deploy@v3` using a publish profile stored in `AZURE_WEBAPP_PUBLISH_PROFILE`.

To rotate the publish profile: App Service → *Get publish profile* → paste into the GitHub repo secret.

## Verification after deploy

```bash
curl -I https://polymtkcp.azurewebsites.net
# Expect: HSTS, X-Content-Type-Options, X-Frame-Options DENY,
#         Referrer-Policy, Permissions-Policy, Content-Security-Policy
```

Sign in, open `/Account/Profile`, save L2 credentials + private key, flip a CopyPlan to **Real**, then watch App Service log stream for `OrderExecutor` lines.

## Operations

- **Logs**: App Service → Log stream. `TraderWatcher` and `OrderExecutor` log structured ticks.
- **Failed orders**: surface in `/Traders/{id}` execution history with the failure `reason`.
- **Disable trading fast**: set `Executor__Enabled=false`, save, restart. Pending rows stay in the queue until re-enabled.
- **Vulnerability check**: `dotnet list package --vulnerable --include-transitive` (run for both projects). `Microsoft.AspNetCore.DataProtection` is pinned to 10.0.5 in `PolymtkCp.csproj` to override the vulnerable transitive 8.0.16 — keep it pinned until the Azure Data Protection NuGets update their floor.
- **Rotating the KEK**: create a new key version in Key Vault and update `DataProtection__KeyVaultKekUri`. Existing keyring entries continue to decrypt (Data Protection picks the new version for new writes).
