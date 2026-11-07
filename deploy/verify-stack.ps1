# Verifies the running dev stack: Keycloak login (password + TOTP), token claims, and tenant isolation via the API.
# SYNTHETIC development check only. Usage: powershell -File deploy/verify-stack.ps1 -EnvFile .env [-User alice] [-NoOtp]
param([string]$EnvFile, [string]$User = 'alice', [switch]$NoOtp)
$ErrorActionPreference = 'Stop'
$cfg = @{}
Get-Content $EnvFile | Where-Object { $_ -match '^\w+=' } | ForEach-Object { $k, $v = $_ -split '=', 2; $cfg[$k] = $v }

function Get-Totp([string]$secret) {
    $key = [Text.Encoding]::UTF8.GetBytes($secret)
    $counter = [long][Math]::Floor(([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) / 30)
    $bytes = [BitConverter]::GetBytes($counter); [Array]::Reverse($bytes)
    $hmac = New-Object Security.Cryptography.HMACSHA1 (, $key)
    $h = $hmac.ComputeHash($bytes)
    $o = $h[19] -band 0x0f
    $bin = (($h[$o] -band 0x7f) -shl 24) -bor (($h[$o + 1] -band 0xff) -shl 16) -bor (($h[$o + 2] -band 0xff) -shl 8) -bor ($h[$o + 3] -band 0xff)
    ($bin % 1000000).ToString('000000')
}

function Decode-Jwt([string]$jwt) {
    $p = $jwt.Split('.')[1].Replace('-', '+').Replace('_', '/')
    switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
}

$body = @{ grant_type = 'password'; client_id = 'trustedinventory-pwa'; username = $User; password = $cfg.TI_DEV_USER_PASSWORD; scope = 'openid' }
if (-not $NoOtp) { $body.otp = Get-Totp $cfg.TI_DEV_OTP_SECRET }
try {
    $token = Invoke-RestMethod -Method Post -Uri 'http://localhost:8080/realms/trustedinventory/protocol/openid-connect/token' -Body $body
} catch {
    "token request failed: $($_.Exception.Response.StatusCode.value__) $($_.ErrorDetails.Message)"
    return
}
$claims = Decode-Jwt $token.access_token
"token: iss=$($claims.iss) aud=$(($claims.aud) -join ',') sub=$($claims.sub) amr=$(($claims.amr) -join ',') acr=$($claims.acr)"

$h = @{ Authorization = "Bearer $($token.access_token)" }
if ($env:TI_VERIFY_CREATE) {
    $r = Invoke-WebRequest -UseBasicParsing -Method Post -Uri 'http://localhost:8081/api/v1/warehouses' -ContentType 'application/json' `
        -Headers ($h + @{ 'X-Tenant-Id' = '10000000-0000-4000-8000-00000000000a' }) -Body $env:TI_VERIFY_CREATE
    "POST /api/v1/warehouses: $($r.StatusCode) ETag=$($r.Headers.ETag)"
}
foreach ($call in @(
    @{ name = 'GET /api/v1/me'; uri = 'http://localhost:8081/api/v1/me'; headers = $h },
    @{ name = 'GET /api/v1/warehouses (tenant A)'; uri = 'http://localhost:8081/api/v1/warehouses'; headers = ($h + @{ 'X-Tenant-Id' = '10000000-0000-4000-8000-00000000000a' }) },
    @{ name = 'GET /api/v1/warehouses (tenant B)'; uri = 'http://localhost:8081/api/v1/warehouses'; headers = ($h + @{ 'X-Tenant-Id' = '10000000-0000-4000-8000-00000000000b' }) })) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $call.uri -Headers $call.headers
        "$($call.name): $($r.StatusCode) $($r.Content.Substring(0, [Math]::Min(300, $r.Content.Length)))"
    } catch {
        "$($call.name): $($_.Exception.Response.StatusCode.value__) $($_.ErrorDetails.Message)"
    }
}
