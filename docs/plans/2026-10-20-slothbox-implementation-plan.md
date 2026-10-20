# SlothBox implementation plan

| | |
|---|---|
| Date | 20 Oct 2026 |
| Spec | `docs/specs/2026-10-20-slothbox-design.md`, version 3 |
| Deadline | Mon 26 Oct 2026, 12:29 PM IST |
| Status | Draft, waiting for Amit's approval |

## Rules for every task

- One branch per task, named after its ID (for example `e01-file-gate`). All of Track 0 is one branch, `track-0-contracts`.
- Write the listed tests first. Merge only with green CI and a review by the other agent.
- A task can start once everything in "Depends on" is merged.
- A task that changes the API updates `contracts/openapi.json` in the same pull request. A task that changes behaviour in the spec updates the spec in the same pull request.
- No task is assigned here; Amit decides. "Runs on" says what a task needs:
  - `any`: .NET 10 and Node only
  - `Docker`: Testcontainers
  - `Windows`: a Windows machine
  - `laptop`: Amit's laptop (GPU, models, phone or real folders)

**Verify commands**

| Short | Command |
|---|---|
| `B` | `dotnet build SlothBox.slnx` |
| `T x` | `dotnet test SlothBox.slnx --filter "FullyQualifiedName~x"` (Docker must be running for database tests) |
| `W` | `npm run verify` in `src/SlothBox.Web` (lint, type-check, tests, build) |
| `C` | `docker compose -f docker/compose.yaml config --quiet` |

**Pinned versions** (checked on the registries on 20 Oct 2026; anything not listed is looked up and pinned in T0.1 or T0.7)
- .NET: OllamaSharp 5.5.0; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 (referenced directly by Data); Pgvector.EntityFrameworkCore 0.3.0; Microsoft.EntityFrameworkCore 10.0.13; Microsoft.AspNetCore.OpenApi 10.0.13; Testcontainers.PostgreSql 4.16.0; xunit.v3 4.0.2; Shouldly 4.3.0.
- Web: tailwindcss and @tailwindcss/vite 4.3.3; vite 8.3.4; @vitejs/plugin-react 6.1.2; @fontsource/atkinson-hyperlegible-next and @fontsource/atkinson-hyperlegible-mono 5.3.0.
- Images: `ollama/ollama:0.40.2` (confirmed in T0.10); `pgvector/pgvector:0.8.7-pg17`.

## Track 0: contracts

Merged before Track E and Track U start. Exit check (kickoff Step 3): `B` and `dotnet test SlothBox.slnx` pass, `W` passes, `C` validates. Then commit, tell Amit, and do not push.

| ID | Task | Files | Depends on | Tests first | Verify | Done when | Est | Runs on |
|---|---|---|---|---|---|---|---|---|
| T0.1 | Repo skeleton | `.gitignore` (in the first commit), `global.json`, `SlothBox.slnx`, `Directory.Build.props`, `Directory.Packages.props`, empty projects Core, Ai, Data, Server, App, Tests, Eval | — | none (scaffolding) | `B` | all projects build with warnings as errors; references as in spec 4.6; App sets `EnableWindowsTargeting` so the solution also builds on Linux; versions pinned | 0.5 h | any |
| T0.2 | Core contracts and settings model | `src/SlothBox.Core/` | T0.1 | `Settings`: defaults equal spec 11; JSON round trip | `T Settings` | enums, records and interfaces of 4.7, including `IKnownFolders`, `IAutoStart`, `ITrayNotifier`; settings records; no implementations | 0.5 h | any |
| T0.3 | Schema, migration, seed | `src/SlothBox.Data/`, `.config/dotnet-tools.json` (dotnet-ef 10.0.13) | T0.2 | `Migration` (Testcontainers, container `slothbox-test-*`): migration applies; HNSW, GIN, filtered unique `jobs.path` and `actions.before` exist; 19 seed categories, 7 of them system | `T Migration` | schema of 6.2 with explicit lowercase snake_case names (the raw SQL in 6.3 uses them unquoted); `Initial` migration committed | 1 h | Docker |
| T0.4 | Server stubs and DTOs | `src/SlothBox.Server/` | T0.2 | `Endpoints`: every route in 8.2 answers 501 ProblemDetails; `/events` answers `text/event-stream` | `T Endpoints` | every 8.2 endpoint has typed request and response DTOs (camelCase, enums as strings); the 8.3 event payloads are named schemas in the document; `/openapi/v1.json` is served | 1 h | any |
| T0.5 | API protections and contract file | `src/SlothBox.Server/`, `contracts/openapi.json`, `tests/SlothBox.Tests/Api/` | T0.4 | spec 13.3 cases 19 and 22 | `T Api` | Host check (400), `X-SlothBox` (403), no CORS, nothing but the phone routes on 5081; `contracts/openapi.json` committed and equal to `/openapi/v1.json` | 0.5 h | any |
| T0.6 | App shell | `src/SlothBox.App/` | T0.4 | none (manual) | `B`; run App and open `http://127.0.0.1:5080/openapi/v1.json` | App hosts Server in-process; tray icon with Exit; `--headless` works | 0.5 h | Windows |
| T0.7 | Web skeleton, tokens, fonts | `src/SlothBox.Web/` | T0.1 | smoke render test | `W` | Vite, React, TypeScript, Tailwind v4; `tokens.css` from 7.2; static Fontsource 400 and 700; ESLint and Vitest; the build writes to `src/SlothBox.Server/wwwroot` | 0.5 h | any |
| T0.8 | API types and MSW handlers | `src/SlothBox.Web/src/api/`, `src/SlothBox.Web/src/mocks/` | T0.5, T0.7 | every operation in `contracts/openapi.json` has an MSW handler | `W` | `gen:api` builds the types from `contracts/openapi.json` before dev, build and type-check; MSW handlers with sample data for every endpoint; an SSE mock helper for tests | 1 h | any |
| T0.9 | Compose, CI, agent docs | `docker/compose.yaml`, `docker/.env.example`, `.github/workflows/ci.yml`, `AGENTS.md`, `CLAUDE.md`, `README.md` | T0.1 | none | `C` | compose as in spec 4.4; CI jobs as in 14.1; `AGENTS.md` holds the hard rules (allowing `slothbox-test-*` containers and Ryuk), the spec link, module boundaries, and the branch and PR process; `CLAUDE.md` is the single line `@AGENTS.md`; README skeleton with a "commits after the deadline" section | 0.5 h | any |
| T0.10 | Models on `ollama/ollama:0.40.2` | spec 5.7 (result only) | T0.9 | none | `docker compose up -d`; pull both models; `ollama run gemma4:e4b "reply ok"`; `ollama ps` | both models run on 0.40.2, GPU share recorded. **Ask Amit before the ~7 GB model download.** If either fails, propose 0.40.6 (a ~9 GB image) | 0.5 h + download | laptop |

