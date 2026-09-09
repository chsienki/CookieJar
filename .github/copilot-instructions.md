# Copilot Instructions -- CookieJar

## What this repo is

CookieJar is a local cookie broker for AI agents. It pairs:

- An **Edge MV3 extension** (`extension/`) that holds the `cookies` and
  `nativeMessaging` permissions and exposes the user's currently logged-in
  browser cookies on demand.
- A **.NET 10 console app** (`server/CookieJar.Host/`) that Edge launches over
  the native-messaging protocol (4-byte length-prefixed JSON over stdio). The
  same process also hosts an ASP.NET Core minimal API on `127.0.0.1:47891` and
  forwards each authorised HTTP request to the extension.

Agents fetch cookies via `GET /cookies?domain=...` with a bearer token. The
checked-in `cookies` skill (`.github/skills/cookies/SKILL.md`) documents the
public contract for callers and can be copied to
`~/.copilot/skills/cookies/` for machine-wide use.

## Repo layout

```
CookieJar/
+-- .github/
|   +-- skills/cookies/SKILL.md # Canonical public Copilot CLI skill
+-- extension/                  # Edge MV3 extension
|   +-- manifest.json           # MV3 manifest. No icons (intentional v1).
|   +-- background.js           # Service worker: native-messaging client + handlers
|   +-- popup.html / popup.js   # Status UI; "Reconnect host" button
+-- server/
|   +-- CookieJar.sln(x)
|   +-- CookieJar.Host/
|       +-- Program.cs                  # Entry point + API + bearer middleware
|       +-- NativeMessagingChannel.cs   # Reads/writes the stdio framing
|       +-- CookieBroker.cs             # id->TaskCompletionSource correlation
|       +-- TokenStore.cs               # Token gen + Windows ACL tightening
|       +-- InstallerCommands.cs        # Subcommands invoked by install.ps1
+-- scripts/
|   +-- test-host.ps1           # End-to-end smoke test (no browser required)
+-- install.ps1                 # publish + manifest + registry + token
+-- uninstall.ps1               # inverse, with -KeepToken
+-- README.md
```

## How install / runtime works

1. `install.ps1` (no `-ExtensionId`) publishes the host to
   `%LOCALAPPDATA%\CookieJar\bin\CookieJar.Host.exe`, generates the bearer
   token at `%LOCALAPPDATA%\CookieJar\token.txt` (user-only ACL via
   `TokenStore.RestrictToCurrentUserWindows`), and prints "load unpacked"
   instructions.
2. User loads `extension\` in `edge://extensions/`, copies the generated
   extension ID, and re-runs `install.ps1 -ExtensionId <id>`.
3. The host's `--write-manifest` subcommand writes the native-messaging
   manifest to
   `%LOCALAPPDATA%\Microsoft\Edge\User Data\NativeMessagingHosts\com.cookiejar.host.json`
   and registers
   `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.cookiejar.host`
   pointing at it. `allowed_origins` is locked to the supplied extension ID.
4. Edge starts the host whenever the extension calls
   `chrome.runtime.connectNative("com.cookiejar.host")`. Stdin/stdout are
   bound to the native-messaging pipe; the HTTP server listens on
   `127.0.0.1:47891` (overridable via `COOKIEJAR_LISTEN`).
5. When the extension is alive it sends `{op:"hello"}`; the broker flips
   `ExtensionConnected = true`. HTTP `/cookies` requests get an id, are
   forwarded via stdio, and resolve when the matching reply comes back (5s
   timeout).

## Wire protocol (extension <-> host)

Frames are 4-byte little-endian length prefix + UTF-8 JSON. Messages:

| Direction      | op           | Fields                                          |
|----------------|--------------|-------------------------------------------------|
| ext -> host    | `hello`      | `version`                                       |
| host -> ext    | `getCookies` | `id`, `domain`, `includeSubdomains?`, `name?`   |
| ext -> host    | (response)   | `id`, `ok:true`, `cookies:[...]`                |
| host -> ext    | `listDomains`| `id`                                            |
| ext -> host    | (response)   | `id`, `ok:true`, `domains:[...]`                |
| ext -> host    | (error)      | `id`, `ok:false`, `error:"..."`                 |

