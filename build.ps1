param([switch]$Test)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $refs = @('/r:System.Security.dll','/r:System.Management.dll','/r:System.Web.Extensions.dll')
    New-Item -ItemType Directory -Force dist | Out-Null
    & $compiler /nologo /target:winexe /out:dist\MedQueue.exe @refs src\Core.cs src\Cloud.cs src\Dashboard.cs
    if ($LASTEXITCODE) { throw 'App build failed.' }
    & $compiler /nologo /target:winexe /out:dist\MedQueueSetup.exe /resource:dist\MedQueue.exe,MedQueuePayload @refs src\Core.cs src\Setup.cs
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
    if ($Test) {
        & $compiler /nologo /target:exe /out:dist\Smoke.exe @refs src\Core.cs src\Cloud.cs tests\Smoke.cs
        if ($LASTEXITCODE) { throw 'Test build failed.' }
        & ./dist\Smoke.exe
        if ($LASTEXITCODE) { throw 'Test run failed.' }
    }
} finally { Pop-Location }
