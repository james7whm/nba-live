param([switch]$SkipTests, [string]$Version = '1.2.4', [string]$ArtifactSuffix = '', [switch]$Compatible, [switch]$Lite)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a numeric major.minor.patch value.' }
if ($ArtifactSuffix -notmatch '^[a-z0-9-]*$') { throw 'ArtifactSuffix must use lowercase letters, numbers and hyphens.' }
if ($Compatible -and $Lite) { throw 'Choose either Lite or Compatible.' }
$taskPackage = "NbaLive-$Version" + $(if ($ArtifactSuffix) { "-$ArtifactSuffix" } elseif ($Compatible) { '-compatibility-preview' } else { '' }) + $(if($Compatible){'-net48'}elseif($Lite){'-lite'}else{''}) + '-win-x64'
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
$taskRuntimeVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'installer/runtime.json') -Raw | ConvertFrom-Json).version
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $taskRoot
try {
    if (-not $SkipTests) {
        & $taskDotnet run --project tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        if($Compatible){
            & $taskDotnet build tests-compat -c Release
            if($LASTEXITCODE -ne 0){throw 'Compatibility test build failed.'}
            & (Join-Path $taskRoot 'tests-compat\bin\Release\net48\NbaLive.CompatTests.exe')
            if($LASTEXITCODE -ne 0){throw 'Compatibility tests failed.'}
        }
    }
    $taskOutput = Join-Path $taskRoot ("artifacts\$taskPackage")
    if (Test-Path -LiteralPath $taskOutput) {
        $taskResolvedOutput = (Resolve-Path -LiteralPath $taskOutput).Path
        $taskArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
        if (-not $taskResolvedOutput.StartsWith($taskArtifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory is outside artifacts.' }
        $taskRunning = Get-Process NbaLive -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $taskOutput 'NbaLive.exe') }
        if ($taskRunning) { throw 'Close the packaged app before rebuilding it.' }
        Remove-Item -LiteralPath $taskResolvedOutput -Recurse -Force
    }
    if($Compatible){
        & $taskDotnet publish src\NbaLive\NbaLive.csproj -f net48 -c Release -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $taskOutput
    }elseif($Lite){
        & $taskDotnet publish src\NbaLive\NbaLive.csproj -f net10.0-windows -c Release -r win-x64 --self-contained false -p:LitePackage=true -p:RuntimeFrameworkVersion=$taskRuntimeVersion -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $taskOutput
    }else{
        & $taskDotnet publish src\NbaLive\NbaLive.csproj -f net10.0-windows -c Release -r win-x64 --self-contained true -p:LitePackage=false -p:RuntimeFrameworkVersion=$taskRuntimeVersion -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $taskOutput
    }
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput
    New-Item -ItemType Directory -Force (Join-Path $taskOutput 'docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\VERIFICATION.md') -Destination (Join-Path $taskOutput 'docs\VERIFICATION.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\TEAM-COLORS.md') -Destination (Join-Path $taskOutput 'docs\TEAM-COLORS.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\COMPATIBILITY.md') -Destination (Join-Path $taskOutput 'docs\COMPATIBILITY.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\PLAYER-DETAIL.md') -Destination (Join-Path $taskOutput 'docs\PLAYER-DETAIL.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\UPDATES.md') -Destination (Join-Path $taskOutput 'docs\UPDATES.md')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\app-icon.png') -Destination (Join-Path $taskOutput 'docs\app-icon.png')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\screenshots') -Destination (Join-Path $taskOutput 'docs') -Recurse
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\releases') -Destination (Join-Path $taskOutput 'docs') -Recurse
    $taskDotnetRoot = Split-Path -Parent $taskDotnet
    Copy-Item -LiteralPath (Join-Path $taskDotnetRoot 'LICENSE.txt') -Destination (Join-Path $taskOutput 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $taskDotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $taskOutput 'DOTNET-ThirdPartyNotices.txt')
    $taskManifestFiles = [ordered]@{}
    Get-ChildItem -LiteralPath $taskOutput -File -Recurse | Sort-Object FullName | ForEach-Object {
        $taskRelative = $_.FullName.Substring($taskOutput.Length + 1).Replace('\','/')
        $taskManifestFiles[$taskRelative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $taskManifest = [ordered]@{ Product='NbaLive'; Version=$Version; Variant=$(if($Compatible){'net48'}elseif($Lite){'modern-lite'}else{'modern'}); Files=$taskManifestFiles }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'NbaLive.package.json'),($taskManifest | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    $taskZip = Join-Path $taskRoot ("artifacts\$taskPackage.zip")
    Compress-Archive -LiteralPath $taskOutput -DestinationPath $taskZip -Force
    $taskHash = Get-FileHash -LiteralPath $taskZip -Algorithm SHA256
    ('{0}  {1}' -f $taskHash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($taskZip)) | Set-Content -LiteralPath ($taskZip + '.sha256') -Encoding ascii
    $taskHash | Format-List
    Write-Output $taskZip
}
finally { Pop-Location }
