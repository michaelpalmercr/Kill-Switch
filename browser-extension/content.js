// KillSwitch Vault — content script. Detects when YOU submit a login on a page you're visiting and
// offers to save it. It only reads the form you just submitted, in your own browser. Nothing is sent anywhere.

(() => {
  let lastKey = "";       // dedupe identical captures
  let lastAt = 0;
  let bannerHost = null;

  // ---------- credential extraction ----------
  function filledPassword(root = document) {
    const pws = Array.from(root.querySelectorAll('input[type="password"]'));
    return pws.find((p) => p.value && p.value.length > 0) || null;
  }

  function guessUsername(pwField) {
    const form = pwField.form || document;
    const inputs = Array.from(form.querySelectorAll("input"));
    // Prefer explicit username/email fields.
    const byAuto = inputs.find((i) => (i.autocomplete || "").includes("username") && i.value);
    if (byAuto) return byAuto.value;
    const byType = inputs.find((i) => (i.type === "email" || i.type === "text" || i.type === "tel") && i.value &&
      /user|email|login|account|phone|mail/i.test((i.name || "") + (i.id || "") + (i.autocomplete || "")));
    if (byType) return byType.value;
    // Fall back to the last filled text/email input before the password field.
    const pwIndex = inputs.indexOf(pwField);
    for (let i = pwIndex - 1; i >= 0; i--) {
      const t = inputs[i].type;
      if ((t === "text" || t === "email" || t === "tel") && inputs[i].value) return inputs[i].value;
    }
    const anyText = inputs.find((i) => (i.type === "text" || i.type === "email") && i.value);
    return anyText ? anyText.value : "";
  }

  function capture() {
    const pw = filledPassword();
    if (!pw) return;
    const data = {
      origin: location.origin,
      url: location.origin + location.pathname,
      username: guessUsername(pw),
      password: pw.value,
    };
    const key = data.origin + "|" + data.username + "|" + data.password;
    const now = Date.now();
    if (key === lastKey && now - lastAt < 4000) return; // avoid double-fire (submit + click)
    lastKey = key; lastAt = now;

    chrome.runtime.sendMessage({ type: "capture", data }, (resp) => {
      if (chrome.runtime.lastError || !resp) return;
      if (resp.action === "save") showBanner("save", data);
      else if (resp.action === "update") showBanner("update", data);
      else if (resp.action === "locked") showBanner("locked", data);
      // "known" / "never" → stay silent
    });
  }

  // ---------- triggers ----------
  document.addEventListener("submit", () => setTimeout(capture, 0), true);
  document.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && e.target && e.target.type === "password") setTimeout(capture, 0);
  }, true);
  document.addEventListener("click", (e) => {
    const el = e.target && e.target.closest ? e.target.closest('button, input[type="submit"], [role="button"]') : null;
    if (el && filledPassword()) setTimeout(capture, 50);
  }, true);

  // ---------- banner (shadow DOM) ----------
  function removeBanner() {
    if (bannerHost) { bannerHost.remove(); bannerHost = null; }
  }

  function showBanner(mode, data) {
    removeBanner();
    bannerHost = document.createElement("div");
    bannerHost.style.cssText = "position:fixed;top:16px;right:16px;z-index:2147483647;";
    const shadow = bannerHost.attachShadow({ mode: "open" });

    const wrap = document.createElement("div");
    wrap.innerHTML = `
      <style>
        .card{font:13px/1.4 'Segoe UI',system-ui,sans-serif;width:320px;background:#fff;color:#1b1b1b;
          border:1px solid #d7d7d7;border-radius:10px;box-shadow:0 8px 28px rgba(0,0,0,.22);overflow:hidden}
        .hd{display:flex;align-items:center;gap:8px;padding:10px 12px;background:#0f6b3f;color:#fff;font-weight:600}
        .dot{width:10px;height:10px;border-radius:50%;background:#7CFFB0;box-shadow:0 0 0 2px rgba(255,255,255,.35)}
        .bd{padding:12px}
        .muted{color:#666;font-size:12px;margin-top:4px;word-break:break-all}
        .row{display:flex;gap:8px;margin-top:12px;justify-content:flex-end;flex-wrap:wrap}
        button{font:13px 'Segoe UI',sans-serif;border-radius:7px;padding:7px 12px;border:1px solid #cdcdcd;
          background:#f4f4f4;cursor:pointer}
        button.primary{background:#0f6b3f;border-color:#0f6b3f;color:#fff}
        button.ghost{background:transparent;border-color:transparent;color:#666}
        .x{margin-left:auto;background:transparent;border:0;color:#fff;font-size:16px;cursor:pointer;line-height:1}
      </style>
      <div class="card">
        <div class="hd"><span class="dot"></span><span id="title"></span>
          <button class="x" id="close">&times;</button></div>
        <div class="bd">
          <div id="msg"></div>
          <div class="muted" id="detail"></div>
          <div class="row" id="row"></div>
        </div>
      </div>`;
    shadow.appendChild(wrap);

    const $ = (id) => shadow.getElementById(id);
    const title = $("title"), msg = $("msg"), detail = $("detail"), row = $("row");
    $("close").onclick = removeBanner;

    const addBtn = (label, cls, fn) => {
      const b = document.createElement("button");
      b.textContent = label; if (cls) b.className = cls; b.onclick = fn; row.appendChild(b);
    };

    if (mode === "locked") {
      title.textContent = "KillSwitch Vault is locked";
      msg.textContent = "Unlock the vault to save this login.";
      detail.textContent = "Click the KillSwitch Vault icon in your toolbar, enter your master password, then submit again.";
      addBtn("Dismiss", "ghost", removeBanner);
      return;
    }

    const save = () => {
      chrome.runtime.sendMessage({ type: "confirmSave", data }, () => {
        msg.textContent = "Saved to your vault ✓"; detail.textContent = ""; row.innerHTML = "";
        setTimeout(removeBanner, 1200);
      });
    };

    title.textContent = "KillSwitch Vault";
    msg.textContent = mode === "update" ? "Update the saved password?" : "Save this login?";
    detail.textContent = (data.username ? data.username + "  •  " : "") + data.origin;
    if (mode === "save") {
      addBtn("Never for this site", "ghost", () => { chrome.runtime.sendMessage({ type: "never", origin: data.origin }); removeBanner(); });
    }
    addBtn(mode === "update" ? "Update" : "Save", "primary", save);
    addBtn("Not now", "", removeBanner);

    // auto-dismiss after 15s so it never lingers
    setTimeout(() => { if (bannerHost) removeBanner(); }, 15000);
  }
})();
