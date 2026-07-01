# KillSwitch — Path to Shippable

**Product shape (decided):** an all‑in‑one, local, Windows PC‑control & privacy suite.
**Model (decided):** free and open‑source.
**Marquee hook:** even inside an "all‑in‑one" story, lead the README/landing with the one thing
nobody else has — **AI reports on any app / IP / website + one‑click act on the verdict.** Breadth gets
people in; the AI copilot is why they tell a friend.

This doc is the sequence from "personal .exe on my Desktop" to "a project a stranger can trust, install,
and contribute to." Effort tags: **S** = hours, **M** = a few days, **L** = weeks, **XL** = months / real cost.

---

## 0. What already exists (baseline)

Kill switch (manual + scheduled) · per‑app firewall control · allowlist / "ask me" · safe apps · block
System / by‑destination‑IP · live traffic + per‑app usage graphs · DNS/SNI/HTTP host attribution · HTTPS
inspection (MITM) · **AI analysis of app/IP/domain (Claude + Gemini)** · force‑close + lock apps (IFEO) ·
two‑tier removal (uninstall + force‑remove) with impact preview + reversible quarantine · force‑delete ·
encrypted password vault (AES‑256‑GCM) + Ctrl+Alt+L quick‑save · browser extension (consent save‑prompt).

Tech: C#/.NET 8 WinForms, self‑contained single‑file, requireAdministrator. Enforcement via `netsh`/Windows
Firewall + IFEO registry. Deploy today = copy exe to Desktop (not shippable).

---

## 1. The three things that decide whether it can ship

These are not features. They are the gate.

| # | Blocker | Why it's fatal if ignored | Fix | Effort |
|---|---------|---------------------------|-----|--------|
| A | **Not code‑signed** | SmartScreen + every AV red‑flags an unsigned admin exe that touches the firewall and other processes. First impression = "malware." | Authenticode sign the exe + installer. See Wave 2. | M (+ cert cost) |
| B | **Destructive features shipped raw** | Force‑uninstall System32, kill‑anything, delete‑on‑reboot will brick strangers' machines and get you classified as a RAT. OSS license covers *legal* liability, not *reputation*. | Gate behind Expert mode + guardrails; drop force‑System32‑removal from the public build. See Wave 1. | M |
| C | **User‑space enforcement + no self‑protection** | `netsh` rules are coarse and a security tool a user can `taskkill` is a toy to reviewers. This is the depth gap vs simplewall/Portmaster/Little Snitch. | WFP kernel driver + self‑protection. See Wave 4. **Hardest item.** | XL |

Everything else is normal open‑source project work. A, B are doable now; C is the long game.

---

## 2. Phased roadmap

### Wave 0 — Make it a project (repo hygiene) — **S–M**
The code lives in a loose folder today. Turn it into something a stranger can read and trust.

- [ ] `git init` the KillSwitch folder; push to a public GitHub repo (separate from TenantOak).
- [ ] **Pick a license.** Recommend **GPLv3** (copyleft — keeps forks open, standard for security tools;
      simplewall is GPLv3). Alternative: **MIT** for max adoption/contribution. *This is a decision.*
- [ ] `README.md` — lead with the AI‑copilot screenshot, then the feature grid, then install/build.
- [ ] `SECURITY.md` — how to report a vuln, what's in scope (vault crypto, MITM, driver).
- [ ] `CONTRIBUTING.md` + issue/PR templates + `CODE_OF_CONDUCT.md`.
- [ ] Screenshots / a 60‑sec demo GIF (the AI report + a block action is the money shot).
- [ ] `.gitignore` (bin/obj/publish), and delete the Desktop‑swap deploy hack from the docs.
- [ ] `CHANGELOG.md` (Keep a Changelog format) + semantic version in the csproj.

**Gate:** repo builds from a clean clone with documented steps.

### Wave 1 — Safe to hand to a stranger (guardrails + stability) — **M**
Keep the power for expert users; ship with the safety on.

- [ ] **Expert mode toggle** (off by default). Force‑remove, force‑delete, delete‑on‑reboot, block‑System,
      MITM all live behind it, with a one‑time "I understand this can break Windows" consent.
- [ ] **Drop force‑System32‑removal from the public build** (or hard‑gate + typed confirm + AI impact
      mandatory). It's the single most machine‑bricking capability.
- [ ] **MITM done right:** strictly opt‑in; install the root CA only with a plain‑language consent screen;
      **auto‑remove the CA and restore the proxy on exit/uninstall** (you already restore the proxy — extend
      to the CA); a persistent "traffic is being decrypted" indicator while active.
- [ ] **Crash + error handling:** global exception handler → friendly dialog + local log file; never leave
      the network cut or the CA installed after a crash (fail‑open on connectivity).
- [ ] **Settings & vault backup/restore** (export/import), so an update or crash can't lose data.
- [ ] **First‑run wizard:** explain what the app does, that AI is opt‑in, that nothing is uploaded except
      the app/domain names you send to the AI when *you* click analyze.

**Gate:** a non‑technical tester can install, use the free features, and cannot accidentally brick their PC.

### Wave 2 — Installable & updatable — **M** (+ cert cost)
- [ ] **Installer:** Inno Setup or WiX (MSI). Registers Start‑menu, uninstaller, and the logon task.
- [ ] **Authenticode code signing.** Options for OSS:
      - OV cert (~$100–300/yr) — signs, but SmartScreen reputation still builds over time.
      - EV cert (~$300–600/yr) — instant SmartScreen trust; **also required later to sign the driver.**
      - Azure Trusted Signing (cheap, if eligible) — increasingly the OSS‑friendly route.
