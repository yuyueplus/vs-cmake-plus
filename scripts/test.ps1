param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $testArguments = @('run', '--project', 'tests/CMakePlus.Tests')
    if ($Integration) { $testArguments += @('--', '--integration') }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw "测试失败：$LASTEXITCODE" }
} finally { Pop-Location }
