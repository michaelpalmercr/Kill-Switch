// KillSwitch Vault — service worker. Holds the derived key in storage.session (session-only, never on disk),
// decrypts the vault on demand, and decides when to prompt to save a login.
// The vault ciphertext lives in storage.local; the master password is never stored.

const PBKDF2_ITERS = 200000;

let key = null;       // CryptoKey (in-memory)
let entries = null;   // decrypted [{origin, url, username, password, updated}]

// ---------- helpers ----------
const enc = new TextEncoder();
const dec = new TextDecoder();
const b64 = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf)));
const ub64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));

async function deriveKey(master, salt) {
  const baseKey = await crypto.subtle.importKey("raw", enc.encode(master), "PBKDF2", false, ["deriveKey"]);
  return crypto.subtle.deriveKey(
    { name: "PBKDF2", salt, iterations: PBKDF2_ITERS, hash: "SHA-256" },
    baseKey,
    { name: "AES-GCM", length: 256 },
    true,
    ["encrypt", "decrypt"]
  );
}

async function persist() {
  const salt = (await chrome.storage.local.get("salt")).salt;
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ct = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, enc.encode(JSON.stringify(entries)));
  await chrome.storage.local.set({ vault: { iv: b64(iv), ct: b64(ct) }, salt });
}

async function saveSessionKey() {
  const raw = await crypto.subtle.exportKey("raw", key);
  await chrome.storage.session.set({ k: b64(raw) });
}

// Rehydrate key+entries after the worker was suspended, using the session-held key.
async function ensureLoaded() {
  if (key && entries) return true;
  const { k } = await chrome.storage.session.get("k");
  if (!k) return false;
  try {
    key = await crypto.subtle.importKey("raw", ub64(k), "AES-GCM", true, ["encrypt", "decrypt"]);
    const { vault } = await chrome.storage.local.get("vault");
    if (!vault) { entries = []; return true; }
    const pt = await crypto.subtle.decrypt({ name: "AES-GCM", iv: ub64(vault.iv) }, key, ub64(vault.ct));
    entries = JSON.parse(dec.decode(pt));
    return true;
  } catch { key = null; entries = null; return false; }
}

// ---------- vault operations ----------
async function create(master) {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  await chrome.storage.local.set({ salt: b64(salt) }); // base64; persist() reads it back
  key = await deriveKey(master, salt);
  entries = [];
  await persist();
  await saveSessionKey();
  return { ok: true };
}

async function unlock(master) {
  const { salt, vault } = await chrome.storage.local.get(["salt", "vault"]);
  if (!salt) return { error: "No vault yet." };
  try {
    const saltBytes = ub64(salt);
    key = await deriveKey(master, saltBytes);
    if (vault) {
      const pt = await crypto.subtle.decrypt({ name: "AES-GCM", iv: ub64(vault.iv) }, key, ub64(vault.ct));
      entries = JSON.parse(dec.decode(pt));
    } else {
      entries = [];
    }
    await saveSessionKey();
    return { ok: true };
  } catch { key = null; entries = null; return { error: "Wrong master password." }; }
}

async function lock() {
  key = null; entries = null;
  await chrome.storage.session.remove("k");
  return { ok: true };
}

function findMatch(origin, username) {
  return entries.findIndex((e) => e.origin === origin && e.username === username);
}

// Decide whether to prompt for a captured login.
async function decide(data) {
  const loaded = await ensureLoaded();
  if (!loaded) return { action: "locked" };
  const { neverList = [] } = await chrome.storage.local.get("neverList");
  if (neverList.includes(data.origin)) return { action: "never" };

  const idx = findMatch(data.origin, data.username);
  if (idx >= 0) {
    if (entries[idx].password === data.password) return { action: "known" }; // recognized → no prompt
    return { action: "update" };
  }
  return { action: "save" };
}

async function confirmSave(data) {
  if (!(await ensureLoaded())) return { error: "locked" };
  const idx = findMatch(data.origin, data.username);
  const rec = { origin: data.origin, url: data.url, username: data.username, password: data.password, updated: new Date().toISOString() };
  if (idx >= 0) entries[idx] = rec; else entries.push(rec);
  await persist();
  return { ok: true };
}

async function addNever(origin) {
  const { neverList = [] } = await chrome.storage.local.get("neverList");
  if (!neverList.includes(origin)) neverList.push(origin);
  await chrome.storage.local.set({ neverList });
  return { ok: true };
}

// ---------- message router ----------
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  (async () => {
    try {
      switch (msg.type) {
        case "status": {
          const { salt } = await chrome.storage.local.get("salt");
          const unlocked = await ensureLoaded();
          sendResponse({ exists: !!salt, unlocked });
          break;
        }
        case "create": sendResponse(await create(msg.master)); break;
        case "unlock": sendResponse(await unlock(msg.master)); break;
        case "lock": sendResponse(await lock()); break;
        case "list":
          sendResponse((await ensureLoaded()) ? { entries } : { locked: true });
          break;
        case "delete":
          if (await ensureLoaded()) { entries.splice(msg.index, 1); await persist(); sendResponse({ ok: true }); }
          else sendResponse({ locked: true });
          break;
        case "capture": sendResponse(await decide(msg.data)); break;
        case "confirmSave": sendResponse(await confirmSave(msg.data)); break;
        case "never": sendResponse(await addNever(msg.origin)); break;
        default: sendResponse({ error: "unknown" });
      }
    } catch (e) { sendResponse({ error: String(e && e.message || e) }); }
  })();
  return true; // async response
});
