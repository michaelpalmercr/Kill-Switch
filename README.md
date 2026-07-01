# KillSwitch

**An all‑in‑one, local Windows control & privacy suite** — cut your internet, see and limit exactly what
every app talks to, and get a plain‑English **AI report on any app, IP, or website** so you can decide what
to trust. Everything runs on your machine; nothing is uploaded unless *you* ask the AI to analyze something.

> ⚠️ **Power tool.** KillSwitch runs as Administrator and can block, close, lock, and remove programs. It's
> built for people who want full control of their own PC. Read [Safety](#safety) before using the advanced
> features.

## Features

- **Internet kill switch** — instant, on a schedule, or via a global hotkey. Firewall mode (reversible,
  loopback‑safe) or hard adapter‑down mode. Fail‑safe: connectivity is always restored on exit.
- **Per‑app network control** — see which apps use the internet, how much (live + per‑hour graphs), and the
  domains/IPs they reach (DNS / TLS SNI / HTTP). Block per app, per destination IP, or run a default‑deny
  **allowlist ("ask me")** mode. Mark apps **safe** to bypass the cut.
- **AI copilot** *(opt‑in, bring your own key)* — a full report on any **app / IP / domain**: what it is, who
  owns it, why it phones home, what it reads from you, and a block/allow recommendation. Works with **Claude**
  or **Gemini**.
- **Traffic inspection** — optional HTTPS inspection to see what's actually being sent (off by default).
- **Process control** — force‑close a program and **lock it from running** until you allow it again.
- **Program removal** — two‑tier: run the clean uninstaller, or **force‑remove** with an **impact preview**
  (what breaks, which programs/services depend on it) and **reversible quarantine**. Force‑delete stubborn
  files/folders.
- **Password vault** — your own **AES‑256‑GCM** encrypted vault (master password never stored), with a
  `Ctrl+Alt+L` quick‑save for any login. Plus a companion **browser extension** with a consent‑based
  "Save this login?" prompt. See [`browser-extension/`](browser-extension/).

## Requirements

- Windows 10/11, 64‑bit. Runs **as Administrator** (one UAC prompt at launch).
- .NET 8 SDK to build. The published build is self‑contained (no .NET needed on the target).

## Build

```powershell
# from this folder
dotnet build -c Release
# portable, self-contained single .exe:
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The published `KillSwitch.exe` lands in `bin\Release\net8.0-windows\win-x64\publish\`.

## Safety

- **Nothing is uploaded** except the specific app/IP/domain you choose to send to Claude/Gemini when you click
  *Analyze*. AI is off until you add your own API key. There is **no telemetry**.
- Advanced actions (force‑remove, force‑delete, blocking the OS, HTTPS inspection) are powerful and can break
  things. Removals are quarantined and restorable; connectivity is restored on exit.
- **KillSwitch does not, and will not, silently capture passwords or keystrokes.** The password vault and the
  browser extension only store what *you* choose to save. That's the line between a password manager and a
  credential harvester, and we stay on the right side of it.

## Roadmap

See [`ROADMAP.md`](ROADMAP.md) — the path from this build to a signed, installable, driver‑backed v2.

## License

GPLv3 — see [`LICENSE`](LICENSE). Free and open source; derivatives stay open.

## Contributing

Issues and PRs welcome — see [`CONTRIBUTING.md`](CONTRIBUTING.md). Security reports: [`SECURITY.md`](SECURITY.md).
