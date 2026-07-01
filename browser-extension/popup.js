// KillSwitch Vault — popup UI.
const $ = (id) => document.getElementById(id);
const send = (msg) => new Promise((res) => chrome.runtime.sendMessage(msg, res));

let creating = false;
let allEntries = [];

async function init() {
  const st = await send({ type: "status" });
  if (st && st.unlocked) return showVault();
  showAuth(!st || !st.exists);
}

function showAuth(create) {
  creating = create;
  $("auth").style.display = "block";
  $("vault").style.display = "none";
  $("lockBtn").style.display = "none";
  $("authTitle").textContent = create ? "Create your vault" : "Unlock your vault";
  $("authInfo").textContent = create
    ? "Pick a master password. It's never stored — if you lose it, the vault can't be opened."
    : "Enter your master password.";
  $("master2").style.display = create ? "block" : "none";
  $("authBtn").textContent = create ? "Create vault" : "Unlock";
  $("authErr").textContent = "";
  $("master").value = ""; $("master2").value = "";
  $("master").focus();
}

async function doAuth() {
  const m = $("master").value;
  $("authErr").textContent = "";
  if (creating) {
    if (m.length < 4) return ($("authErr").textContent = "Use at least 4 characters.");
    if (m !== $("master2").value) return ($("authErr").textContent = "Passwords don't match.");
    const r = await send({ type: "create", master: m });
    if (r && r.ok) return showVault();
    $("authErr").textContent = (r && r.error) || "Could not create vault.";
  } else {
    const r = await send({ type: "unlock", master: m });
    if (r && r.ok) return showVault();
    $("authErr").textContent = (r && r.error) || "Wrong master password.";
  }
}

async function showVault() {
  $("auth").style.display = "none";
  $("vault").style.display = "block";
  $("lockBtn").style.display = "inline-block";
  const r = await send({ type: "list" });
  if (r && r.locked) return showAuth(false);
  allEntries = (r && r.entries) || [];
  render();
}

function render() {
  const q = $("search").value.trim().toLowerCase();
  const list = $("list");
  list.innerHTML = "";
  const items = allEntries
    .map((e, i) => ({ e, i }))
    .filter(({ e }) => !q || (e.origin + " " + e.username).toLowerCase().includes(q))
    .sort((a, b) => a.e.origin.localeCompare(b.e.origin));

  if (items.length === 0) {
    list.innerHTML = `<div class="empty">${allEntries.length ? "No matches." : "No saved logins yet.<br>Submit a login on any site and choose “Save”."}</div>`;
    return;
  }

  for (const { e, i } of items) {
    const div = document.createElement("div");
    div.className = "item";
    const site = document.createElement("div"); site.className = "site"; site.textContent = e.origin;
    const usr = document.createElement("div"); usr.className = "usr"; usr.textContent = e.username || "(no username)";
    const pw = document.createElement("div"); pw.className = "pw"; pw.textContent = "•".repeat(Math.min(12, Math.max(6, (e.password || "").length)));
    const acts = document.createElement("div"); acts.className = "acts";

    const mk = (label, fn, cls) => { const b = document.createElement("button"); b.textContent = label; if (cls) b.className = cls; b.onclick = fn; return b; };
    let shown = false;
    acts.appendChild(mk("Show", (ev) => { shown = !shown; pw.textContent = shown ? e.password : "•".repeat(Math.min(12, Math.max(6, (e.password || "").length))); ev.target.textContent = shown ? "Hide" : "Show"; }));
    acts.appendChild(mk("Copy user", () => navigator.clipboard.writeText(e.username || "")));
    acts.appendChild(mk("Copy pass", () => navigator.clipboard.writeText(e.password || "")));
    acts.appendChild(mk("Delete", async () => {
      if (!confirm(`Delete saved login for ${e.origin}?`)) return;
      await send({ type: "delete", index: i });
      allEntries.splice(i, 1);
      render();
    }, "del"));

    div.append(site, usr, pw, acts);
    list.appendChild(div);
  }
}

$("authBtn").onclick = doAuth;
$("master").addEventListener("keydown", (e) => { if (e.key === "Enter") { if (creating) $("master2").focus(); else doAuth(); } });
$("master2").addEventListener("keydown", (e) => { if (e.key === "Enter") doAuth(); });
$("search").addEventListener("input", render);
$("lockBtn").onclick = async () => { await send({ type: "lock" }); showAuth(false); };

init();
