# Repository guidance

Read the [design spec](docs/specs/2026-10-20-slothbox-design.md) and
[implementation plan](docs/plans/2026-10-20-slothbox-implementation-plan.md)
fully before work. The spec is the source of truth.

## Hard rules

1. Follow the spec and plan. If something is wrong, missing or risky, explain why and propose a fix. Never silently work around it.
2. Everything on D: on the local Windows machine. Never install anything to C:. If `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH` or the npm cache does not point to D:, stop and give the owner the PowerShell one-liner in spec Section 15. Do not change environment variables yourself. Existing installed tools may be used.
3. Docker: touch only `slothbox-*` containers and volumes. Test containers use `slothbox-test-*`; the Testcontainers Ryuk reaper is allowed. Never stop, remove or modify resources from other projects, including `rag-*`, `dads-garden`, `breakmyquery-llama32` and `semantic-rag-observability`. Never run `docker system prune`, `docker volume prune` or similar.
4. Never read, move or change files in real Downloads, Screenshots, Pictures, OneDrive or any personal folder. Tests use temporary folders on D: and `tests/fixtures/public` only.
5. No personal data in the repo. Never create `docker/.env`, `settings.json`, logs or database dumps in the repo. Never put anything in `tests/fixtures/private/`.
6. Never change Git state. The owner performs all Git operations. Only `git status`, `git diff`, `git log` and `git show` are allowed. Never run `git init`, `add`, `commit`, `push`, `pull`, `merge`, `rebase`, `reset`, `restore`, `checkout`, `switch`, `stash`, `tag`, `branch`, `worktree`, `config`, `cherry-pick`, `revert`, or any `gh` command. Never create a GitHub repository, branch, pull request or issue.
7. No AI attribution anywhere: no co-author trailers, generated-by lines, or coding-agent tool, product or model names in code, comments, file headers, docs, README or suggested commit messages.
8. Verify before claiming completion. Run the build and tests and report their actual results. Never claim that unrun checks passed.
9. No invented APIs or packages. Use the versions pinned in the plan. Verify anything unpinned in the official registry or documentation before using it.
10. Ask before large downloads. Tell the owner before pulling any model or image over 1 GB.
11. Give commands the owner must run as PowerShell one-liners.

## Scope and handoff

- Current authorisation: T0.1 through T0.9 only, in the kickoff order.
- Write each task's listed tests first. Do not start T0.10 or any Track E or Track U task.
- Run App only on Windows; WSL may cross-build it but cannot run WinForms.
- All changes remain uncommitted. The other agent reviews the diff, and the owner commits.
- Parallel implementation requires separate worktrees created by the owner.
- Until the repository is on GitHub, green CI means the same checks pass locally.
- Update the API contract and spec in the same reviewable diff when their behaviour changes.

## Module boundaries

- Core: models, interfaces, settings and cross-platform rules/file I/O. No network, database or Windows-only APIs.
- Ai and Data depend on Core. Ai handles model calls; Data handles Postgres, migrations and storage.
- Server depends on Core, Ai and Data. It owns Kestrel, API, SSE, uploads, watchers and worker.
- App depends on Core and Server. It hosts Server in-process and contains Windows-only integrations.
- Web is a static React application generated against `contracts/openapi.json`. It builds into Server's ignored `wwwroot`.
- Tests depend on Core, Ai, Data and Server, never App. Database tests use disposable named containers and synthetic data.
- Eval depends on Core and Ai. Real-file evaluation is outside the current scope.

## Verification

- `dotnet build SlothBox.slnx`
- `dotnet test SlothBox.slnx` (Docker running)
- `npm --prefix src/SlothBox.Web run verify`
- `docker compose -f docker/compose.yaml config --quiet`

Product settings, watchers and model calls are not enabled by the Track 0 shell.

## Owner-supplied Compose configuration

The owner supplies `docker/.env`, which is ignored. Do not read, print or edit it.
Validate with `docker compose config --quiet` from `docker/`; never use plain
`config`, which prints secrets. Never echo secret values in commands, logs or reports.
Do not require a shell environment variable for this Compose validation.
