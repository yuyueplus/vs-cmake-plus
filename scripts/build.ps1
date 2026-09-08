param(
    [ValidateSet('2022', '2026')][string]$VisualStudio = '2022',
    [switch]$DeployExperimental
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instances = & $vswhere -all -format json | ConvertFrom-Json
$major = if ($VisualStudio -eq '2022') { '17.' } else { '18.' }
$instance = $instances | Where-Object { $_.installationVersion.StartsWith($major) } | Select-Object -First 1
if (-not $instance) { throw "未找到 Visual Studio $VisualStudio" }
$msbuild = Join-Path $instance.installationPath 'MSBuild\Current\Bin\MSBuild.exe'
$buildArguments = @((Join-Path $projectRoot 'src\CMakePlus.Vsix\CMakePlus.Vsix.csproj'), '/restore', '/t:Rebuild', '/p:Configuration=Release', '/nologo', '/v:minimal')
if ($DeployExperimental) {
    $buildArguments += @('/p:DeployExtension=true', "/p:DeployTargetInstanceId=$($instance.instanceId)", '/p:VSSDKTargetPlatformRegRootSuffix=CMakePlus')
}
Push-Location $projectRoot
try {
    & $msbuild @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$LASTEXITCODE" }
    $package = Join-Path $projectRoot 'src\CMakePlus.Vsix\bin\Release\net48\CMakePlus.vsix'
    if (-not (Test-Path -LiteralPath $package)) { throw '构建没有生成 VSIX。' }
    $output = Join-Path $projectRoot 'artifacts\release'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CMakePlus.Vsix\source.extension.vsixmanifest') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $reader = [IO.StreamReader]::new($archive.GetEntry('extension.vsixmanifest').Open())
        try { [xml]$packedManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($packedManifest.PackageManifest.Metadata.Identity.Version -ne $manifest.PackageManifest.Metadata.Identity.Version) {
            throw 'VSIX 包内版本与源码清单不一致，停止发布。'
        }
    } finally { $archive.Dispose() }
    $packageName = "CMakePlus-$($manifest.PackageManifest.Metadata.Identity.Version).vsix"
    Copy-Item -LiteralPath $package -Destination (Join-Path $output $packageName)
    Write-Host "安装包：$(Join-Path $output $packageName)"
} finally { Pop-Location }
