# Applies the v3.0 control-plane draft to a throwaway PostgreSQL 16 container and runs saas-smoke.sql.
# Order mirrors a real deployment: cluster bootstrap and its addendum as superuser, then each migration as
# ti_migrator acting as ti_owner in ONE transaction (like TrustedInventory.Migrator), with smoke stages in
# between to check the expand/contract steps. Exit code is non-zero on any failure; the container is
# always removed. Native commands are checked through $LASTEXITCODE (PowerShell 5.1 would otherwise turn
# their stderr into terminating errors).
param([switch]$KeepOnFailure)   # leave the container running to inspect a failure
$ErrorActionPreference = 'Continue'
$name = 'ti-saas-smoke'
$password = [guid]::NewGuid().ToString('N')   # synthetic, discarded with the container
$design = $PSScriptRoot
$db = Split-Path $design -Parent

function Get-Sections([string]$path) {
    # Splits a file on "-- @@ section <name> as <superuser|migrator>" markers.
    $sections = [ordered]@{}
    $current = $null
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^-- @@ section (\S+) as (superuser|migrator)\s*$') {
            $current = $Matches[1]
            $sections[$current] = @{ Role = $Matches[2]; Sql = New-Object Text.StringBuilder }
        }
        elseif ($current) {
            [void]$sections[$current].Sql.AppendLine($line)
        }
    }
    $sections
}

function Invoke-Step([string]$label, [string]$role, [string]$sql) {
    Write-Host "== $label ($role)"
    if ($role -eq 'migrator') {
        ("SET ROLE ti_owner;`n" + $sql) |
            docker exec -i $name psql -U ti_migrator -d postgres -1 -v ON_ERROR_STOP=1 -q -X
    }
    else {
        $sql | docker exec -i $name psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q -X -t
    }
    if ($LASTEXITCODE -ne 0) { throw "$label failed (exit $LASTEXITCODE)" }
}

$draft = Get-Sections (Join-Path $design 'saas-schema-draft.sql')
$smoke = Get-Sections (Join-Path $design 'saas-smoke.sql')

docker rm -f $name *> $null
docker run -d --name $name -e "POSTGRES_PASSWORD=$password" postgres:16 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'docker run failed (is Docker running?)' }
$failed = $true
try {
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        Start-Sleep -Seconds 1
        docker exec $name pg_isready -U postgres -q *> $null
        $ready = $LASTEXITCODE -eq 0
    }
    if (-not $ready) { throw 'PostgreSQL did not become ready' }
    Start-Sleep -Seconds 2   # the image restarts once after initdb

    Invoke-Step '0000_cluster' 'superuser' ([IO.File]::ReadAllText((Join-Path $db 'bootstrap\0000_cluster.sql')))
    Invoke-Step 'bootstrap-addendum' 'superuser' $draft['bootstrap-addendum'].Sql.ToString()
    foreach ($m in Get-ChildItem (Join-Path $db 'migrations') -Filter '*.sql' | Sort-Object Name) {
        Invoke-Step $m.BaseName 'migrator' ([IO.File]::ReadAllText($m.FullName))
    }
    Invoke-Step 'smoke: seed-v2' 'superuser' $smoke['seed-v2'].Sql.ToString()
    Invoke-Step '0003_saas_enum_values' 'migrator' $draft['0003_saas_enum_values'].Sql.ToString()
    Invoke-Step '0004_saas_control_plane' 'migrator' $draft['0004_saas_control_plane'].Sql.ToString()
    Invoke-Step 'smoke: expand-checks' 'superuser' $smoke['expand-checks'].Sql.ToString()
    Invoke-Step '0005_tenant_status_contract' 'migrator' $draft['0005_tenant_status_contract'].Sql.ToString()
    Invoke-Step 'smoke: checks' 'superuser' $smoke['checks'].Sql.ToString()
    $failed = $false
}
finally {
    if ($failed -and $KeepOnFailure) { Write-Host "container $name kept for inspection" }
    else { docker rm -f $name *> $null }
}
