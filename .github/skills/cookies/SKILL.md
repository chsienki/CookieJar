---
name: cookies
description: Fetch fresh browser cookies for a domain from the local CookieJar broker. Use this when authenticated access requires the user's existing browser session instead of asking them to copy cookies from DevTools.
---

# cookies

CookieJar serves the user's current browser cookies through an authenticated
localhost API. Use it when a site requires the browser's logged-in session.

## Endpoint

- Base URL: `http://127.0.0.1:47891`
- Authentication: `Authorization: Bearer <token>` on every route except
  `/health`
- Token file: `%LOCALAPPDATA%\CookieJar\token.txt`

## Routes

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Report whether the host and browser extension are connected. |
| GET | `/cookies?domain=<d>[&name=<n>][&includeSubdomains=true\|false]` | Return structured cookies and a ready-to-use `Cookie` header. |
| GET | `/domains` | List domains for which the browser has cookies. |

`includeSubdomains` defaults to `true`. Pass
`includeSubdomains=false` to restrict the result to the exact host.

## PowerShell

```powershell
$token = (Get-Content "$env:LOCALAPPDATA\CookieJar\token.txt" -Raw).Trim()
$headers = @{ Authorization = "Bearer $token" }

$result = Invoke-RestMethod -Headers $headers `
  'http://127.0.0.1:47891/cookies?domain=example.com'

$result.header
```

Use the returned `header` value as the `Cookie` request header:

```powershell
Invoke-WebRequest -Headers @{ Cookie = $result.header } `
  'https://www.example.com/'
```

Treat the token and returned cookies as credentials. Do not print, persist, or
include them in a response unless the task specifically requires it.

## Error handling

| Status | Meaning | Action |
|--------|---------|--------|
| 401 | Missing or stale token | Re-read the token file; it may have been rotated. |
| 404 | No cookies for the domain | Ask the user to log in to the site in the browser. |
| 502 | The extension rejected the request | Report the returned error. |
| 503 | The extension is disconnected | Ask the user to open the browser or reload the extension. |
| 504 | The extension timed out | Retry once, then ask the user to reload the extension. |

## When not to use this skill

- The task does not require an authenticated browser session.
- The user already supplied the required authentication.
- `/health` reports that the extension is disconnected and the user is not
  available to reconnect it.

## Diagnostics

```powershell
Invoke-RestMethod 'http://127.0.0.1:47891/health'
```

The extension popup reports its connection state and provides a reconnect
button. Installation instructions are available at
https://github.com/chsienki/CookieJar.
