# Security Policy

KillSwitch is a local, admin‑level security tool. We take reports seriously.

## Reporting a vulnerability

Please report privately using **GitHub Security Advisories** ("Report a vulnerability" on the repo's
Security tab) rather than a public issue. Include steps to reproduce and the affected version.

You can expect an initial response within a few days.

## In scope (highest interest)

- **Password vault** — the AES‑256‑GCM / PBKDF2 implementation, key handling, and the vault file format.
- **Browser extension** — the crypto, storage, and the save‑prompt/consent flow.
- **HTTPS inspection (MITM)** — root‑CA install/cleanup and proxy restoration.
- **Privilege / removal engine** — anything that could delete or damage unintended targets.
- Any path where data leaves the machine unexpectedly.

## Design commitments

- No telemetry. Nothing leaves the machine except the app/IP/domain a user explicitly sends to their chosen
  AI provider.
- No silent capture of credentials or keystrokes — ever. The vault and extension store only what the user
  chooses to save.
- Connectivity fails open: the app restores the network (and removes any MITM CA) on exit.
