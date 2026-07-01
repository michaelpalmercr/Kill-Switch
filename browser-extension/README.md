# KillSwitch Vault — browser extension

A self-contained, encrypted password vault for **your own browser** that shows a Chrome-style
**"Save this login?"** prompt when you submit a login form.

## Why this is the safe version of "auto-save"

- It runs **inside your browser** and only ever reads **the form you just submitted** — the same thing
  Chrome's own password manager does.
- It does **not** sniff network traffic, decrypt HTTPS, or log keystrokes. There is no interception of
  other apps or other people's logins.
- Everything is encrypted locally with **AES-256-GCM** (key derived from your master password with
  PBKDF2-SHA256, 200k iterations). The master password is never stored. **Nothing is uploaded.**

## What it does

- When you submit a login, it asks **"Save this login?"** — with **Save**, **Never for this site**, or **Not now**.
- If it already has that exact site + username + password, it **stays silent** (it recognizes it).
- If the password changed for a known username, it offers to **Update**.
- Toolbar popup: create/unlock with your master password, search, reveal, copy username/password, delete, lock.

## Install (Chrome / Edge / Brave — any Chromium browser)

1. Open `chrome://extensions` (or `edge://extensions`).
2. Turn on **Developer mode** (top-right).
3. Click **Load unpacked** and select this `browser-extension` folder.
4. Click the puzzle-piece icon → pin **KillSwitch Vault**.
5. Open the popup once and **create your master password**.

## Notes

- You stay unlocked for the browser session; closing the browser locks it again.
- This vault is separate from the KillSwitch desktop app's vault (different runtime/security model).
  Use the desktop app's **Ctrl+Alt+L** quick-save for non-browser logins.
