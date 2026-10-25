param([string]$Version='1.2.3')
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or !$env:RUNNER_TEMP){throw 'Run only on a disposable GitHub Windows runner; this test installs a Microsoft runtime.'}
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskScratch=Join-Path $env:RUNNER_TEMP ('NbaLive-Installer-Check-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskScratch|Out-Null
$taskHeaders=@{'User-Agent'='NbaLive-Installer-Verification';'Authorization'=('Bearer '+$env:GH_TOKEN)}
$taskRelease=Invoke-RestMethod -Uri "https://api.github.com/repos/james7whm/nba-live/releases/tags/v$Version" -Headers $taskHeaders
$taskName="NbaLive-$Version-online-setup-win-x64.exe"
$taskAsset=@($taskRelease.assets|Where-Object name -eq $taskName)
if($taskAsset.Count -ne 1 -or $taskRelease.draft -or $taskRelease.prerelease){throw 'Expected one published installer'}
$taskInstaller=Join-Path $taskScratch $taskName
Invoke-WebRequest -Uri $taskAsset[0].browser_download_url -OutFile $taskInstaller
if($taskAsset[0].digest -ne ('sha256:'+(Get-FileHash -LiteralPath $taskInstaller).Hash.ToLowerInvariant())){throw 'Published installer digest mismatch'}
function Run-Setup([string[]]$Arguments){
    $taskProcess=Start-Process -FilePath $taskInstaller -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    return $taskProcess.ExitCode
}
$taskBeforeLog=Join-Path $taskScratch 'runtime-before.log'
[void](Run-Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/RUNTIMESELFTEST=1',('/LOG="'+$taskBeforeLog+'"')))
if(!(Select-String -LiteralPath $taskBeforeLog -SimpleMatch 'NBA_RUNTIME_TEST_PASS')){throw 'Dependency self-test failed'}
$taskInstallDir=Join-Path $taskScratch 'app'
if(Select-String -LiteralPath $taskBeforeLog -SimpleMatch 'NBA_RUNTIME_MISSING'){
    $taskMissingLog=Join-Path $taskScratch 'runtime-missing.log'
    $taskMissingCode=Run-Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$taskInstallDir+'"'),('/LOG="'+$taskMissingLog+'"'))
    if($taskMissingCode -eq 0 -or (Test-Path -LiteralPath (Join-Path $taskInstallDir 'NbaLive.exe'))){throw 'Missing runtime did not block unattended app installation'}
    'PASS Missing runtime blocks unattended installation without writing app files.'
}
$taskRuntime=Get-Content -LiteralPath (Join-Path $taskRoot 'installer/runtime.json') -Raw|ConvertFrom-Json
$taskRuntimeExe=Join-Path $taskScratch 'microsoft-desktop-runtime.exe'
Invoke-WebRequest -Uri $taskRuntime.url -OutFile $taskRuntimeExe
if((Get-FileHash -LiteralPath $taskRuntimeExe -Algorithm SHA512).Hash.ToLowerInvariant() -ne $taskRuntime.sha512){throw 'Runtime digest mismatch'}
$taskSignature=Get-AuthenticodeSignature -LiteralPath $taskRuntimeExe
if($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw 'Microsoft runtime signature invalid'}
$taskRuntimeProcess=Start-Process -FilePath $taskRuntimeExe -ArgumentList @('/install','/quiet','/norestart') -WindowStyle Hidden -PassThru -Wait
if($taskRuntimeProcess.ExitCode -ne 0){throw ('Disposable runner runtime installation returned '+$taskRuntimeProcess.ExitCode)}
$taskData=Join-Path $env:LOCALAPPDATA 'NbaLive'
New-Item -ItemType Directory -Path $taskData -Force|Out-Null
$taskMarker=Join-Path $taskData 'installer-preservation-probe.txt'
[IO.File]::WriteAllText($taskMarker,'preserve configuration directory')
$taskInstallLog=Join-Path $taskScratch 'install.log'
$taskCode=Run-Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="'+$taskInstallDir+'"'),('/LOG="'+$taskInstallLog+'"'))
if($taskCode -ne 0){Get-Content -LiteralPath $taskInstallLog -Tail 20;throw ('App installation returned '+$taskCode)}
if(!(Select-String -LiteralPath $taskInstallLog -SimpleMatch 'Compatible runtime found; no download.')){throw 'Installed runtime was not detected'}
$taskManifest=Get-Content -LiteralPath (Join-Path $taskInstallDir 'NbaLive.package.json') -Raw|ConvertFrom-Json
if($taskManifest.Version -ne $Version -or $taskManifest.Variant -ne 'modern-lite' -or (Test-Path -LiteralPath (Join-Path $taskInstallDir 'coreclr.dll'))){throw 'Installer did not deploy the lite payload'}
foreach($taskFile in $taskManifest.Files.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $taskInstallDir $taskFile.Name)).Hash.ToLowerInvariant() -ne $taskFile.Value){throw 'Installed payload differs from manifest'}}
'PASS Real install detects shared runtime and deploys verified lite files.'
$env:TMP=$taskScratch;$env:TEMP=$taskScratch
$taskAppPath=Join-Path $taskInstallDir 'NbaLive.exe'
$taskApp=Start-Process -FilePath $taskAppPath -ArgumentList @('--demo','--settings') -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 5
$taskApp.Refresh()
if($taskApp.HasExited){throw 'Installed app exited during startup'}
if($taskApp.Path -ne $taskAppPath){throw 'Unexpected process path'}
Stop-Process -Id $taskApp.Id
'PASS Installed application starts with system runtime.'
$taskUninstall=Join-Path $taskInstallDir 'unins000.exe'
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($taskUninstall)) -ne [IO.Path]::GetFullPath($taskInstallDir)){throw 'Uninstaller path escaped scratch directory'}
$taskUninstallProcess=Start-Process -FilePath $taskUninstall -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
if($taskUninstallProcess.ExitCode -ne 0 -or (Test-Path -LiteralPath $taskAppPath) -or !(Test-Path -LiteralPath $taskMarker)){throw 'Uninstall or data preservation failed'}
'PASS Uninstall removes application and preserves user data directory.'
