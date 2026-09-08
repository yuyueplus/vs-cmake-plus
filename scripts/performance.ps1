$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet build tests/CMakePlus.Performance -c Release
    if ($LASTEXITCODE -ne 0) { throw '性能测试构建失败。' }
    & .\tests\CMakePlus.Performance\bin\Release\net48\CMakePlus.Performance.exe
    if ($LASTEXITCODE -ne 0) { throw '性能或行为检查失败。' }
} finally { Pop-Location }