If you add a new op:
1. Handle it in `background.js`'s `handleMessage`.
2. Add a `MapGet` route in `Program.cs` that builds the request JsonObject and
   calls `broker.RequestAsync`.
3. Document the route in `README.md` and in the branch's `cookies` skill at
   `.github/skills/cookies/SKILL.md`. On this machine, apply the compatible
   documentation change to `~/.copilot/skills/cookies/SKILL.md` and the
   chezmoi source.

## Public-facing docs

These files are seen by other people / public dotfiles. Use `example.com` only
-- no real site names:

- `README.md`
- `install.ps1` (the printed "Quick test" snippet)
- `extension/popup.js` (the copy-curl button)
- `.github/skills/cookies/SKILL.md`
- `~/.copilot/skills/cookies/SKILL.md` AND
  `~/.local/share/chezmoi/dot_copilot/skills/cookies/SKILL.md`

## Skill workflow (per user-level instructions)

The checked-in `.github/skills/cookies/SKILL.md` is the public skill for the
API implemented by the current branch. When changing it on this machine,
**always** update all three locations:

1. Edit `.github/skills/cookies/SKILL.md`.
2. Apply the compatible change to `~/.copilot/skills/cookies/SKILL.md`
   (live), preserving any routes implemented by other active branches.
3. Mirror the live skill to
   `~/.local/share/chezmoi/dot_copilot/skills/cookies/SKILL.md`.
4. In the chezmoi repo: `git add` the file, **then** `git stash -u --keep-index`
   to set aside unrelated dirty files, commit, push, `git stash pop`. The
   chezmoi working tree often has unrelated edits -- never touch them.

## Testing

`scripts/test-host.ps1` is the canonical smoke test -- it spawns the host with
a redirected stdin pipe, sends a fake `hello` frame, hits `/health`, exercises
401/200 paths through `/cookies`, and verifies the `header` formatting. Run it
before any release-touching change. It does NOT require a real browser.

For end-to-end verification with the real extension, use `Invoke-RestMethod`
against `127.0.0.1:47891` while Edge is running -- see README "Use" section.

## Security model (don't regress)

- HTTP server is bound to `127.0.0.1` only. **Do not** add a configurable bind
  address that allows non-loopback addresses.
- All non-`/health` routes require `Authorization: Bearer <token>`. Comparison
  is constant-time (`CryptographicEquals` in `Program.cs`).
- Token is 256-bit cryptographic random hex, stored with Windows ACL granting
  only the current user (see `TokenStore.RestrictToCurrentUserWindows`).
- Native-messaging manifest's `allowed_origins` is restricted to the single
  installed extension ID -- other extensions cannot connect.
- If you add a route that mutates state (cookies write, etc.), require auth
  *and* think hard about CSRF (browsers can hit localhost from any origin).

## Conventions

- C# / .NET 10, file-scoped namespaces, primary constructors, `var` when type
  is obvious. Follow user-level Copilot instructions (`~/.copilot/copilot-instructions.md`).
- ASCII only in source comments and PowerShell strings (no em dashes, smart
  quotes, Unicode arrows -- they get mangled when piped).
- Don't add icon files unless someone actually asks; the manifest currently
  has no `icons` field on purpose.
- Don't commit changes here without explicit user approval (per user-level
  rule). The chezmoi mirror push for skill changes is the exception because
  it's part of the documented skill workflow.

## Known quirks

- The Edge extension ID is non-deterministic for unpacked extensions. Adding a
  fixed `key` field to `manifest.json` would stabilise it but expand the
  install flow; for now the two-step install is intentional.
- Service worker is kept alive by the open native-messaging port; the
  `chrome.alarms` keepalive in `background.js` is belt-and-suspenders for
  cases where the port drops.
- `Program.cs` detects "interactive vs native-messaging mode" by checking
  whether `args[0]` starts with `--`. Subcommands always pass a `--` flag;
  Edge launches with no args. Keep this contract -- changing it breaks the
  installer.