**Parallel:** after T0.1, start T0.2, T0.7 and T0.9 together. After T0.2, start T0.3 and T0.4 together. T0.10 does not block the Track 0 merge; it blocks only E7.

## Track E: engine

| ID | Task | Files | Depends on | Tests first | Verify | Done when | Est | Runs on |
|---|---|---|---|---|---|---|---|---|
| E1 | File gate | `src/SlothBox.Core/Gate/` | Track 0 | 13.3 case 8; temp, hidden and system files skipped; 3 s stable plus `FileShare.None`; baseline skip; SHA-256 | `T Gate` | `IFileGate` returns Ready, NotReady, Skip or Duplicate as in 5.1 step 2 | 0.5 h | any |
| E2 | Persistent job queue | `src/SlothBox.Data/Queue/` | Track 0 | case 14; on restart, `processing` returns to `queued` | `T Queue` | `IJobQueue` dedupes through the filtered unique index | 0.5 h | Docker |
| E3 | Safe mover, action log, crash recovery | `src/SlothBox.Core/Moving/`, `src/SlothBox.Data/Actions/` | Track 0 | cases 12 and 13; every row of 6.5; "no log, no move" | `T SafeMove`, `T Recovery` | 6.4 and 6.5 work; refiles write the `before` snapshot | 1 h | Docker |
| E4 | Undo | `src/SlothBox.Core/Undo/`, `src/SlothBox.Data/` | E3 | cases 10, 11, 18 and 20 | `T Undo` | undo by file, last and since, newest first, as in 6.6 | 1 h | Docker |
| E5 | Content readers | `src/SlothBox.Core/Reading/`, `tests/fixtures/public/` | Track 0 | resize and EXIF rotation; PDF and DOCX text; scanned PDF; password-protected PDF and DOCX; HEIC; TXT, MD and CSV | `T Reader` | `IContentReader` as in 5.1 step 3; public fixtures only | 1 h | any |
| E6 | Names, routing, validation | `src/SlothBox.Core/Naming/`, `src/SlothBox.Core/Rules/` | Track 0 | cases 1 to 7 | `T Naming`, `T Rules` | `INameBuilder`, extension routing (5.3), Needs review rules and corrections (5.2) | 0.5 h | any |
| E7 | Gemma analyzer | `src/SlothBox.Ai/Analyze/` | Track 0, T0.10 | with a fake HTTP handler: base64 image, `format` as an object, `think: false`, `num_ctx` 8192, `keep_alive` 30m, temperature 0; one retry after a timeout; parsing and the empty-string fallback | `T Analyzer`; one real call on the laptop | tests pass; the real call returns schema-valid JSON; nullable support recorded in 5.4 | 1 h | any (real call: laptop) |
| E8 | Embedder | `src/SlothBox.Ai/Embed/` | Track 0 | both prefixes, `title: none`, `num_gpu: 0`, length 768 | `T Embedder` | `IEmbedder` passes; if OllamaSharp's `EmbedRequest` cannot carry `num_gpu`, send that call over plain HTTP | 0.5 h | any |
| E9 | Pipeline and worker | `src/SlothBox.Core/Pipeline/`, `src/SlothBox.Server/Worker/` | E1 to E8 | with a fake analyzer and embedder in temp folders: receipt filed with a ledger row; each review reason; duplicate; copy-only | `T Pipeline` | steps 2 to 6 run for each job; pause and resume; SSE events published | 1 h | Docker |
| E10 | Phone upload server | `src/SlothBox.Server/Phone/` | Track 0 | 401 for a bad token, 413 over 50 MB, 415 by magic bytes, generated names in `Inbox`, nothing else on 5081 | `T Phone` | uploads land in `<output>\Inbox` and are queued | 0.5 h | any |
| E11 | Feed, file and review endpoints | `src/SlothBox.Server/Endpoints/` | E9 | API tests for status, pause and resume, activity cursor, laziness stats, files, thumb and preview, open and reveal, undo, refile, review, `/events` | `T Api` | these endpoints return real data instead of 501 | 1.5 h | Docker |
| E12 | Folder rules | `src/SlothBox.Core/Folders/` | Track 0 | case 16 and every rule in 4.5, with a fake `IPlatformPaths` | `T FolderRules` | `IFolderRules` | 0.5 h | any |
| E13 | Watchers, baselines, sweep, tidy | `src/SlothBox.Server/Watching/`, `src/SlothBox.Data/`, folder endpoints | E9, E12 | case 17; watcher and sweep give one job; missing folder; a tidy batch can be undone | `T Watching` | `/folders`, `/folders/check` and tidy work | 1.5 h | Docker |
| E14 | Screenshot, document and other paths | `src/SlothBox.Core/` | E9 | screenshot names use the file's creation date; Office files become documents by name; installers, archives, media and misc routing | `T Routing` | the 5.3 table works end to end in pipeline tests | 0.5 h | any |
| E15 | Hybrid search | `src/SlothBox.Data/Search/`, search and rebuild endpoints | E8, E9 | cases 15 and 21 | `T Search` | `/search` hits carry `matchedWords` and `matchedMeaning`; `ef_search` 100; rebuild of missing vectors works | 1 h | Docker |
| E16 | Categories and receipts API | `src/SlothBox.Data/`, `src/SlothBox.Server/Endpoints/` | E9 | A22 rules; monthly totals per currency; CSV export | `T Categories`, `T Receipts` | `/categories`, `/receipts` and `/receipts/export.csv` work; the prompt uses the live category list | 1 h | Docker |
| E17 | Setup, settings, phone and ignored API | `src/SlothBox.Server/Endpoints/` | E11, E13 | setup state and complete; settings get and put; phone URL, QR SVG and rotate; ignored list and allow | `T Setup`, `T Settings`, `T Ignored` | no 501 left; the Database check reports a missing `SLOTHBOX_DB_PASSWORD` | 1 h | any |
| E18 | Windows pieces in App | `src/SlothBox.App/` | T0.6, E17 | none (manual checklist) | `B`; run App: tray menu, start at login, folder dialog in front of the browser, a second launch opens the dashboard | tray (7.8), Run key, mutex, `FolderBrowserDialog`, known folders, notifications, `--headless` | 1 h | Windows |
| E19 | Eval runner and day-one rule | `tools/SlothBox.Eval/`, spec 5.7 | E7, E9, T0.10 | none | `dotnet run --project tools/SlothBox.Eval` over `tests/fixtures/private` | accuracy and seconds-per-file table; `ollama ps` GPU share; model decision recorded in spec 5.7 | 1 h | laptop |

