// CookieJar -- Edge MV3 service worker.
// Connects to the native-messaging host and answers cookie requests.

const HOST_NAME = "com.cookiejar.host";
const RECONNECT_DELAY_MS = 2000;
const KEEPALIVE_ALARM = "cookiejar-keepalive";

let port = null;

function log(...args) {
  console.log("[CookieJar]", ...args);
}

function connect() {
  if (port) return;
  try {
    log("Connecting to native host", HOST_NAME);
    port = chrome.runtime.connectNative(HOST_NAME);
    port.onMessage.addListener(handleMessage);
    port.onDisconnect.addListener(() => {
      const err = chrome.runtime.lastError?.message;
      log("Native host disconnected:", err);
      port = null;
      setTimeout(connect, RECONNECT_DELAY_MS);
    });
    port.postMessage({ op: "hello", version: chrome.runtime.getManifest().version });
  } catch (e) {
    log("connectNative failed:", e);
    port = null;
    setTimeout(connect, RECONNECT_DELAY_MS);
  }
}

async function handleMessage(msg) {
  const { id, op } = msg || {};
  try {
    if (op === "getCookies") {
      const cookies = await getCookies(msg);
      reply({ id, ok: true, cookies });
    } else if (op === "listDomains") {
      const domains = await listDomains();
      reply({ id, ok: true, domains });
    } else {
      reply({ id, ok: false, error: "unknown_op" });
    }
  } catch (e) {
    reply({ id, ok: false, error: String(e?.message || e) });
  }
}

function reply(msg) {
  if (!port) return;
  try {
    port.postMessage(msg);
  } catch (e) {
    log("postMessage failed:", e);
  }
}

async function getCookies({ domain, name, includeSubdomains }) {
  const query = { domain };
  if (name) query.name = name;
  let cookies = await chrome.cookies.getAll(query);

  if (includeSubdomains === false) {
    cookies = cookies.filter((c) => normalize(c.domain) === normalize(domain));
  }

  return cookies.map(serializeCookie);
}

async function listDomains() {
  const all = await chrome.cookies.getAll({});
  const set = new Set(all.map((c) => normalize(c.domain)));
  return [...set].sort();
}

function normalize(d) {
  return (d || "").replace(/^\./, "").toLowerCase();
}

function serializeCookie(c) {
  return {
    name: c.name,
    value: c.value,
    domain: c.domain,
    path: c.path,
    expires: c.expirationDate ?? null,
    httpOnly: c.httpOnly,
    secure: c.secure,
    sameSite: c.sameSite,
    session: c.session,
    storeId: c.storeId,
  };
}

// Keep the service worker alive while we want a live native-messaging port.
chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 0.4 });
chrome.alarms.onAlarm.addListener((a) => {
  if (a.name === KEEPALIVE_ALARM) {
    if (!port) connect();
  }
});

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
