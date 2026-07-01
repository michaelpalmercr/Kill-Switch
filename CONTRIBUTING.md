# Contributing to KillSwitch

Thanks for helping! KillSwitch is a Windows‑only C# / .NET 8 WinForms project.

## Getting started

```powershell
dotnet build -c Release
```

Run the built exe **as Administrator** (it manages the firewall and other processes).

## Guidelines

- **One focused change per PR.** Describe what it does and why.
- **Match the surrounding style** — this codebase favors small, self‑contained classes with a short
  summary comment at the top.
- **Don't add telemetry or any network call** that isn't a user‑initiated, opt‑in AI request.
- **Never add silent credential/keystroke capture.** PRs that do will be closed. The vault and extension
  only ever store what the user explicitly chooses to save.
- Test risky/pure logic where practical (vault crypto round‑trip, path matching, settings serialization).
- Advanced/destructive features must stay behind explicit confirmation and (going forward) Expert mode.

## Reporting security issues

See [`SECURITY.md`](SECURITY.md) — please use private advisories, not public issues.
