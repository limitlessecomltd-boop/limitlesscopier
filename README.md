# Limitless Trade Copier

Monorepo for Limitless Trade Copier: a Windows desktop trade-copier app
that protects funded prop trading accounts.

## Repository layout

```
.
├── LTC.App           # WPF customer desktop app (build target: Windows)
├── LTC.AdminApp      # Operator GUI for minting licenses
├── LTC.Core          # Engine, routing, prop calculations
├── LTC.Persistence   # SQLite repos, DPAPI
├── LTC.Server        # ASP.NET Core 8 licensing server (deploys to Railway)
├── LTC.KeyGen        # Admin CLI for offline license minting (legacy)
├── LTC.ConsoleTest   # Internal manual testing
├── LTC.Tests         # xUnit test suite
├── lib/              # Vendored mtapi.mt5.dll
├── installer/        # Inno Setup configs
├── obfuscation/      # Obfuscar configs
├── Dockerfile        # For Railway deploy of LTC.Server
└── railway.json      # Railway build/deploy config
```

## Where things run

| Component        | Host             | URL                              |
| ---------------- | ---------------- | -------------------------------- |
| Customer app     | User's Windows PC| (desktop binary)                 |
| Licensing API    | Railway          | `https://api.limitlesscopier.com`|
| Landing site     | Vercel           | `https://limitlesscopier.com`    |
| Source of truth  | This GitHub repo | private                          |

## Licensing server deploy flow

Railway watches this repo. **Pushing to `main` auto-deploys.**

1. Make changes to `LTC.Server/...`
2. `git add . && git commit -m "..." && git push`
3. Railway builds via `Dockerfile`, deploys, runs health check
4. Live in ~2 minutes

The Dockerfile only copies `LTC.Server/`, so changes to client code
(LTC.App etc.) DON'T trigger a server rebuild.

## Secrets

NEVER committed. All injected at runtime via Railway environment variables:

| Variable                  | What                                          | Required? |
| ------------------------- | --------------------------------------------- | --------- |
| `Signing__PrivateKeyPath` | File path to the Ed25519 signing key          | Yes       |
| `Admin__BearerToken`      | Random string for admin endpoint auth         | Yes       |
| `Database__Path`          | SQLite path (default `/data/licenses.db`)     | No        |
| `Smtp__Host` etc.         | Optional outgoing-mail alerts                 | No        |

The signing key itself is uploaded as a **Railway Secret File** to
`/run/secrets/keygen-private.key` — never in the repo, never in env vars.

## Customer app build (local Windows only)

```powershell
cd LTC.App
dotnet build
dotnet run
```

For obfuscated release build, see `build-obfuscated.ps1`.

## Tests

```bash
dotnet test LTC.Tests
```
