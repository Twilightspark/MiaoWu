# 把 Inno 生成的 setup.exe 清单从 asInvoker 改成 requireAdministrator。
#
# 为什么需要：Inno 的 SetupLdr 清单固定是 asInvoker，提权是运行时才发起的；如果双击它的
# 入口把进程压到低完整性（受限终端 / 沙箱 / 部分安全软件），SetupLdr 会先解包到 %TEMP%
# 再提权，于是先撞上 "Setup was unable to create the directory ... Error 5"。
# 改成 requireAdministrator 后，Windows 在进程创建时就弹 UAC，解包发生在高完整性进程里。
#
# 实现：Inno 在清单里留了一段空格填充，正好够放下更长的执行级别。本脚本只在原位置
# 改写字符串并把等量空格删掉，文件总长度与 PE 结构完全不变；改完必须重新签名。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\patch-setup-manifest.ps1 -Path <setup.exe>

param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Find-Bytes([byte[]]$haystack, [byte[]]$needle) {
    $limit = $haystack.Length - $needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        if ($haystack[$i] -ne $needle[0]) { continue }
        $match = $true
        for ($j = 1; $j -lt $needle.Length; $j++) {
            if ($haystack[$i + $j] -ne $needle[$j]) { $match = $false; break }
        }
        if ($match) { return $i }
    }
    return -1
}

$full = (Resolve-Path -LiteralPath $Path).Path
$bytes = [IO.File]::ReadAllBytes($full)
$ascii = [Text.Encoding]::ASCII

$start = Find-Bytes $bytes ($ascii.GetBytes('<assembly'))
$end = Find-Bytes $bytes ($ascii.GetBytes('</assembly>'))
if ($start -lt 0 -or $end -lt $start) {
    Write-Host "MANIFEST_NOT_FOUND $full"
    exit 1
}

$manifestLength = $end + 11 - $start
$manifest = $ascii.GetString($bytes, $start, $manifestLength)

$oldLevel = 'level="asInvoker"'
$newLevel = 'level="requireAdministrator"'

if ($manifest.Contains($newLevel)) {
    Write-Host "MANIFEST_ALREADY_ADMIN $full"
    exit 0
}

if (([regex]::Matches($manifest, [regex]::Escape($oldLevel))).Count -ne 1) {
    Write-Host "MANIFEST_LEVEL_AMBIGUOUS $full"
    exit 1
}

$patched = $manifest.Replace($oldLevel, $newLevel)
$padding = $patched.Length - $manifest.Length

# 从 </description> 后面的空格串里借相同长度的空格，保持清单总长度不变。
$marker = '</description>'
$markerEnd = $patched.IndexOf($marker) + $marker.Length
if ($markerEnd -lt $marker.Length) {
    Write-Host "MANIFEST_PADDING_ANCHOR_MISSING $full"
    exit 1
}

$runLength = 0
$probe = $markerEnd
while ($probe -lt $patched.Length -and ($patched[$probe] -eq "`r" -or $patched[$probe] -eq "`n")) {
    $probe++
}

while (($probe + $runLength) -lt $patched.Length -and $patched[$probe + $runLength] -eq ' ') {
    $runLength++
}

if ($runLength -lt $padding) {
    Write-Host "MANIFEST_PADDING_TOO_SMALL $full ($runLength < $padding)"
    exit 1
}

$patched = $patched.Remove($probe, $padding)
if ($patched.Length -ne $manifest.Length) {
    Write-Host "MANIFEST_LENGTH_MISMATCH $full"
    exit 1
}

try {
    $null = [xml]$patched
}
catch {
    Write-Host ("MANIFEST_XML_INVALID " + $_.Exception.Message)
    exit 1
}

[Array]::Copy($ascii.GetBytes($patched), 0, $bytes, $start, $manifestLength)
[IO.File]::WriteAllBytes($full, $bytes)

$verify = $ascii.GetString([IO.File]::ReadAllBytes($full), $start, $manifestLength)
if (-not $verify.Contains($newLevel)) {
    Write-Host "MANIFEST_WRITE_FAILED $full"
    exit 1
}

Write-Host ("MANIFEST_PATCHED {0} level=requireAdministrator paddingTaken={1}" -f $full, $padding)
exit 0