**Parallel:** right after Track 0, start E1, E2, E3, E5, E6, E8, E10 and E12 together (E7 once T0.10 is done). E4 follows E3. E9 follows E1 to E8. Then E11, E13, E14, E15 and E16 run together. E17 follows E11 and E13; E18 follows E17. E19 can start after E9.

## Track U: UI

Starts right after Track 0, against `contracts/openapi.json` and the MSW handlers from T0.8.

| ID | Task | Files | Depends on | Tests first | Verify | Done when | Est | Runs on |
|---|---|---|---|---|---|---|---|---|
| U1 | Shell | `src/SlothBox.Web/src/app/`, `src/SlothBox.Web/src/components/`, `src/SlothBox.Web/src/api/` | Track 0 | 7.10 test 3 (a `status.changed` event with Gemma offline shows the banner); the sidebar collapses under 1024 px; a success toast hides after 6 s | `W` | layout, sidebar, status block, banners, toasts, query client sending `X-SlothBox`, SSE hook | 1.5 h | any |
| U2 | Phone page | `src/SlothBox.Web/phone/` (built to `src/SlothBox.Server/wwwroot/phone/`) | Track 0 | one upload at a time with progress; the copy for 401, 413 and 415; the `accept` and `capture` attributes | `W` | 7.9: plain HTML, CSS and a small script; system font; tokens from 7.2 | 0.5 h | any |
| U3 | Today: sentence meter and hanging bars | `src/SlothBox.Web/src/pages/Today/`, `HangingBars` | U1 | 7.10 tests 5 and 6 | `W` | 7.4.2 meter and bars | 1 h | any |
| U4 | Today: activity feed and undo | `src/SlothBox.Web/src/pages/Today/`, `ActivityRow` | U1 | 7.10 test 2; the "3 new files" pill; Undo since confirm, then toast | `W` | 7.4.2 feed, empty state, Undo since | 1 h | any |
| U5 | Needs review | `src/SlothBox.Web/src/pages/Review/` | U1 | 7.10 test 1; File it disabled until the missing fields are filled; Skip | `W` | 7.4.3, keyboard-first | 1 h | any |
| U6 | File drawer | `src/SlothBox.Web/src/components/FileDrawer` | U1 | `?file=` deep link; `Esc` returns focus; Change refiles | `W` | 7.4.9 | 0.5 h | any |
| U7 | Search | `src/SlothBox.Web/src/pages/Search/` | U1 | 400 ms debounce; the three match labels; empty-box and no-result copy | `W` | 7.4.4 | 1 h | any |
| U8 | Receipts | `src/SlothBox.Web/src/pages/Receipts/` | U1 | `en-IN` money (₹1,23,456.00); totals per currency; amounts in the sans face | `W` | 7.4.5 | 1 h | any |
| U9 | First-run setup | `src/SlothBox.Web/src/pages/Setup/` | U1 | step order; Finish only when all checks pass; checks every 10 s; Finish anyway | `W` | 7.4.1 | 1 h | any |
| U10 | Folders | `src/SlothBox.Web/src/pages/Folders/`, `FolderRow` | U1 | 7.10 test 4; tidy progress; missing folder | `W` | 7.4.6, with the phone block | 1 h | any |
| U11 | Categories | `src/SlothBox.Web/src/pages/Categories/` | U1 | A22: folder name locked once files exist; remove disabled unless empty | `W` | 7.4.7 | 0.5 h | any |
| U12 | Settings and theme | `src/SlothBox.Web/src/pages/Settings/`, `index.html` | U1 | System, Light and Dark, with a live system listener | `W` | 7.4.8 | 0.5 h | any |