- [ ] **Auto‑update:** either a built‑in updater (check GitHub Releases, verify signature, swap) or ship a
      **winget** manifest and let winget handle updates. Winget is the low‑maintenance OSS choice.
- [ ] Release artifacts: signed installer + **SHA‑256 checksums** + release notes.

**Gate:** `Download → run installer → no SmartScreen scare → auto‑updates`.

### Wave 3 — CI/CD + reproducibility — **S–M**
- [ ] **GitHub Actions:** build on push, run any tests, produce the publish artifact.
- [ ] Tag‑triggered **release workflow:** build → sign → attach installer + checksums to the GitHub Release.
- [ ] Dependabot / renovate for the NuGet deps (Anthropic SDK, SharpPcap, Titanium proxy).
- [ ] Add a minimal **test project** for the risky pure logic (vault crypto round‑trip, impact scanner path
      matching, settings (de)serialization — the OrdinalIgnoreCase bug that bit you is a perfect regression test).

**Gate:** cutting a release = pushing a tag.

### Wave 4 — The depth moat (WFP driver + self‑protection) — **XL, the hard one**
This is what moves you from "netsh wrapper" to peer of simplewall/Portmaster.

- [ ] **WFP (Windows Filtering Platform) callout driver** for real per‑app, per‑connection filtering in the
      kernel — faster, harder to bypass, can truly enforce default‑deny before a packet leaves.
- [ ] **Self‑protection:** the service/driver resists being killed by a normal user (reviewers test this).
- [ ] **Driver signing reality check:** a kernel driver needs an **EV cert + Microsoft attestation / WHQL**.
      This is real money and process even for open source. Plan it as its own project; ship Waves 0–3 without it
      first (netsh path stays as the "lite" engine).

**Gate:** enforcement survives a hostile user and a determined app.

### Wave 5 — Trust & documentation — **M**
- [ ] **Docs site** (GitHub Pages / mkdocs): install, every tab explained, the MITM/CA story, FAQ.
- [ ] **Privacy policy + threat model:** exactly what leaves the machine (only your explicit AI queries),
      what the vault protects against and what it doesn't, what Expert mode can break.
- [ ] **"No telemetry" promise**, verifiable because the source is open.
- [ ] Invite a **community security review** of the vault + MITM (the OSS substitute for a paid audit;
      a paid audit later is the gold standard for the vault).

### Wave 6 — Growth & launch — **M, ongoing**
- [ ] Landing page (the AI‑copilot demo front and center) + the demo video.
- [ ] Publish to **winget**; a "monitor‑only" build to the **Microsoft Store** as a funnel (Store won't allow
      the driver/MITM build — direct download for full).
- [ ] Launch posts: r/windows, r/privacy, Hacker News "Show HN", Product Hunt. OSS + "AI explains what your
      apps are doing" is a strong hook.
- [ ] GitHub Discussions / Discord for support and contributors.

---

## 3. Priority / sequencing at a glance

| Priority | Item | Wave | Effort | Blocker? |
|----------|------|------|--------|----------|
| 1 | Repo + license + README + screenshots | 0 | S–M | — |
| 2 | Expert‑mode guardrails; fix MITM CA lifecycle; crash safety | 1 | M | **B** |
| 3 | Installer + code signing + winget | 2 | M+$ | **A** |
| 4 | CI/CD release pipeline + core tests | 3 | S–M | — |
| 5 | Docs, privacy policy, threat model, community review | 5 | M | — |
| 6 | Launch (winget/Store funnel, landing, Show HN) | 6 | M | — |
| 7 | WFP driver + self‑protection | 4 | XL+$ | **C** |

Ship 1–6 as **v1.0 (netsh engine, signed, safe, documented, open)**. Land the driver as **v2.0**; it's the
one item that can't be rushed and shouldn't hold the launch.

---

## 4. Honest risks to plan around

- **AV false‑positives** on first release (admin + firewall + process control + optional MITM). Mitigate with
  signing, a clean reputation over time, and submitting the signed build to Microsoft/AV vendors for allow‑listing.
- **The MITM feature is the reputational tightrope.** Off by default, loud consent, auto‑cleanup, and "we never
  see your traffic — it stays on your machine" messaging. If it ever looks sneaky, it sinks the whole project.
- **Support load** scales with power. Expert mode + good docs + "Restore everything" one‑click keep it sane.
- **Driver cost/time (Wave 4)** is the one XL. Don't let it block v1.

## 5. The one permanent line

The vault and the browser‑extension **consent prompt** are marketable and stay. **Silent/automatic capture of
usernames + passwords from traffic or keystrokes is not part of this product and won't be built** — it's the
difference between a password manager and a credential harvester, and it's also exactly what would get an
open‑source security tool (rightly) branded as malware.

---

## 6. Recommended next 3 actions
1. **Wave 0:** stand up the public repo — license choice, README with the AI screenshot, screenshots/GIF.
2. **Wave 1:** the guardrail refactor (Expert mode + MITM CA auto‑cleanup + crash fail‑open). Makes it safe to share.
3. **Wave 2:** installer + pick a signing path (Azure Trusted Signing or an OV/EV cert). Makes it installable.

Do those and you have a signed, safe, documented, open‑source all‑in‑one suite people can actually run — the
driver moat then upgrades it from "great free tool" to "beats the specialists."
