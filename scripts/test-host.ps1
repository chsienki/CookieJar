# Integration smoke test: start the host with a stdin pipe, send a "hello"
# native-messaging frame so the extension appears connected, then call the HTTP
# API and verify the responses.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSCommandPath | Split-Path -Parent
$hostExe = Join-Path $repo 'publish\CookieJar.Host.exe'
$token = (Get-Content (Join-Path $env:LOCALAPPDATA 'CookieJar\token.txt')).Trim()

# Use an unusual port so we don't collide with a running install.
$env:COOKIEJAR_LISTEN = 'http://127.0.0.1:47899'

$psi = [System.Diagnostics.ProcessStartInfo]::new($hostExe)
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)

try {
    function Send-Frame($obj) {
        $json = ConvertTo-Json $obj -Compress
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $len = [BitConverter]::GetBytes([uint32]$bytes.Length)
        $proc.StandardInput.BaseStream.Write($len, 0, 4)
        $proc.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $proc.StandardInput.BaseStream.Flush()
    }

    Start-Sleep -Milliseconds 1500
    Send-Frame @{ op = 'hello'; version = '0.1.0' }
    Start-Sleep -Milliseconds 500

    Write-Host "=== /health (no auth) ===" -ForegroundColor Cyan
    $health = Invoke-RestMethod 'http://127.0.0.1:47899/health'
    $health | Format-List
    if (-not $health.extensionConnected) { throw "extensionConnected should be true" }

    Write-Host "=== /cookies without auth (expect 401) ===" -ForegroundColor Cyan
    $r = Invoke-WebRequest 'http://127.0.0.1:47899/cookies?domain=example.com' -SkipHttpErrorCheck
    Write-Host "  status: $($r.StatusCode)"
    if ($r.StatusCode -ne 401) { throw "expected 401 got $($r.StatusCode)" }

    Write-Host "=== /cookies with auth (extension reply) ===" -ForegroundColor Cyan

    # Read the next outgoing frame from the host on a thread.
    $responder = Start-ThreadJob -ScriptBlock {
        param($p)
        $stream = $p.StandardOutput.BaseStream
        $lenBuf = New-Object byte[] 4
        $r = 0; while ($r -lt 4) { $n = $stream.Read($lenBuf, $r, 4 - $r); if ($n -le 0) { return }; $r += $n }
        $len = [BitConverter]::ToUInt32($lenBuf, 0)
        $buf = New-Object byte[] $len
        $r = 0; while ($r -lt $len) { $n = $stream.Read($buf, $r, $len - $r); if ($n -le 0) { return }; $r += $n }
        return ([System.Text.Encoding]::UTF8.GetString($buf) | ConvertFrom-Json).id
    } -ArgumentList $proc

    $req = Start-ThreadJob -ScriptBlock {
        param($t)
        Invoke-RestMethod -Headers @{Authorization="Bearer $t"} `
            'http://127.0.0.1:47899/cookies?domain=example.com'
    } -ArgumentList $token

    $id = Wait-Job $responder | Receive-Job
    Write-Host "  extension saw request id=$id"
    $reply = @{
        id = $id
        ok = $true
        cookies = @(
            @{ name='sid'; value='abc'; domain='.example.com'; path='/'; expires=$null;
               httpOnly=$true; secure=$true; sameSite='lax'; session=$false }
            @{ name='cid'; value='xyz'; domain='.example.com'; path='/'; expires=$null;
               httpOnly=$false; secure=$true; sameSite='lax'; session=$false }
        )
    }
    Send-Frame $reply

    $got = Wait-Job $req | Receive-Job
    $got | Format-List
    if ($got.header -ne 'sid=abc; cid=xyz') { throw "header mismatch: '$($got.header)'" }

    Write-Host "OK" -ForegroundColor Green
}
finally {
    try { $proc.StandardInput.Close() } catch {}
    Start-Sleep -Milliseconds 500
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