**Parallel:** U1 and U2 start together right after Track 0. U3 to U12 run together after U1.

## Finish

| ID | Task | Files | Depends on | Tests first | Verify | Done when | Est | Runs on |
|---|---|---|---|---|---|---|---|---|
| F1 | UI against the real API | `src/SlothBox.Web/`, `src/SlothBox.Server/` | E11, E13, E15, E16, E17, U3 to U12 | none | run App with the built UI; click through every page | every page works against the real API; MSW only in tests and `npm run dev:mock` | 1 h | laptop |
| F2 | Smoke checklist and release build | `README.md` | F1, E18, E19 | none | spec 13.5; `dotnet publish src/SlothBox.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` | all 10 smoke items pass; the single exe runs | 1 h | laptop |

## Milestones

- **M1, first real demo.** Needs E1 to E10, U2 and T0.10. On the laptop, a receipt photo uploaded from the phone page is renamed `{date}_{Vendor}_{amount}{CUR}.jpg`, filed under `Receipts\<category>`, and logged (a `receipts` row and a `done` action) within seconds. Target: Thu 22 Oct.
- **M2, real folders.** Needs E13 and E18. **Only when Amit says.** SlothBox watches Amit's real Downloads and Screenshots, so the laziness meter collects real numbers for the post (spec 17). Target: Fri 23 Oct.
- **M3, demo-ready.** Needs F2. Smoke checklist passes, then the video is recorded. Target: Sun 25 Oct.

## Totals and critical path

- Track 0 about 6.5 h, Track E about 16 h, Track U about 10.5 h, Finish 2 h: about 35 h in total.
- Section 18 estimated about 24 h. Track 0 grew (the Server project, MSW for every endpoint, CI and docs), and the real API endpoints and the integration pass were not costed there.
- Critical path: Track 0, then E1 to E10, M1, then E11, E13 and E15, then F1 and F2.
- Track U has about 5.5 h less work than Track E. When Track U is done, its agent can take Track E tasks marked `any`.
