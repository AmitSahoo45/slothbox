# SlothBox

A local file butler for Windows. SlothBox watches the folders you choose, such as Downloads and Screenshots. A local
AI model (Gemma, running in Docker on your own PC) reads each new file. SlothBox then gives the file a clear name and
files it in a sorted folder. Receipts go into a monthly ledger, every move can be undone, and you can search your files
by words or by meaning. A small upload page lets your phone send photos over your home Wi-Fi. Files never leave the PC.

- [Design spec](docs/specs/2026-10-20-slothbox-design.md)
- [Implementation plan](docs/plans/2026-10-20-slothbox-implementation-plan.md)
- [Repository guidance](AGENTS.md)

## What you need

- Windows 10 or 11, 64-bit.
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) with Linux containers.
- An NVIDIA GPU with at least 8 GB of video memory is recommended. Without one, Gemma runs on the CPU and is much
  slower.
- About 17 GB of free disk space for Docker: about 10 GB of images and 7 GB of models.

To build the exe yourself you also need the .NET SDK 10.0.400 and Node 22.20.0.

## One-time setup

Run these in PowerShell from the repository folder.

1. Choose a database password and save it for your Windows user. Docker and SlothBox both read it from this variable.
   Then open a new PowerShell window so the variable is visible there.

   ```powershell
   [Environment]::SetEnvironmentVariable('SLOTHBOX_DB_PASSWORD','<choose a password>','User')
   ```

2. Start the database and Ollama:

   ```powershell
   docker compose -f docker/compose.yaml up -d
   ```

   This starts two containers, `slothbox-postgres` (port 5433) and `slothbox-ollama` (port 11436). Both listen on
   127.0.0.1 only and start again with Docker Desktop. The compose file gives Ollama the NVIDIA GPU. On a PC without
   an NVIDIA GPU, delete the `deploy:` block under `ollama` in `docker/compose.yaml` first.

3. Download the two models (about 7 GB) and check that Gemma answers:

   ```powershell
   docker exec slothbox-ollama ollama pull gemma4:e4b; docker exec slothbox-ollama ollama pull embeddinggemma; docker exec slothbox-ollama ollama run gemma4:e4b "reply ok"
   ```

   Then `docker exec slothbox-ollama ollama ps` should show `100% GPU` for `gemma4:e4b`.

4. Optional, for the phone page: allow its port on private networks. Run this once in an administrator PowerShell,
   and make sure Windows treats your home Wi-Fi as a private network.

   ```powershell
   New-NetFirewallRule -DisplayName "SlothBox phone page" -Direction Inbound -Protocol TCP -LocalPort 5081 -Action Allow -Profile Private
   ```

To check the compose file, use `docker compose -f docker/compose.yaml config --quiet`. Plain `docker compose config`
prints the password.

## Build the single exe

```powershell
npm --prefix src/SlothBox.Web ci
npm --prefix src/SlothBox.Web run build
dotnet publish src/SlothBox.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The result is `src\SlothBox.App\bin\Release\net10.0-windows\win-x64\publish\SlothBox.exe`. It contains the .NET
runtime, the dashboard and everything else SlothBox needs, so it is the only file to copy, and the PC that runs it
needs no .NET install. The other files in that folder are not needed to run it. Build the UI before publishing: the
dashboard is built into the exe.

## Run

1. Start Docker Desktop and wait until `slothbox-postgres` and `slothbox-ollama` are running.
2. Double-click `SlothBox.exe`. A tray icon appears; it may be under the ^ arrow.
3. Double-click the tray icon, or right-click it and choose **Open dashboard**. The dashboard is at
   http://127.0.0.1:5080/.
4. The first time, the dashboard shows a three-step setup: the output folder for sorted files, the folders to watch,
   and a check that Gemma and the database are ready. Nothing moves until setup is finished.

Good to know:

- Files that are already in a folder when you add it stay where they are. **Tidy them now** on the Folders page files
  them, and **Undo since…** on Today puts them back.
- The tray menu has Open dashboard, Pause or Resume, Undo last, Start at login and Exit.
- Starting the exe again while it runs just opens the dashboard.
- `SlothBox.exe --headless` runs without the tray icon. Folder paths are then typed instead of picked.
- Settings are in `%LOCALAPPDATA%\SlothBox\settings.json`. Watched folders, categories and the file history are in
  the database.
- If Docker stops, the dashboard shows a banner and nothing moves. SlothBox carries on by itself once Docker is back.
- The phone page's QR code and link are on the Folders page. The phone must be on the same Wi-Fi.

## Development

```powershell
npm --prefix src/SlothBox.Web run build
dotnet run --project src/SlothBox.App
```

`dotnet run --project src/SlothBox.App -- --headless` starts the same server without a tray. For live UI changes,
run `npm --prefix src/SlothBox.Web run dev`; Vite binds to loopback and proxies API and SSE requests to the local
server. Fonts are bundled locally. Web assets are embedded in Server on build. OpenAPI:
`http://127.0.0.1:5080/openapi/v1.json`.

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
