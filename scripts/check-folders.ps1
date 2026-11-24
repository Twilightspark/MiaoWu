# 检查 MiaoWu 用到的每个目录在当前上下文里是否可写。
#
# 用法（请在“平时双击 MiaoWu 的那个账户”下运行，不要用提权终端或受限沙箱）：
#   powershell -ExecutionPolicy Bypass -File scripts\internal\check-folders.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\internal\check-folders.ps1 -Workspace D:\work\demo
#
# 只做“创建 / 删除一个探针文件”，不改任何权限；输出 FOLDER_OK / FOLDER_DENIED 行。
# 低完整性（Low）上下文里的 DENIED 不能说明目录真的没权限——那是 Windows 的完整性级别
# 策略在拒绝写入，换到普通桌面（Medium）再跑一次即可区分。

param(
    [string]$Workspace = ""
)

$ErrorActionPreference = "Continue"

function Get-IntegrityLevel {
    $match = (whoami /groups) | Select-String -Pattern 'S-1-16-(\d+)' | Select-Object -First 1
    if (-not $match) { return 'Unknown' }
    $rid = $match.Matches[0].Groups[1].Value
    switch ($rid) {
        '0' { return 'Untrusted' }
        '4096' { return 'Low' }
        '8192' { return 'Medium' }
        '12288' { return 'High' }
        '16384' { return 'System' }
        '20480' { return 'Protected' }
        default { return "Unknown($rid)" }
    }
}

function Test-Probe([string]$path) {
    $probe = Join-Path $path ('.miaowu-write-test-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = [System.IO.File]::Open($probe, 'CreateNew', 'Write', 'None')
        $stream.WriteByte(0)
        $stream.Dispose()
        Remove-Item -LiteralPath $probe -Force
        return $true
    }
    catch [System.UnauthorizedAccessException] {
        return $false
    }
    catch {
        return $false
    }
    finally {
        if (Test-Path -LiteralPath $probe) {
            Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-NearestExisting([string]$path) {
    $current = $path
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
            return $null
        }
        $current = $parent
    }
    return $current
}

function Test-Folder([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        $nearest = Get-NearestExisting $path
        $writable = if ($nearest) { Test-Probe $nearest } else { $false }
        $state = if ($writable) { 'MISSING (首次运行会创建)' } else { 'MISSING 且上级不可写' }
        return [pscustomobject]@{ Path = $path; State = $state; Writable = $writable }
    }

    $writableNow = Test-Probe $path
    $owner = ''
    try { $owner = (Get-Acl -LiteralPath $path).Owner } catch { $owner = '(读不到)' }
    $state = if ($writableNow) { 'OK' } else { "DENIED（属主 $owner）" }
    return [pscustomobject]@{ Path = $path; State = $state; Writable = $writableNow }
}

$integrity = Get-IntegrityLevel
$dataRoot = if ($env:MIAOWU_DATA_DIR) { $env:MIAOWU_DATA_DIR } else { Join-Path $env:USERPROFILE '.miaowu' }

Write-Host "MiaoWu folder check"
Write-Host ("  user       : " + [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
Write-Host ("  integrity  : " + $integrity + $(if ($integrity -eq 'Low') { '  <-- 低完整性，写入会被完整性策略拒绝，请换普通桌面重跑' } else { '' }))
Write-Host ("  TEMP       : " + $env:TEMP)
Write-Host ("  data root  : " + $dataRoot)
Write-Host ""

$targets = @(
    $env:TEMP,
    (Join-Path $env:LOCALAPPDATA 'Programs\MiaoWu'),
    $dataRoot,
    (Join-Path $dataRoot 'attachments'),
    (Join-Path $dataRoot 'backups'),
    (Join-Path $dataRoot 'logs'),
    (Join-Path $dataRoot 'cache'),
    (Join-Path $dataRoot 'cache\tool-output'),
    (Join-Path $dataRoot 'exports'),
    (Join-Path $dataRoot 'skills')
)

if (-not [string]::IsNullOrWhiteSpace($Workspace)) {
    $agent = Join-Path $Workspace '.miaowu'
    $targets += @(
        $Workspace,
        $agent,
        (Join-Path $agent 'sessions'),
        (Join-Path $agent 'attachments'),
        (Join-Path $agent 'cache'),
        (Join-Path $agent 'skills')
    )
}

$denied = 0
foreach ($target in ($targets | Select-Object -Unique)) {
    if ([string]::IsNullOrWhiteSpace($target)) { continue }
    $result = Test-Folder $target
    if ($result.Writable) {
        Write-Host ("FOLDER_OK     {0}  [{1}]" -f $result.Path, $result.State)
    }
    else {
        $denied++
        Write-Host ("FOLDER_DENIED {0}  [{1}]" -f $result.Path, $result.State)
    }
}

Write-Host ""
if ($denied -eq 0) {
    Write-Host "RESULT=OK 所有目录在当前上下文可写。"
    exit 0
}

Write-Host "RESULT=DENIED $denied 个目录不可写。"
Write-Host "处置：1) 确认不是在低完整性 / 沙箱终端里运行；2) 资源管理器里给该目录补当前用户的修改权限；"
Write-Host "      3) 由管理员账户创建的目录请改用创建它的账户，或删掉让 MiaoWu 重新创建。"
exit 1
