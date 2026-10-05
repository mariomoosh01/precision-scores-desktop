// Precision Scores desktop shell strip.
//
// Rendered above the React UI; owns a 34px band at the top of the
// viewport and communicates with the native .NET host via Photino's
// web-message channel (window.external.sendMessage / receiveMessage).
//
// Protocol (JSON over web-message):
//   native -> web:
//     { "kind": "state", "online": bool, "syncing": bool,
//       "pending": int, "lastSyncAt": "ISO8601" | null }
//     { "kind": "matches", "list": [{ "id": "<guid>", "name": "...",
//                                      "date": "ISO8601" }] }
//     { "kind": "toast", "level": "info"|"error", "text": "..." }
//   web -> native:
//     { "kind": "downloadMatchListRequested" }
//     { "kind": "downloadMatchRequested", "matchId": "<guid>" }
//     { "kind": "uploadRequested" }

(function () {
  "use strict";

  function ensureStrip() {
    if (document.getElementById("ps-shell-strip")) return;
    const strip = document.createElement("div");
    strip.id = "ps-shell-strip";
    strip.setAttribute("data-online", "false");
    strip.setAttribute("data-syncing", "false");
    strip.innerHTML = `
      <span class="ps-dot" aria-hidden="true"></span>
      <span class="ps-status">Offline</span>
      <span class="ps-pending" data-count="0">0 cards pending</span>
      <span class="ps-last-sync"></span>
      <span class="ps-spacer"></span>
      <button type="button" class="ps-download" disabled>Download match…</button>
      <button type="button" class="ps-upload" disabled>Upload now</button>
    `;
    document.body.insertBefore(strip, document.body.firstChild);

    strip.querySelector(".ps-download").addEventListener("click", () => {
      sendToNative({ kind: "downloadMatchListRequested" });
    });
    strip.querySelector(".ps-upload").addEventListener("click", () => {
      sendToNative({ kind: "uploadRequested" });
    });
  }

  // Photino initialises window.external.sendMessage sometime between
  // DOMContentLoaded and the end of main-thread work. If we try to
  // send before it's wired, buffer the message and retry on a short
  // interval until the bridge exists.
  const pendingOut = [];
  function sendToNative(msg) {
    if (tryFlushOne(msg)) return;
    pendingOut.push(msg);
    scheduleFlush();
  }
  function tryFlushOne(msg) {
    try {
      if (typeof window.external !== "undefined" &&
          typeof window.external.sendMessage === "function") {
        window.external.sendMessage(JSON.stringify(msg));
        return true;
      }
    } catch (_) { /* fall through to retry */ }
    return false;
  }
  let flushTimer = null;
  function scheduleFlush() {
    if (flushTimer) return;
    flushTimer = setInterval(() => {
      while (pendingOut.length > 0) {
        if (!tryFlushOne(pendingOut[0])) return;
        pendingOut.shift();
      }
      clearInterval(flushTimer);
      flushTimer = null;
    }, 100);
  }

  function applyState(state) {
    const strip = document.getElementById("ps-shell-strip");
    if (!strip) return;
    strip.dataset.online = state.online ? "true" : "false";
    strip.dataset.syncing = state.syncing ? "true" : "false";
    strip.querySelector(".ps-status").textContent =
      state.syncing ? "Syncing…" : state.online ? "Online" : "Offline";
    const pending = strip.querySelector(".ps-pending");
    pending.textContent = (state.pending || 0) + " cards pending";
    pending.setAttribute("data-count", String(state.pending || 0));
    strip.querySelector(".ps-download").disabled = !state.online || state.syncing;
    strip.querySelector(".ps-upload").disabled =
      !state.online || state.syncing || !state.pending;
    const lastSync = strip.querySelector(".ps-last-sync");
    lastSync.textContent = state.lastSyncAt
      ? "last sync " + formatRelative(state.lastSyncAt)
      : "";
  }

  function formatRelative(iso) {
    const then = new Date(iso).getTime();
    const now = Date.now();
    const diff = Math.max(0, Math.round((now - then) / 1000));
    if (diff < 60) return "just now";
    if (diff < 3600) return Math.round(diff / 60) + " min ago";
    if (diff < 86400) return Math.round(diff / 3600) + " h ago";
    return Math.round(diff / 86400) + " d ago";
  }

  function showMatchesModal(list) {
    // Phase E placeholder — the full modal lands with the Download
    // workflow. For now the native handler returns the list and we
    // just emit it to the console; next commit replaces this with an
    // actual picker UI.
    console.info("[shell-strip] matches available:", list);
    sendToNative({
      kind: "downloadMatchRequested",
      matchId: list && list.length ? list[0].id : null,
    });
  }

  function handleNativeMessage(raw) {
    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (!payload || !payload.kind) return;
      switch (payload.kind) {
        case "state": applyState(payload); break;
        case "matches": showMatchesModal(payload.list || []); break;
        case "toast":
          console.info("[shell-strip] toast:", payload.level, payload.text);
          break;
      }
    } catch (err) {
      console.warn("[shell-strip] bad native message:", err);
    }
  }

  // Photino's native→web channel calls window.external.receiveMessage
  // once; the registered handler fires for every SendWebMessage from
  // the .NET side.
  if (typeof window.external !== "undefined" &&
      typeof window.external.receiveMessage === "function") {
    window.external.receiveMessage(handleNativeMessage);
  } else {
    // Dev mode (plain browser) — expose a hook for manual testing.
    window.__psShellInject = handleNativeMessage;
  }

  function announceReady() {
    // Debug probe: a GET we can see in Kestrel logs to confirm strip.js
    // is actually running (independent of whether the native bridge works).
    try {
      fetch("/_shell/probe?stage=ready&hasExternal=" + (typeof window.external) +
            "&hasSend=" + (typeof window.external?.sendMessage));
    } catch (_) { /* noop */ }
    sendToNative({ kind: "ready" });
  }

  // Build the strip + handshake with native as soon as the body exists.
  if (document.body) {
    ensureStrip();
    announceReady();
  } else {
    document.addEventListener("DOMContentLoaded", () => {
      ensureStrip();
      announceReady();
    }, { once: true });
  }
})();
