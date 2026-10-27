param([string]$Version='1.3.1', [string]$Compiler='')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
$taskPayload=Join-Path $taskRoot "artifacts/NbaLive-$Version-lite-win-x64"
$taskManifest=Get-Content -LiteralPath (Join-Path $taskPayload 'NbaLive.package.json') -Raw | ConvertFrom-Json
if($taskManifest.Version -ne $Version -or $taskManifest.Variant -ne 'modern-lite'){throw 'Build matching lite package first'}
if(!$Compiler){
    $taskCandidates=@("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $Compiler=$taskCandidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
}
if(!$Compiler){throw 'Inno Setup 6 compiler not found. Use the Build release packages GitHub workflow, whose Windows runner has Inno Setup preinstalled.'}
$taskRuntime=Get-Content -LiteralPath (Join-Path $taskRoot 'installer/runtime.json') -Raw | ConvertFrom-Json
if($taskRuntime.version -notmatch '^10\.0\.\d+$' -or $taskRuntime.url -ne "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$($taskRuntime.version)/windowsdesktop-runtime-$($taskRuntime.version)-win-x64.exe" -or $taskRuntime.sha512 -notmatch '^[a-f0-9]{128}$'){throw 'Invalid pinned Microsoft runtime metadata'}
$taskCache=Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
$taskRuntimeFile=Join-Path $taskCache "windowsdesktop-runtime-$($taskRuntime.version)-win-x64.exe"
if(!(Test-Path -LiteralPath $taskRuntimeFile)){Invoke-WebRequest -Uri $taskRuntime.url -OutFile $taskRuntimeFile}
if((Get-FileHash -LiteralPath $taskRuntimeFile -Algorithm SHA512).Hash.ToLowerInvariant() -ne $taskRuntime.sha512){throw 'Runtime SHA512 does not match Microsoft release metadata'}
$taskSignature=Get-AuthenticodeSignature -LiteralPath $taskRuntimeFile
if($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw 'Runtime Microsoft signature invalid'}
$taskHash=(Get-FileHash -LiteralPath $taskRuntimeFile -Algorithm SHA256).Hash.ToLowerInvariant()
$taskDefines=@{
    AppVersion=$Version; SourceRoot=$taskRoot; Payload=$taskPayload; RuntimeVersion=$taskRuntime.version;
    RuntimePatch=($taskRuntime.version -split '\.')[2]; RuntimeUrl=$taskRuntime.url; RuntimeSHA256=$taskHash
}
$taskPrefix=($taskDefines.GetEnumerator() | ForEach-Object {'#define '+$_.Key+' "'+$_.Value+'"'}) -join "`r`n"
# RuntimePatch is numeric Pascal code, even though other defines are strings.
$taskScript=$taskPrefix+"`r`n"+[IO.File]::ReadAllText((Join-Path $taskRoot 'installer/online.iss'))
$taskGenerated=Join-Path $taskRoot 'artifacts/online.generated.iss'
[IO.File]::WriteAllText($taskGenerated,$taskScript,[Text.UTF8Encoding]::new($true))
& $Compiler $taskGenerated
if($LASTEXITCODE -ne 0){throw 'Online installer compilation failed'}
$taskExe=Join-Path $taskRoot "artifacts/NbaLive-$Version-online-setup-win-x64.exe"
if((Get-Item -LiteralPath $taskExe).Length -gt 20MB){throw 'Online installer unexpectedly includes a runtime'}
('{0}  {1}' -f (Get-FileHash -LiteralPath $taskExe).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($taskExe)) | Set-Content -LiteralPath ($taskExe+'.sha256') -Encoding ascii
Write-Output $taskExe
