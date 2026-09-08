# CookieJar

Local cookie broker for AI agents. An Edge MV3 extension and a tiny .NET 10
native-messaging host cooperate to expose your already-logged-in browser
cookies via a localhost REST API, so agents (e.g. GitHub Copilot CLI) can
authenticate to sites without you copying cookies out of DevTools.

## Why

Sites that gate everything behind short-lived browser cookies require
short-lived browser cookies for authentication. The previous workflow was:
open DevTools, copy `sid`/`cid`/`PHPSESSID`, paste into the agent prompt -- on
every cookie expiry. CookieJar removes the human in the loop while keeping the
cookies inside the browser; the agent only ever sees them via an authenticated
localhost API.

## Architecture

```
agent (curl/PowerShell) ─HTTP+Bearer─►  CookieJar.Host (.NET)  ─stdio─►  Edge extension
                                            │ 127.0.0.1:47891              │
                                            └──── launched by Edge ◄───────┘
```

- **Extension** -- holds `cookies` + `nativeMessaging` permissions; on startup
  it connects to the host and answers `chrome.cookies.getAll` requests.
- **Host** -- a .NET 10 console app that Edge launches via the native-messaging
  protocol. It also runs an ASP.NET Core minimal API on `127.0.0.1:47891` and
  forwards each authorised HTTP request to the extension over stdio.

Native messaging (rather than a WebSocket from the extension) means only one
process opens a TCP port, the host's lifetime is tied to Edge, and the MV3
service worker is kept alive by the open native port.

## Install

Requirements: Windows, Edge, .NET 10 SDK.

```powershell
# 1. Publish the host and generate a token (no extension wired up yet).
.\install.ps1

# 2. Open edge://extensions/, enable Developer mode, click "Load unpacked",
#    pick the .\extension\ folder. Copy the extension ID shown on the card.

# 3. Re-run the installer with the ID to register the native-messaging host:
.\install.ps1 -ExtensionId <id>

# 4. Reload the extension once. It should now connect; the popup shows
#    "Native host: connected".
```

For Chrome instead of Edge, pass `-Browser chrome`.

## Use

```powershell
$t = (Get-Content $env:LOCALAPPDATA\CookieJar\token.txt).Trim()
Invoke-RestMethod -Headers @{Authorization="Bearer $t"} `
  'http://127.0.0.1:47891/cookies?domain=example.com'
```

```bash
TOKEN=$(cat $LOCALAPPDATA/CookieJar/token.txt)
curl -sH "Authorization: Bearer $TOKEN" \
  'http://127.0.0.1:47891/cookies?domain=example.com' | jq -r .header
```

### API

| Method | Path                                                            | Auth |
|--------|-----------------------------------------------------------------|------|
| GET    | `/health`                                                       | no   |
| GET    | `/cookies?domain=<d>[&name=<n>][&includeSubdomains=true\|false]`| yes  |
| GET    | `/domains`                                                      | yes  |
| GET    | `/request-auth?domain=<d>`                                      | yes  |

`/cookies` returns both a ready-to-use `Cookie` header string and structured
cookie objects:

```json
{
  "domain": "example.com",
  "fetchedAt": "2026-04-17T23:12:00Z",
  "header": "sid=...; cid=...",
  "curlB":  "sid=...; cid=...",
  "cookies": [
    { "name": "sid", "value": "...", "domain": ".example.com",
      "path": "/", "expires": 1800000000, "httpOnly": true,
      "secure": true, "sameSite": "lax", "session": false }
  ]
}
```

Status codes: `401` bad/missing token, `404` no cookies for that domain,
`503` extension not connected (open Edge), `504` extension timed out.

`/request-auth` returns the coherent Cookie, User-Agent, `x-bc`, user-ID, and
request-signing headers from the latest matching browser request. Only requests
carrying the required authentication headers are retained. Open or refresh an
authenticated page before calling it; unlike separately fetched cookies, these
values belong to one signed request and avoid mixing rotated sessions or browser
fingerprints. Signing headers are diagnostic and request-specific; callers
should not replay them for another URL.

## Security model

- The HTTP server binds to `127.0.0.1` only. Nothing is exposed off-box.
- Every request needs `Authorization: Bearer <token>`. The token is a 256-bit
  random hex string regenerated on first run, stored at
  `%LOCALAPPDATA%\CookieJar\token.txt` with an ACL that grants access only to
  the current user.
- Native messaging restricts which extension may connect via the `allowed_origins`
  field of the host manifest, so other extensions on the same browser can't
  invoke us.
- Rotate the token any time:
  ```powershell
  & "$env:LOCALAPPDATA\CookieJar\bin\CookieJar.Host.exe" --rotate-token
  ```
  After rotation, reload the CookieJar extension once.

Note: any local process running as your user can still read the token file
(that's the whole point -- agents need it). The threat model is "trusted local
user, untrusted network", not "trusted user, untrusted local processes".

## Uninstall

```powershell
.\uninstall.ps1            # removes manifest, registry key, install dir, token
.\uninstall.ps1 -KeepToken # keep the token for a future reinstall
```

Then remove the CookieJar extension from `edge://extensions/`.

## Layout

```
CookieJar\
├── extension\               -- Edge MV3 extension (background.js, popup, manifest)
├── server\
│   └── CookieJar.Host\      -- .NET 10 host: stdio bridge + minimal API
├── scripts\
│   └── test-host.ps1        -- Integration smoke test (no real browser needed)
├── install.ps1
├── uninstall.ps1
└── README.md
```

## Troubleshooting

- **Popup says "offline"** -- the host isn't running. That's normal until Edge
  starts the extension. Check `edge://extensions/` -> CookieJar -> "service
  worker" link, look for native-messaging errors.
- **`/health` returns `extensionConnected: false`** -- the host is running
  (probably because of a stale agent install) but Edge hasn't connected. Open
  Edge or reload the extension.
- **`401 unauthorized`** -- token mismatch. Re-read `token.txt`.
- **Extension ID changed** -- happens if you reload from a different folder or
  delete + re-add the extension. Re-run `install.ps1 -ExtensionId <new-id>`.
- **Multiple browser profiles** -- the cookies returned are from the profile
  that loaded the extension. Install separately in each profile if you need
  both.
- **`/request-auth` returns `404 no_request_auth`** -- open or refresh an
  authenticated page so the extension observes a signed API request.

## Out of scope (for now)

- Firefox support (different native-messaging manifest layout).
- Setting/deleting cookies, cookie change subscriptions.
- Cross-platform installers (Linux/macOS).
