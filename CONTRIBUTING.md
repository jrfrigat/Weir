# Contributing to Weir

Thanks for your interest in improving Weir! This guide covers what you need to get a change merged.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- A recent IDE (Visual Studio 2022/2026, Rider, or VS Code with the C#/Razor extensions)
- Docker (optional, to build/run the container and the compose stack)

## Build and test

The repository builds from a single solution:

```sh
dotnet restore
dotnet build -c Release
dotnet test  -c Release
```

Run the host locally (serves the JSON API and the admin PWA on the same origin):

```sh
dotnet run --project src/Weir.Host
```

## Conventions

Weir keeps a strict, consistent style. Before making a change, read the golden rules in
[CLAUDE.md](CLAUDE.md):

- **XML documentation** on every type and member (public, internal and private).
- **Keyboard-only text**: ASCII in English text, Cyrillic allowed only in Russian docs; no em-dashes,
  arrows, bullets, box-drawing or emoji.
- **Bilingual docs**: every README/prose doc exists in English and Russian (README.md + README.ru.md,
  docs/en + docs/ru). Keep both in sync.
- **Thin hot path**: the data plane streams the reader straight to JSON. No ORM, no per-request
  allocation churn.
- **Never log parameter values** by default (PII-safe); telemetry carries metadata only.

## Workflow

1. Fork the repo and create a short-lived branch off `main` (e.g. `feat/postgres-store`,
   `fix/tvp-binding`).
2. Make your change with tests under `tests/`.
3. Make sure `dotnet build` and `dotnet test` pass and CI is green on your PR.
4. Open a pull request against `main`. Please use
   [Conventional Commits](https://www.conventionalcommits.org/) style for the title
   (`feat(core): ...`, `fix(host): ...`, `docs: ...`). Do not add co-author trailers.

## CI and releasing

Two GitHub Actions workflows drive the pipeline:

- **CI** (`.github/workflows/ci.yml`) - on every push and pull request to `main`/`master`: restore,
  `dotnet build -c Release`, and `dotnet test -c Release`. The runner provides Docker, so the opt-in
  Testcontainers integration tests run (`WEIR_CONTAINER_TESTS=1`).
- **Release** (`.github/workflows/release.yml`) - on a pushed `v*` tag (e.g. `v0.2.0`). It builds and
  tests, then produces two kinds of artifact:

  | Artifact | What | Where |
  | :-- | :-- | :-- |
  | **NuGet packages** | The 9 reusable libraries: `Weir.Contracts`, `Weir.Abstractions`, `Weir.Core`, `Weir.Diagnostics`, `Weir.ControlPlane.Sqlite`, `Weir.ControlPlane.PostgreSql`, `Weir.ControlPlane.SqlServer`, `Weir.Connectors.SqlServer`, `Weir.Connectors.PostgreSql` | NuGet.org, via Trusted Publishing (OIDC) |
  | **Container image** | The runnable app: `Weir.Host` with the Blazor WASM admin bundled as static assets | `ghcr.io/jrfrigat/weir:X.Y.Z` and `:latest` |
  | **GitHub Release** | The tag's release page, with auto-generated notes and the `.nupkg` files attached | `github.com/jrfrigat/weir/releases` |

  The application projects (`Weir.Host`, `Weir.Admin`), the test project and the sample connector are
  marked `IsPackable=false`, so they are never pushed to NuGet - the host ships only as the image. The
  version comes from the tag (`v1.2.3` -> `1.2.3`).

### NuGet Trusted Publishing (one-time setup)

Publishing to NuGet.org uses **Trusted Publishing**: the workflow exchanges a GitHub OIDC token for a
short-lived NuGet API key (`NuGet/login@v1`), so no long-lived key is stored in the repository. Set it
up once:

1. On nuget.org, sign in and open **Account -> Trusted Publishing**, then add a policy for this
   repository. Set the owner (`jrfrigat`), repository (`weir`) and the workflow file (`release.yml`).
   Repeat, or use a glob, so the policy covers every package id the workflow pushes.
2. Add a repository **variable** (not a secret) named `NUGET_USER` set to the nuget.org account/username
   that owns the packages: `gh variable set NUGET_USER --repo jrfrigat/weir --body "<nuget-username>"`.

The container image uses the built-in `GITHUB_TOKEN` (via `packages: write`); it needs no extra setup.

To cut a release: update [CHANGELOG.md](CHANGELOG.md), then push an annotated tag:

```sh
git tag -a v0.2.0 -m "Weir 0.2.0"
git push origin v0.2.0
```

## Reporting bugs and requesting features

Use GitHub issues. For security issues, see [SECURITY.md](SECURITY.md) - please do not open a public
issue.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
