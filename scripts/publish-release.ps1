param([Parameter(Mandatory)][ValidateSet('Inspect','Draft','Upload','Publish','Verify')][string]$Action,[string]$Commit='', [string]$Version='1.2.4')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
if($env:GITHUB_ACTIONS -ne 'true' -or !$env:GH_TOKEN){throw 'Run this publisher through GitHub Actions'}
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
$taskApi='https://api.github.com/repos/james7whm/nba-live'
$taskTag='v'+$Version
$taskHeaders=@{'User-Agent'='NbaLive-Release';'Authorization'=('Bearer '+$env:GH_TOKEN);'Accept'='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
function Api([string]$Path,[string]$Method='Get',$Body=$null){
    if($null -eq $Body){$taskResponse=Invoke-RestMethod -Uri ($taskApi+$Path) -Method $Method -Headers $taskHeaders}
    else{$taskResponse=Invoke-RestMethod -Uri ($taskApi+$Path) -Method $Method -Headers $taskHeaders -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8)))}
    return $taskResponse
}
function Check-Assets($Release){
    foreach($taskName in @("NbaLive-$Version-win-x64.zip","NbaLive-$Version-win-x64.zip.sha256","NbaLive-$Version-lite-win-x64.zip","NbaLive-$Version-lite-win-x64.zip.sha256","NbaLive-$Version-compatibility-preview-net48-win-x64.zip","NbaLive-$Version-compatibility-preview-net48-win-x64.zip.sha256","NbaLive-$Version-online-setup-win-x64.exe","NbaLive-$Version-online-setup-win-x64.exe.sha256")){
        $taskAsset=@($Release.assets | Where-Object name -eq $taskName)
        if($taskAsset.Count -ne 1 -or $taskAsset[0].state -ne 'uploaded'){throw ('Asset missing or incomplete: '+$taskName)}
        $taskFile=Join-Path $taskRoot ('artifacts\'+$taskName)
        if($taskAsset[0].size -ne (Get-Item -LiteralPath $taskFile).Length){throw ('Asset size mismatch: '+$taskName)}
        $taskDigest='sha256:'+(Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash.ToLowerInvariant()
        if($taskAsset[0].digest -ne $taskDigest){throw ('Remote asset digest mismatch or unavailable: '+$taskName)}
    }
}
try{
    $taskReleases=@(Api '/releases?per_page=100')
    $taskMatches=@($taskReleases | Where-Object tag_name -eq $taskTag)
    if($taskMatches.Count -gt 1){throw 'More than one matching release'}
    $taskRelease=if($taskMatches.Count){$taskMatches[0]}else{$null}
    switch($Action){
        'Inspect'{
            $taskRepo=Api '';[pscustomobject]@{Permissions=$taskRepo.permissions;ExistingRelease=if($taskRelease){@{Id=$taskRelease.id;Draft=$taskRelease.draft;URL=$taskRelease.html_url}}else{$null};OldReleasePreserved=(@($taskReleases | Where-Object tag_name -eq 'v1.0.0').Count -eq 1)} | ConvertTo-Json -Depth 4
        }
        'Draft'{
            if(!$Commit){throw 'Commit is required'}
            $taskRef=Api ("/git/ref/tags/v"+$Version);$taskTagged=if($taskRef.object.type -eq 'tag'){(Api ('/git/tags/'+$taskRef.object.sha)).object.sha}else{$taskRef.object.sha}
            if($taskTagged -ne $Commit){throw 'Release tag points to a different commit'}
            if($taskRelease){throw 'Release already exists; inspect before resuming'}
            $taskNotes=[IO.File]::ReadAllText((Join-Path $taskRoot ("docs\releases\"+$Version+".md")))
            $taskRelease=Api '/releases' 'Post' @{tag_name=$taskTag;target_commitish=$Commit;name=("NBA 直播条 v"+$Version);body=$taskNotes;draft=$true;prerelease=$false}
            [pscustomobject]@{Id=$taskRelease.id;Draft=$taskRelease.draft;URL=$taskRelease.html_url} | ConvertTo-Json
        }
        'Upload'{
            if(!$taskRelease -or !$taskRelease.draft){throw 'Expected an existing draft release'}
            foreach($taskName in @("NbaLive-$Version-win-x64.zip","NbaLive-$Version-win-x64.zip.sha256","NbaLive-$Version-lite-win-x64.zip","NbaLive-$Version-lite-win-x64.zip.sha256","NbaLive-$Version-compatibility-preview-net48-win-x64.zip","NbaLive-$Version-compatibility-preview-net48-win-x64.zip.sha256","NbaLive-$Version-online-setup-win-x64.exe","NbaLive-$Version-online-setup-win-x64.exe.sha256")){
                $taskExisting=@($taskRelease.assets | Where-Object name -eq $taskName)
                if($taskExisting.Count){
                    $taskFile=Join-Path $taskRoot ('artifacts\'+$taskName)
                    if($taskExisting.Count -ne 1 -or $taskExisting[0].state -ne 'uploaded' -or $taskExisting[0].size -ne (Get-Item -LiteralPath $taskFile).Length -or $taskExisting[0].digest -ne ('sha256:'+(Get-FileHash -LiteralPath $taskFile).Hash.ToLowerInvariant())){throw 'Existing draft attachment differs; refusing replacement'}
                    continue
                }
                $taskUrl=($taskRelease.upload_url -split '\{')[0]+'?name='+[Uri]::EscapeDataString($taskName)
                $taskAsset=Invoke-RestMethod -Method Post -Uri $taskUrl -Headers $taskHeaders -ContentType 'application/octet-stream' -InFile (Join-Path $taskRoot ('artifacts\'+$taskName))
                [pscustomobject]@{Name=$taskAsset.name;Size=$taskAsset.size;State=$taskAsset.state;Digest=$taskAsset.digest} | ConvertTo-Json
            }
            $taskRelease=Api ('/releases/'+$taskRelease.id);Check-Assets $taskRelease
            'All eight draft assets match local SHA256 digests.'
        }
        'Publish'{
            if(!$taskRelease -or !$taskRelease.draft){throw 'Expected an existing draft release'}
            Check-Assets $taskRelease
            $taskRelease=Api ('/releases/'+$taskRelease.id) 'Patch' @{draft=$false;prerelease=$false;make_latest='true'}
            [pscustomobject]@{Id=$taskRelease.id;Draft=$taskRelease.draft;Prerelease=$taskRelease.prerelease;URL=$taskRelease.html_url} | ConvertTo-Json
        }
        'Verify'{
            $taskLatest=Api '/releases/latest'
            if($taskLatest.tag_name -ne $taskTag -or $taskLatest.draft -or $taskLatest.prerelease){throw 'Latest stable release mismatch'}
            Check-Assets $taskLatest
            foreach ($taskOldTag in @('v1.0.0','v1.1.0','v1.2.0','v1.2.1','v1.2.2')) { if(!(@(Api '/releases?per_page=100') | Where-Object tag_name -eq $taskOldTag)){throw ('Old release missing: '+$taskOldTag)} }
            [pscustomobject]@{Release=$taskLatest.html_url;Tag=$taskLatest.tag_name;Assets=@($taskLatest.assets | Select-Object name,size,digest,browser_download_url);OldReleasePreserved=$true} | ConvertTo-Json -Depth 4
        }
    }
}finally{$taskHeaders=$null}
