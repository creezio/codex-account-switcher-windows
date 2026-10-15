[CmdletBinding()]
param([ValidateSet('certivan','pegolotti')][string]$Instance='certivan',[string]$EngineAssembly='work\test-bin\Tests.exe',[ValidateSet('inventory','inventory-production','cloud-start','cloud-follow','local','local-fresh','name-check','inspect-local','inspect-bind','local-utf8','local-bind','local-bind-fixed')][string]$Phase='inventory')
$ErrorActionPreference='Stop'
$taskProject=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWork=Join-Path $taskProject 'work'
$taskEngine=[IO.Path]::GetFullPath((Join-Path $taskProject $EngineAssembly))
$taskBinary=Join-Path $taskWork 'test-bin\HeadlessAcceptance.exe'
$taskReceipt=Join-Path $taskWork ('headless-closed-'+$Instance+'.json')
if($Phase -eq 'inventory-production'){$taskReceipt=Join-Path $taskWork ('headless-production-'+$Instance+'.json')}
if($Phase -eq 'local' -or $Phase -eq 'name-check'){$taskReceipt=Join-Path $taskWork ('headless-local-'+$Instance+'.json')}
if($Phase -eq 'local-fresh' -or $Phase -eq 'inspect-local'){$taskReceipt=Join-Path $taskWork ('headless-local-'+$Instance+'-fresh.json')}
if($Phase -eq 'local-utf8'){$taskReceipt=Join-Path $taskWork ('headless-local-'+$Instance+'-utf8.json')}
if($Phase -eq 'local-bind'){$taskReceipt=Join-Path $taskWork ('headless-local-'+$Instance+'-bind.json')}
if($Phase -eq 'local-bind-fixed'){$taskReceipt=Join-Path $taskWork ('headless-local-'+$Instance+'-bind-fixed.json')}
if($Phase -eq 'inspect-bind'){$taskReceipt=Join-Path $taskWork ('headless-local-'+$Instance+'-bind-read.json');if(-not(Test-Path -LiteralPath $taskReceipt)){ $taskPrior=Get-Content -LiteralPath (Join-Path $taskWork ('headless-local-'+$Instance+'-bind.json')) -Raw|ConvertFrom-Json; $taskPrior|Select-Object StartedUtc,UpdatedUtc,ForcedRefreshSucceeded,AccountKeyHash,LocalThreadId,LocalTurnId|ConvertTo-Json|Set-Content -LiteralPath $taskReceipt -Encoding utf8 }}
if($Phase -eq 'cloud-start' -or $Phase -eq 'cloud-follow'){
    if($Instance -ne 'certivan'){throw 'La mission Cloud de recette est limitée au propriétaire Certivan et à son environnement laboratoire existant.'}
    $taskInventory=Get-Content -LiteralPath $taskReceipt -Raw|ConvertFrom-Json
    if($taskInventory.State -ne 'passed' -or -not $taskInventory.TargetDesktopFinallyClosed -or -not $taskInventory.ServerEmailMatchesProfile){throw 'La qualification du profil fermé doit avoir réussi avant la mission.'}
    $taskVault=Join-Path $env:LOCALAPPDATA 'Creezio\CodexAccountSwitcher'
    $taskHome=Join-Path $taskVault ('instances\'+$taskInventory.TargetInstanceId+'\codex-home')
    function Assert-TargetClosed {
        $taskStatePath=Join-Path (Split-Path -Parent $taskHome) 'state.json'
        if(Test-Path -LiteralPath $taskStatePath){
            $taskDesktop=Get-Content -LiteralPath $taskStatePath -Raw|ConvertFrom-Json
            $taskLive=Get-Process -Id $taskDesktop.DesktopPid -ErrorAction SilentlyContinue
            if($taskLive -and $taskLive.StartTime.ToUniversalTime().Ticks -eq $taskDesktop.DesktopStartTicks){throw 'Le propriétaire a été ouvert ; la recette fermée ne démarre aucun travail supplémentaire.'}
        }
    }
    Assert-TargetClosed
    $taskCloudPath=Join-Path $taskWork 'headless-cloud-acceptance.json'
    $taskDotnet=Join-Path $env:LOCALAPPDATA 'Creezio\tools\dotnet\dotnet.exe'
    $taskDll=Join-Path $taskProject 'outputs\v0.18.0-beta.1\CodexAccountSwitcher.dll'
    $taskCloudPeakResident=0L;$taskCloudPeakPrivate=0L
    function Invoke-HeadlessCloud($taskRequest){
        $taskLaunch=[Diagnostics.ProcessStartInfo]::new($taskDotnet)
        $taskLaunch.ArgumentList.Add($taskDll);$taskLaunch.ArgumentList.Add('--relay')
        $taskLaunch.UseShellExecute=$false;$taskLaunch.CreateNoWindow=$true
        $taskLaunch.RedirectStandardInput=$true;$taskLaunch.RedirectStandardOutput=$true;$taskLaunch.RedirectStandardError=$true
        foreach($taskKey in @('CODEX_THREAD_ID','CODEX_APP_TOOLS_PIPE_PATH','OPENAI_API_KEY','CODEX_API_KEY','CODEX_ACCESS_TOKEN','OPENAI_IDENTITY_TOKEN','OPENAI_IDENTITY_TOKEN_FILE','OPENAI_IDENTITY_PROVIDER_ID','OPENAI_ORGANIZATION','CHATGPT_BASE_URL','CODEX_CHATGPT_BASE_URL')){[void]$taskLaunch.Environment.Remove($taskKey)}
        $taskProcess=[Diagnostics.Process]::Start($taskLaunch)
        try{
            $taskOutput=$taskProcess.StandardOutput.ReadToEndAsync();$taskErrors=$taskProcess.StandardError.ReadToEndAsync()
            $taskProcess.StandardInput.WriteLine(($taskRequest|ConvertTo-Json -Depth 12 -Compress));$taskProcess.StandardInput.Close()
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(70)
            while(-not $taskProcess.WaitForExit(100)){
                $taskProcess.Refresh();$script:taskCloudPeakResident=[Math]::Max($script:taskCloudPeakResident,$taskProcess.WorkingSet64);$script:taskCloudPeakPrivate=[Math]::Max($script:taskCloudPeakPrivate,$taskProcess.PrivateMemorySize64)
                if([DateTime]::UtcNow -gt $taskDeadline){$taskProcess.Kill();throw 'Le seul contrôleur Cloud de recette a dépassé son délai ; ne pas recréer la mission.'}
            }
            $taskResult=$taskOutput.GetAwaiter().GetResult()|ConvertFrom-Json;[void]$taskErrors.GetAwaiter().GetResult()
            if($taskProcess.ExitCode -ne 0 -or $taskResult.error){throw 'La commande Cloud de recette a échoué ; aucun renvoi automatique.'}
            return $taskResult
        }finally{$taskProcess.Dispose()}
    }
    function Save-CloudEvidence {
        $taskCloud.UpdatedUtc=[DateTime]::UtcNow.ToString('o')
        $taskCloud.ControllerPeakResidentBytes=[Math]::Max($taskCloud.ControllerPeakResidentBytes,$script:taskCloudPeakResident)
        $taskCloud.ControllerPeakPrivateBytes=[Math]::Max($taskCloud.ControllerPeakPrivateBytes,$script:taskCloudPeakPrivate)
        $taskCloud|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $taskCloudPath -Encoding utf8
    }
    if(Test-Path -LiteralPath $taskCloudPath){$taskCloud=Get-Content -LiteralPath $taskCloudPath -Raw|ConvertFrom-Json}
    else{
        $taskSetup=Get-Content -LiteralPath (Join-Path $taskWork 'cloud-native-setup-acceptance.json') -Raw|ConvertFrom-Json
        $taskConfiguration=@($taskSetup.Targets|Where-Object {$_.Instance -eq 'certivan' -and $_.State -eq 'completed'})
        if($taskConfiguration.Count -ne 1){throw 'Un seul environnement laboratoire Certivan précédemment publié est requis.'}
        $taskConfig=Invoke-HeadlessCloud @{operation='cloud-read';instance='certivan';configurationId=$taskConfiguration[0].ConfigurationId}
        if($taskConfig.Status -ne 'ready' -or $taskConfig.VersionRevision -le 1 -or @($taskConfig.Repositories).Count -ne 1 -or $taskConfig.Repositories[0].RepositoryId -ne 'github-1420368078'){throw 'Environnement non conforme au laboratoire autorisé.'}
        $taskRepository=Invoke-HeadlessCloud @{operation='cloud-github-repository';instance='certivan';repositoryId=$taskConfig.Repositories[0].RepositoryId}
        if($taskRepository.FullName -ne 'creezio/codex-cloud-switcher-lab-20261015'){throw 'Le dépôt diffère du laboratoire explicitement autorisé.'}
        $taskCloud=[pscustomobject]@{Id=[guid]::NewGuid().ToString('N');Instance='certivan';ConfigurationId=$taskConfig.Id;Repository=$taskRepository.FullName;State='not-sent';ThreadId=$null;TurnId=$null;ResultMatches=$false;AccountMatchesInventory=$false;TargetDesktopClosed=$true;AuthHashBefore=(Get-FileHash -LiteralPath (Join-Path $taskHome 'auth.json') -Algorithm SHA256).Hash;AuthBytesUnchanged=$null;ControllerPeakResidentBytes=0L;ControllerPeakPrivateBytes=0L;StartedUtc=[DateTime]::UtcNow.ToString('o');UpdatedUtc=$null}
        Save-CloudEvidence
    }
    if($Phase -eq 'cloud-start' -and $taskCloud.State -eq 'not-sent'){
        $taskPrompt='Acceptance mission HEADLESS-CLOSED-20261015. This is a short fictitious test, not a repository change. Reply with exactly: HEADLESS_OK 2+3=5. Do not use tools, read files, run commands, modify files, commit, push, create a pull request or publish anything. The pre-existing environment belongs solely to the authorized laboratory creezio/codex-cloud-switcher-lab-20261015.'
        $taskJob=Invoke-HeadlessCloud @{operation='cloud-job-submit';startWorker=$false;job=@{Id=$taskCloud.Id;Instance='certivan';ConfigurationId=$taskCloud.ConfigurationId;Title='Acceptance headless owner closed';Prompt=$taskPrompt;ExplicitDelegation=$true;ReturnToSource=$false}}
    }elseif($Phase -eq 'cloud-follow'){
        $taskJob=Invoke-HeadlessCloud @{operation='cloud-job-poll';id=$taskCloud.Id}
    }else{$taskJob=Invoke-HeadlessCloud @{operation='cloud-job-read';id=$taskCloud.Id}}
    $taskCloud.State=$taskJob.State;$taskCloud.ThreadId=$taskJob.ThreadId;$taskCloud.TurnId=$taskJob.TurnId
    if($taskJob.State -eq 'completed'){
        $taskThread=Invoke-HeadlessCloud @{operation='cloud-task-read';instance='certivan';threadId=$taskJob.ThreadId}
        $taskSha=[Security.Cryptography.SHA256]::Create();try{$taskKeyHash=[Convert]::ToHexString($taskSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskThread.AccountKey))).ToLowerInvariant()}finally{$taskSha.Dispose()}
        $taskCloud.AccountMatchesInventory=$taskKeyHash -eq $taskInventory.AccountKeyHash -and $taskThread.VerifiedEnvironmentConfigId -eq $taskCloud.ConfigurationId -and $taskThread.Id -eq $taskCloud.ThreadId
        $taskCloud.ResultMatches=$taskJob.Result.Trim() -eq 'HEADLESS_OK 2+3=5' -and $taskThread.LatestTurn.Id -eq $taskCloud.TurnId -and $taskThread.LatestTurn.Status -eq 'completed' -and $taskThread.LatestTurn.TextComplete
        if(-not $taskCloud.AccountMatchesInventory -or -not $taskCloud.ResultMatches){$taskCloud.State='verification-failed'}
    }
    Assert-TargetClosed
    $taskCloud.AuthBytesUnchanged=(Get-FileHash -LiteralPath (Join-Path $taskHome 'auth.json') -Algorithm SHA256).Hash -eq $taskCloud.AuthHashBefore
    $taskMissingRoots=@($taskInventory.ProtectedGuiRoots|Where-Object {$taskRoot=$_;$taskRunning=Get-Process -Id $taskRoot.Pid -ErrorAction SilentlyContinue; -not $taskRunning -or $taskRunning.StartTime.ToUniversalTime().Ticks -ne $taskRoot.StartedUtcTicks})
    $taskCloud|Add-Member -NotePropertyName ProtectedGuiRootsPreserved -NotePropertyValue ($taskMissingRoots.Count -eq 0) -Force
    if($taskMissingRoots.Count -gt 0){$taskCloud.State='verification-failed'}
    Save-CloudEvidence
    $taskCloud|Select-Object Id,Instance,State,ThreadId,TurnId,AccountMatchesInventory,ResultMatches,AuthBytesUnchanged,TargetDesktopClosed|ConvertTo-Json -Compress
    return
}
if(Get-Process -Name HeadlessAcceptance -ErrorAction SilentlyContinue){throw 'Le helper de recette travaille déjà ; aucun deuxième lancement.'}
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $taskCompiler /nologo /target:exe /warn:4 /warnaserror+ /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll ('/out:'+$taskBinary) (Join-Path $taskProject 'tests\HeadlessAcceptance.cs')
if($LASTEXITCODE -ne 0){throw 'Échec de compilation du seul helper de recette.'}
if($Phase -in @('inventory-production','local','name-check','inspect-local','inspect-bind','local-utf8','local-bind','local-bind-fixed')){& $taskBinary $taskEngine $Instance $taskReceipt $taskWork $Phase}
elseif($Phase -eq 'local-fresh'){& $taskBinary $taskEngine $Instance $taskReceipt $taskWork local}
else{& $taskBinary $taskEngine $Instance $taskReceipt $taskWork}
if($LASTEXITCODE -ne 0){throw ('Recette refusée ; diagnostic expurgé dans '+$taskReceipt)}
