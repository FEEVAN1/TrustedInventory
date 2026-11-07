# Applies schema-draft.sql to a throwaway PostgreSQL 16 container and runs rls-smoke.sql.
# Exit code is non-zero if either script fails. The container is always removed.
# Native commands are checked through $LASTEXITCODE; PowerShell 5.1 would otherwise turn their
# stderr output into terminating errors.
$ErrorActionPreference = 'Continue'
$name = 'ti-m0-smoke'
$password = [guid]::NewGuid().ToString('N')   # synthetic, discarded with the container
$dir = $PSScriptRoot

docker rm -f $name *> $null
docker run -d --name $name -e "POSTGRES_PASSWORD=$password" postgres:16 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'docker run failed (is Docker running?)' }
try {
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        Start-Sleep -Seconds 1
        docker exec $name pg_isready -U postgres -q *> $null
        $ready = $LASTEXITCODE -eq 0
    }
    if (-not $ready) { throw 'PostgreSQL did not become ready' }
    Start-Sleep -Seconds 2   # the image restarts once after initdb

    foreach ($script in 'schema-draft.sql', 'rls-smoke.sql') {
        Write-Host "== $script"
        Get-Content -Raw (Join-Path $dir $script) |
            docker exec -i $name psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q -X
        if ($LASTEXITCODE -ne 0) { throw "$script failed (exit $LASTEXITCODE)" }
    }
}
finally {
    docker rm -f $name *> $null
}
