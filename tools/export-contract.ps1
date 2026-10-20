$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverPath = Join-Path $repoRoot 'src\SlothBox.Server\bin\Debug\net10.0\SlothBox.Server.dll'
if (-not (Test-Path -LiteralPath $serverPath)) {
    throw 'Build SlothBox.Server before exporting the contract.'
}
$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
if ($listeners | Where-Object { $_.Port -in @(5080, 5081) }) {
    throw 'Ports 5080 and 5081 must be free so the exporter can start its own server.'
}
$server = Start-Process -FilePath 'dotnet' -ArgumentList @('"' + $serverPath + '"') -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
try {
    $document = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($server.HasExited) { throw 'The export server exited before serving OpenAPI.' }
        try {
            $document = (Invoke-WebRequest -Uri 'http://127.0.0.1:5080/openapi/v1.json' -UseBasicParsing -TimeoutSec 2).Content
            break
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $document) { throw 'The export server did not become ready.' }
    $null = $document | ConvertFrom-Json
    $directory = Join-Path $repoRoot 'contracts'
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $directory 'openapi.json'), $document + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Output 'Exported contracts/openapi.json.'
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id }
}
