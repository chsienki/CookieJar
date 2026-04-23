async function ping() {
  try {
    const res = await fetch("http://127.0.0.1:47891/health");
    if (!res.ok) throw new Error(res.status);
    const json = await res.json();
    const el = document.getElementById("hostStatus");
    el.textContent = json.extensionConnected ? "connected" : "running, no ext";
    el.className = json.extensionConnected ? "ok" : "bad";
  } catch (e) {
    const el = document.getElementById("hostStatus");
    el.textContent = "offline";
    el.className = "bad";
  }
}

document.getElementById("reconnectBtn").addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ op: "reconnect" }).catch(() => {});
  setTimeout(ping, 500);
});

document.getElementById("copyCurlBtn").addEventListener("click", async () => {
  const cmd = `curl -H "Authorization: Bearer $(Get-Content $env:LOCALAPPDATA\\CookieJar\\token.txt)" "http://127.0.0.1:47891/cookies?domain=example.com"`;
  await navigator.clipboard.writeText(cmd);
});

ping();
