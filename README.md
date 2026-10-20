# SlothBox

A local file butler for Windows. The design combines watched folders, phone
uploads, a receipt ledger, reversible filing and keyword/meaning search.

This repository currently contains the Track 0 foundation. Product endpoints
are intentionally unimplemented and return HTTP 501. File watching, filing,
model calls and real-folder access are not enabled.

## Design and plan

- [Design spec](docs/specs/2026-10-20-slothbox-design.md)
- [Implementation plan](docs/plans/2026-10-20-slothbox-implementation-plan.md)
- [Repository guidance](AGENTS.md)

## Local prerequisites

Windows 10/11, .NET SDK 10.0.400, Node 22.20.0 and Docker Desktop with Linux
containers. Keep Docker's disk image and all package caches on D:.
If the caches are not on D:, set them yourself, then restart the terminal:

```powershell
[Environment]::SetEnvironmentVariable('NUGET_PACKAGES','D:\.nuget\packages','User'); [Environment]::SetEnvironmentVariable('NUGET_HTTP_CACHE_PATH','D:\.nuget\v3-cache','User'); npm config set cache D:\.npm-cache --global
```

Only the owner manages `docker/.env`. Do not read, print or edit it, and never run
plain `docker compose config`. Validate with `docker compose config --quiet`
from `docker/`; no shell password variable is required. The sample
`docker/.env.example` contains only a placeholder
for CI configuration validation. Do not create a repo-local settings file.
Model/image downloads over 1 GB require explicit approval; Track 0 does not
pull or start Ollama. Existing projects' containers must remain untouched.

## Verify

```powershell
dotnet build SlothBox.slnx
dotnet test SlothBox.slnx
npm --prefix src/SlothBox.Web ci
npm --prefix src/SlothBox.Web run verify
docker compose -f docker/compose.yaml config --quiet
```

Database tests require Docker and create only `slothbox-test-*` containers
(plus the Testcontainers reaper). They use synthetic data and the pinned
pgvector image. The CI workflow includes Linux tests, web checks and a Windows
App build; local results do not claim that hosted CI has run.

## Run the foundation

Build the web assets before running the shell:

```powershell
npm --prefix src/SlothBox.Web run build
dotnet run --project src/SlothBox.App
```

The tray has Exit. `--headless` starts the same server without a tray:

```powershell
dotnet run --project src/SlothBox.App -- --headless
```

Dashboard/API: `http://127.0.0.1:5080`. OpenAPI:
`http://127.0.0.1:5080/openapi/v1.json`. The separate phone listener on 5081
exposes only the phone route stubs. Neither listener starts a file pipeline.

For UI development:

```powershell
npm --prefix src/SlothBox.Web run dev
```

Vite binds to loopback and proxies API and SSE requests to the local server.
Fonts are bundled locally. Web assets are embedded in Server on build.

## API contracts and mocks

After changing Server DTOs, build the solution and refresh the contract:

```powershell
powershell -NoProfile -File tools/export-contract.ps1
```

`npm run gen:api` generates TypeScript from `contracts/openapi.json`; it also
runs before dev, build and type-check. The API test compares the saved document
to the live server document. `src/SlothBox.Web/src/mocks/server.ts` exports a
test server with explicit handlers for all operations, synthetic response
fixtures and a finite SSE stream. Start it in tests with
`server.listen({ onUnhandledFrame: 'error' })`, reset handlers after each test,
and close it after the suite. Mocks do not run in the production application.

## Commits after the deadline

The challenge deadline is Mon 26 Oct 2026, 12:29 PM IST. The owner will list any
post-deadline commits here before publication.
