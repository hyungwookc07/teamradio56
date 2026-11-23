# Microsoft Defender 검사 — 릴리스 게이트 (GitHub Actions Windows 빌드 러너 전용).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File simhub\ci\scan-defender.ps1 `
#       -AssetsDir dist\ci\assets -ReportPath dist\ci\meta\av-scan.txt -WorkDir dist\av-scan [-ExtraDir dist\audio]
#   pwsh -NoProfile -File simhub/ci/scan-defender.ps1 -SelfTest
#       순수 함수 셀프테스트만 (Defender를 건드리지 않는다 — 리눅스 게이트에서 돈다)
#   pwsh -NoProfile -File simhub/ci/scan-defender.ps1 -SampleReport -AssetsDir <폴더> -ReportPath <파일>
#       검증 게이트 전용: Defender 없이 같은 코드로 보고서 형식만 만든다 — 릴리스 흐름 검사가 이것을
#       bash 검사기(release-lib.sh av_report_problem)에 넣어 형식 계약을 확인한다. 워크플로에서 쓰지 않는다
#
# 침묵을 믿지 않는다. 러너 이미지는 속도 때문에 Defender를 느슨하게 설정해 둔다 (실시간 보호 끔,
# C:\·D:\ 제외 경로, 압축 파일 검사 끔, 시작 시 서명 갱신 끔). 그래서 이 스크립트는:
#   1. WinDefend 서비스가 돌고 있어야 한다 (아니면 시작, 못 하면 실패). 상태 요약을 로그에 남긴다
#   2. 제외 항목(경로·확장자·프로세스)을 전부 지운다 (일회용 러너, 관리자). 지운 뒤에도 검사 대상을
#      덮는 제외 경로·확장자가 남아 있으면 실패
#   3. 압축 파일 검사를 켜고 켜졌는지 다시 읽는다
#   4. 서명 갱신 (Update-MpSignature → MpCmdRun -SignatureUpdate → -SignatureUpdate -MMPC, 재시도).
#      그다음 서명이 지금으로부터 -MaxSignatureAgeDays(3일) 이내여야 한다 — 갱신이 조용히 실패해도 걸린다
#   5. 자체 시험: 검사 대상과 같은 드라이브·같은 작업 폴더(-WorkDir, 압축 푼 파일도 여기)에 EICAR 시험
#      파일을 만들어 검사한다. 탐지(종료 코드 2 + 위협 목록)가 아니면 '검사기가 동작하지 않음'으로 실패.
#      EICAR 문자열은 조각을 이어 실행 중에만 만든다 (저장소 파일이 백신에 걸리지 않게). 검사 후 지운다.
#      zip 안의 EICAR도 검사해 압축 파일 검사가 되는지 기록한다 (압축은 따로 풀어 검사하므로 경고만)
#   6. 대상 검사: -AssetsDir의 파일 각각, 그 안의 zip은 새 폴더에 풀어 내용까지, -ExtraDir(새 음성 팩)도
#      같게. MpCmdRun -Scan -ScanType 3 -File <경로> -DisableRemediation — 0 + 위협 없음 = 깨끗함,
#      2 = 위협(실패, 자세히 출력), 그 밖 = 검사 오류(실패). 끝나고 Get-MpThreatDetection에 대상 경로가
#      하나라도 있어도 실패
#   7. 보고서 (-ReportPath, key=value, UTF-8 BOM 없음, LF) — 게시 자산마다 asset=<이름>,<sha256>,<크기>.
#      verify-artifact·attest·publish가 이 sha256을 게시할 파일과 대조한다 (검사한 바이트 = 게시할 바이트).
#      보고서는 전부 통과했을 때만 쓴다 (result=clean)
#
# Windows PowerShell 5 호환으로 쓴다 (검증 게이트 ps-check가 막는다). StrictMode.
param(
    [string]$AssetsDir = "",
    [string]$ReportPath = "",
    [string]$WorkDir = "",
    [string[]]$ExtraDir = @(),
    [int]$MaxSignatureAgeDays = 3,
    [switch]$SelfTest,
    [switch]$SampleReport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# EICAR 표준 시험 파일(68바이트)의 잘 알려진 SHA-256
$EicarSha256 = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Fail([string]$msg) {
    Write-Host "::error::$msg"
    Write-Host "❌ $msg"
    exit 1
}

function Step([string]$msg) {
    Write-Host ""
    Write-Host "▶ $msg"
}

function Warn([string]$msg) {
    Write-Host "::warning::$msg"
}

function Summary([string]$text) {
    if ($env:GITHUB_STEP_SUMMARY) {
        [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $text + "`n", $Utf8NoBom)
    }
}

# ── 순수 함수 (셀프테스트가 리눅스 pwsh로 돌린다) ─────────────────────────

# EICAR 시험 파일 바이트 — 저장소에는 한 덩어리로 두지 않는다
function Get-EicarBytes {
    $parts = @('X5O!P%@AP', '[4\PZX54(P^)', '7CC)7}$', 'EICAR-STAN', 'DARD-ANTIVI', 'RUS-TEST-F', 'ILE!$H+H*')
    $bytes = [System.Text.Encoding]::ASCII.GetBytes(($parts -join ''))
    return ,$bytes
}

function Get-BytesSha256([byte[]]$bytes) {
    $h = [System.Security.Cryptography.SHA256]::Create()
    try {
        return (($h.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join '')
    } finally {
        $h.Dispose()
    }
}

# 있을 수도 없을 수도 있는 속성 (StrictMode에서 없는 속성을 읽으면 예외)
function Get-Prop($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

# 경로 비교용 — 환경 변수 펼침, / → \, 끝의 \ 제거(C:\ → c:), 소문자
function ConvertTo-NormPath([string]$p) {
    $x = [Environment]::ExpandEnvironmentVariables($p.Trim()).Replace('/', '\')
    while ($x.Length -gt 1 -and $x.EndsWith('\')) { $x = $x.Substring(0, $x.Length - 1) }
    return $x.ToLowerInvariant()
}

# 제외 경로 하나가 이 경로(파일·폴더)를 덮는지 — 같거나 그 아래, 와일드카드는 경로와 모든 상위 폴더에 맞춰 본다
function Test-ExclusionCovers([string]$exclusion, [string]$path) {
    $e = ConvertTo-NormPath $exclusion
    $p = ConvertTo-NormPath $path
    if ($e -eq "") { return $false }
    if ($p -eq $e -or $p.StartsWith($e + '\')) { return $true }
    if ($e.IndexOfAny([char[]]@('*', '?')) -ge 0) {
        $cur = $p
        while ($true) {
            if ($cur -like $e) { return $true }
            $i = $cur.LastIndexOf('\')
            if ($i -le 0) { break }
            $cur = $cur.Substring(0, $i)
        }
    }
    return $false
}

# 제외 경로가 검사 대상 폴더 안쪽을 가리키는지 (그 아래 파일은 폴더째 검사할 때 빠질 수 있다)
function Test-ExclusionInside([string]$exclusion, [string]$root) {
    $e = ConvertTo-NormPath $exclusion
    $r = ConvertTo-NormPath $root
    return ($e -ne "" -and $e.StartsWith($r + '\'))
}

# 제외 확장자(".zip", "zip", "*.zip")가 이 파일에 해당하는지
function Test-ExtensionExcluded([string]$ext, [string]$path) {
    $e = $ext.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant()
    $x = [System.IO.Path]::GetExtension($path).TrimStart('.').ToLowerInvariant()
    return ($e -ne "" -and $e -eq $x)
}

# MpCmdRun 출력의 위협 이름 ("Threat                  : Virus:DOS/EICAR_Test_File")
function Get-MpCmdRunThreats([string[]]$lines) {
    $names = @()
    foreach ($l in @($lines)) {
        if ("$l" -match '^\s*Threat\s*:\s*(\S.*?)\s*$') { $names += $Matches[1] }
    }
    return $names
}

# MpCmdRun 출력의 "found N threats" 합
function Get-MpCmdRunThreatCount([string[]]$lines) {
    $n = 0
    foreach ($l in @($lines)) {
        if ("$l" -match 'found\s+(\d+)\s+threats?') { $n += [int]$Matches[1] }
    }
    return $n
}

# MpCmdRun 위협 목록의 자원 경로 ("    file                : C:\a\b.dll")
function Get-MpCmdRunResources([string[]]$lines) {
    $paths = @()
    foreach ($l in @($lines)) {
        if ("$l" -match '^\s*(?:file|containerfile)\s*:\s*(\S.*?)\s*$') { $paths += $Matches[1] }
    }
    return $paths
}

# MpCmdRun -Scan 판정 — 'clean'은 종료 코드 0이면서 위협 표시가 전혀 없을 때만.
# 2 + 위협 목록 = 'threat', 2인데 목록이 없거나 그 밖의 코드 = 'error' (어느 쪽도 깨끗함으로 넘기지 않는다)
function Get-ScanVerdict([int]$code, [string[]]$lines) {
    $names = @(Get-MpCmdRunThreats $lines)
    $count = Get-MpCmdRunThreatCount $lines
    if ($code -eq 0) {
        if ($names.Count -eq 0 -and $count -eq 0) { return "clean" }
        return "error"
    }
    if ($code -eq 2) {
        if ($names.Count -gt 0 -or $count -gt 0) { return "threat" }
        return "error"
    }
    return "error"
}

# Get-MpThreatDetection의 Resources 항목 ("file:_C:\a\b.zip->x.dll", "containerfile:_C:\a\b.zip") → 파일 경로
function ConvertFrom-MpResource([string]$r) {
    $x = $r -replace '^[A-Za-z]+:_', ''
    $i = $x.IndexOf('->')
    if ($i -ge 0) { $x = $x.Substring(0, $i) }
    return $x
}

function ConvertTo-IsoUtc([datetime]$t) {
    return $t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
}

# 서명이 now 기준 maxDays 이내인지 (미래 시각은 하루까지만 — 시계 차이)
function Test-SignatureFresh($updated, [datetime]$now, [int]$maxDays) {
    if ($null -eq $updated -or -not ($updated -is [datetime])) { return $false }
    if ($updated -eq [datetime]::MinValue) { return $false }
    $age = ($now.ToUniversalTime() - $updated.ToUniversalTime()).TotalDays
    return ($age -le $maxDays -and $age -ge -1)
}

function New-ReportLine([string]$key, [string]$value) {
    if ($key -cnotmatch '^[a-z][a-z0-9_]*$') { throw "보고서 키 형식이 잘못됨: '$key'" }
    if ($value -match '[\r\n]') { throw "보고서 값에 줄바꿈이 있음: $key" }
    return "$key=$value"
}

# 자산 줄 — <이름>,<sha256>,<크기> (voice_lines/audio_pack.txt의 asset= 줄과 같은 모양)
function New-AssetLine([string]$key, [string]$name, [string]$sha, [long]$size) {
    if ($name -match '[,\r\n]' -or $name -eq "") { throw "자산 이름에 쉼표·줄바꿈: '$name'" }
    if ($sha -cnotmatch '^[0-9a-f]{64}$') { throw "sha256 형식이 잘못됨: $name" }
    return (New-ReportLine $key ("{0},{1},{2}" -f $name, $sha, $size))
}

function Format-AvReport([string[]]$lines) {
    return ((@($lines) -join "`n") + "`n")
}

# 보고서 줄 전부 — 키 이름은 release-lib.sh av_report_problem·release_verify_section과의 계약이다
# ($info: Product Engine SigVer SigIso UpdateHow Mode ScanIso Removed SelfThreat SelfArchive Scanned Extracted)
function New-AvReportLines($info, $assetFiles, [string[]]$extraDirs) {
    $lines = @(
        (New-ReportLine "format" "1"),
        (New-ReportLine "scanner" "Microsoft Defender"),
        (New-ReportLine "product_version" "$($info.Product)"),
        (New-ReportLine "engine_version" "$($info.Engine)"),
        (New-ReportLine "antivirus_signature_version" "$($info.SigVer)"),
        (New-ReportLine "signature_updated" "$($info.SigIso)"),
        (New-ReportLine "signature_update" "$($info.UpdateHow)"),
        (New-ReportLine "running_mode" "$($info.Mode)"),
        (New-ReportLine "scan_time" "$($info.ScanIso)"),
        (New-ReportLine "exclusions_removed" "$($info.Removed)"),
        (New-ReportLine "archive_scanning" "enabled"),
        (New-ReportLine "selftest" "detected"),
        (New-ReportLine "selftest_threat" "$($info.SelfThreat)"),
        (New-ReportLine "selftest_archive" "$($info.SelfArchive)"),
        (New-ReportLine "scanned_files" "$($info.Scanned)"),
        (New-ReportLine "extracted_files" "$($info.Extracted)"),
        (New-ReportLine "result" "clean")
    )
    foreach ($f in @($assetFiles)) {
        $lines += (New-AssetLine "asset" $f.Name (Get-FileSha256 $f.FullName) $f.Length)
    }
    foreach ($d in @($extraDirs)) {
        if ("$d" -eq "") { continue }
        foreach ($f in @(Get-ChildItem -LiteralPath $d -Recurse -File -Force | Sort-Object FullName)) {
            $rel = $f.FullName.Substring($d.Length).TrimStart('\', '/').Replace('\', '/')
            $lines += (New-AssetLine "extra_file" $rel (Get-FileSha256 $f.FullName) $f.Length)
        }
    }
    return $lines
}

function Get-FileSha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Invoke-SelfTest {
    $fails = New-Object System.Collections.Generic.List[string]
    $script:selfPass = 0
    function Check([bool]$cond, [string]$what) {
        if ($cond) { $script:selfPass++ } else { $fails.Add($what) }
    }

    [byte[]]$e = Get-EicarBytes
    Check ($e.Length -eq 68) "EICAR 길이 68바이트 (지금 $($e.Length))"
    Check ((Get-BytesSha256 $e) -eq $EicarSha256) "EICAR sha256 = 표준 값"
    Check ($e.Length -gt 0 -and $e[0] -eq 0x58 -and $e[$e.Length - 1] -eq 0x2A) "EICAR 첫 바이트 X, 끝 바이트 *"
    # 이 파일에 완성된 EICAR 문자열이 그대로 들어 있으면 저장소 자체가 백신에 걸린다
    $self = [System.IO.File]::ReadAllText($PSCommandPath)
    Check (-not $self.Contains([System.Text.Encoding]::ASCII.GetString($e))) "스크립트에 완성된 EICAR 문자열이 없음"

    Check (Test-ExclusionCovers 'C:\' 'C:\a\b.zip') "제외 C:\ → C:\a\b.zip 덮음"
    Check (Test-ExclusionCovers 'd:\' 'D:\a\teamradio56\dist\ci\assets') "제외 d:\ → D:\…(대소문자 무시)"
    Check (-not (Test-ExclusionCovers 'D:\' 'C:\a\b.zip')) "제외 D:\ → C:\ 경로 안 덮음"
    Check (-not (Test-ExclusionCovers 'C:\a' 'C:\ab\c.zip')) "제외 C:\a → C:\ab 안 덮음 (접두어만 같은 형제)"
    Check (Test-ExclusionCovers 'C:\a\' 'C:\A\B\c.dll') "제외 C:\a\ → 아래 파일 덮음"
    Check (Test-ExclusionCovers 'C:\a/b' 'C:\a\b') "제외 / 구분자 → 같은 경로"
    Check (Test-ExclusionCovers 'C:\a\*' 'C:\a\b\c.dll') "제외 C:\a\* → 아래 파일 덮음"
    Check (Test-ExclusionCovers 'C:\*\dist' 'C:\x\dist\ci\f.zip') "제외 C:\*\dist → 중간 와일드카드 덮음"
    Check (-not (Test-ExclusionCovers 'C:\*\dust' 'C:\x\dist\ci\f.zip')) "제외 C:\*\dust → 안 덮음"
    Check (-not (Test-ExclusionCovers '' 'C:\x')) "빈 제외 → 안 덮음"
    Check (Test-ExclusionInside 'D:\w\dist\ci\assets\sub' 'D:\w\dist\ci\assets') "대상 폴더 안쪽 제외 경로"
    Check (-not (Test-ExclusionInside 'D:\w\dist\ci\assets2' 'D:\w\dist\ci\assets')) "형제 폴더는 안쪽 아님"
    Check (Test-ExtensionExcluded '.zip' 'a\b.ZIP') "제외 확장자 .zip → b.ZIP"
    Check (Test-ExtensionExcluded 'dll' 'x.dll') "제외 확장자 dll → x.dll"
    Check (Test-ExtensionExcluded '*.txt' 'teamradio56-release.txt') "제외 확장자 *.txt → .txt"
    Check (-not (Test-ExtensionExcluded 'zip' 'a.zipx')) "제외 확장자 zip → .zipx 아님"

    $hit = @(
        "Scan starting...",
        "Scan finished.",
        "Scanning D:\w\dist\av-scan\selftest\eicar-selftest.com found 1 threats.",
        "",
        "<===========================LIST OF DETECTED THREATS==========================>",
        "----------------------------- Threat information ------------------------------",
        "Threat                  : Virus:DOS/EICAR_Test_File",
        "Resources               : 1 total",
        "    file                : D:\w\dist\av-scan\selftest\eicar-selftest.com",
        "-------------------------------------------------------------------------------"
    )
    $clean = @("Scan starting...", "Scan finished.", "Scanning D:\w\dist\ci\assets\x.zip found no threats.")
    $names = @(Get-MpCmdRunThreats $hit)
    Check ($names.Count -eq 1 -and $names[0] -eq 'Virus:DOS/EICAR_Test_File') "위협 이름 읽기"
    Check ((Get-MpCmdRunThreatCount $hit) -eq 1) "found 1 threats 읽기"
    $res = @(Get-MpCmdRunResources $hit)
    Check ($res.Count -eq 1 -and $res[0] -eq 'D:\w\dist\av-scan\selftest\eicar-selftest.com') "위협 자원 경로 읽기"
    Check (@(Get-MpCmdRunThreats $clean).Count -eq 0 -and (Get-MpCmdRunThreatCount $clean) -eq 0) "깨끗한 출력엔 위협 없음"
    Check ((Get-ScanVerdict 0 $clean) -eq 'clean') "0 + 깨끗함 → clean"
    Check ((Get-ScanVerdict 2 $hit) -eq 'threat') "2 + 위협 목록 → threat"
    Check ((Get-ScanVerdict 2 $clean) -eq 'error') "2인데 위협 목록 없음 → error"
    Check ((Get-ScanVerdict 0 $hit) -eq 'error') "0인데 위협 목록 → error"
    Check ((Get-ScanVerdict -2147024894 $clean) -eq 'error') "그 밖의 종료 코드 → error"
    Check ((Get-ScanVerdict 0 @()) -eq 'clean') "0 + 출력 없음 → clean"
    Check ((Get-ScanVerdict 2 $null) -eq 'error') "2 + 출력 없음 → error"

    Check ((ConvertFrom-MpResource 'file:_D:\w\a.zip->x.dll') -eq 'D:\w\a.zip') "자원 file:_…->… → zip 경로"
    Check ((ConvertFrom-MpResource 'containerfile:_D:\w\a.zip') -eq 'D:\w\a.zip') "자원 containerfile:_"
    Check ((ConvertFrom-MpResource 'D:\w\b.dll') -eq 'D:\w\b.dll') "자원 접두어 없음"

    $t = [datetime]::SpecifyKind((New-Object DateTime 2026, 1, 2, 3, 4, 5), [DateTimeKind]::Utc)
    Check ((ConvertTo-IsoUtc $t) -eq '2026-01-02T03:04:05Z') "ISO-8601 UTC"
    Check (Test-SignatureFresh $t.AddDays(-2.9) $t 3) "서명 2.9일 → 신선"
    Check (-not (Test-SignatureFresh $t.AddDays(-3.1) $t 3)) "서명 3.1일 → 오래됨"
    Check (-not (Test-SignatureFresh $t.AddDays(2) $t 3)) "서명이 이틀 미래 → 거부"
    Check (-not (Test-SignatureFresh $null $t 3)) "서명 시각 없음 → 거부"
    Check (-not (Test-SignatureFresh ([datetime]::MinValue) $t 3)) "서명 시각 MinValue → 거부"

    $txt = Format-AvReport @((New-ReportLine 'format' '1'), (New-ReportLine 'result' 'clean'))
    Check ($txt -eq "format=1`nresult=clean`n") "보고서 LF 줄"
    $one = Format-AvReport @((New-ReportLine 'result' 'clean'))
    Check ($one -eq "result=clean`n") "보고서 한 줄"
    $threw = $false
    try { New-ReportLine 'Bad-Key' 'x' | Out-Null } catch { $threw = $true }
    Check $threw "보고서 키 형식 거부"
    $threw = $false
    try { New-ReportLine 'k' "a`nb" | Out-Null } catch { $threw = $true }
    Check $threw "보고서 값 줄바꿈 거부"
    $threw = $false
    try { New-AssetLine 'asset' 'a,b.zip' ('0' * 64) 1 | Out-Null } catch { $threw = $true }
    Check $threw "자산 이름 쉼표 거부"

    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllBytes($tmp, [System.Text.Encoding]::ASCII.GetBytes("abc"))
        $line = New-AssetLine 'asset' 'teamradio56-plugin-1.2.3.zip' (Get-FileSha256 $tmp) ((Get-Item -LiteralPath $tmp).Length)
        Check ($line -eq 'asset=teamradio56-plugin-1.2.3.zip,ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad,3') "자산 줄 <이름>,<sha256>,<크기>"
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }

    # 보고서 줄 전체 — 자산 줄과 새 음성 팩 폴더의 상대 경로 줄
    $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("scan-defender-selftest-" + [guid]::NewGuid().ToString("N"))
    try {
        $ad = Join-Path $tmpDir "assets"
        $xd = Join-Path $tmpDir "audio"
        $xsub = Join-Path $xd "pack"
        New-Item -ItemType Directory -Force -Path $ad, $xsub | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $ad "b.zip"), [System.Text.Encoding]::ASCII.GetBytes("abc"))
        [System.IO.File]::WriteAllBytes((Join-Path $ad "a.txt"), [byte[]]@())
        [System.IO.File]::WriteAllBytes((Join-Path $xsub "p.zip"), [System.Text.Encoding]::ASCII.GetBytes("x"))
        $info = @{ Product = "4.18"; Engine = "1.1"; SigVer = "1.4"; SigIso = "2026-01-02T03:04:05Z"; UpdateHow = "Update-MpSignature"
            Mode = "Normal"; ScanIso = "2026-01-02T04:04:05Z"; Removed = 2; SelfThreat = "t"; SelfArchive = "detected"; Scanned = 3; Extracted = 1 }
        $rl = @(New-AvReportLines $info @(Get-ChildItem -LiteralPath $ad -File | Sort-Object Name) @($xd))
        Check ($rl[0] -eq "format=1" -and $rl -contains "result=clean" -and $rl -contains "selftest=detected") "보고서 머리 줄 (format·result·selftest)"
        Check ($rl -contains "scanner=Microsoft Defender" -and $rl -contains "exclusions_removed=2" -and $rl -contains "signature_updated=2026-01-02T03:04:05Z") "보고서 값 줄"
        Check ($rl -contains "asset=b.zip,ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad,3") "보고서 asset 줄 (zip)"
        Check ($rl -contains "asset=a.txt,e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855,0") "보고서 asset 줄 (빈 파일)"
        Check ($rl -contains "extra_file=pack/p.zip,2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881,1") "보고서 extra_file 줄 (상대 경로 /)"
        Check (@($rl | Where-Object { $_ -like "asset=*" }).Count -eq 2) "asset 줄은 assets 파일 수만큼"
    } finally {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($fails.Count -gt 0) {
        Write-Host "❌ scan-defender.ps1 셀프테스트 실패 $($fails.Count)건 (통과 ${script:selfPass}건)"
        foreach ($f in $fails) { Write-Host "   - $f" }
        exit 1
    }
    Write-Host "✅ scan-defender.ps1 셀프테스트 ${script:selfPass}건 통과 (EICAR 조립 68바이트·sha256, 제외 판정, 출력 판정, 보고서 형식)"
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

if ($SampleReport) {
    if ($AssetsDir -eq "" -or $ReportPath -eq "") { Fail "-SampleReport에는 -AssetsDir와 -ReportPath가 필요합니다" }
    $now = Get-Date
    $files = @(Get-ChildItem -LiteralPath $AssetsDir -File -Force | Sort-Object Name)
    $info = @{
        Product = "0.0.0.0"; Engine = "0.0.0.0"; SigVer = "0.0.0.0"; SigIso = (ConvertTo-IsoUtc $now.AddHours(-1))
        UpdateHow = "sample"; Mode = "sample"; ScanIso = (ConvertTo-IsoUtc $now); Removed = 0
        SelfThreat = "sample"; SelfArchive = "detected"; Scanned = $files.Count; Extracted = 0
    }
    $lines = @(New-AvReportLines $info $files @())
    [System.IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath),
        (Format-AvReport $lines), $Utf8NoBom)
    exit 0
}

# ── 여기부터 Windows 러너 (Defender) ─────────────────────────────────────

# 예상 못 한 예외도 ::error:: 한 줄로 끝나게
trap { Fail ("예상하지 못한 오류: " + $_.Exception.Message) }

# 러너 로그에서 한글이 깨지지 않게
try { [Console]::OutputEncoding = $Utf8NoBom } catch { }

# 네이티브 명령 — stderr를 2>&1로 받으면 PowerShell 5는 Stop 모드에서 예외로 바꾼다
function Invoke-Native([string]$exe, [string[]]$argList) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $global:LASTEXITCODE = -1
    try {
        $out = @(& $exe @argList 2>&1 | ForEach-Object { "$_" })
        return @{ Code = [int]$LASTEXITCODE; Output = $out }
    } finally {
        $ErrorActionPreference = $old
    }
}

function Show-Output([string[]]$lines) {
    foreach ($l in @($lines)) {
        if ("$l".Trim() -ne "") { Write-Host "    | $l" }
    }
}

function Find-MpCmdRun {
    $cands = @()
    $plat = Join-Path $env:ProgramData "Microsoft\Windows Defender\Platform"
    if (Test-Path -LiteralPath $plat -PathType Container) {
        $dirs = @(Get-ChildItem -LiteralPath $plat -Directory | Sort-Object -Descending -Property {
                $v = [version]"0.0"
                [void][version]::TryParse(($_.Name -replace '-.*$', ''), [ref]$v)
                $v
            })
        foreach ($d in $dirs) { $cands += (Join-Path $d.FullName "MpCmdRun.exe") }
    }
    $cands += (Join-Path $env:ProgramFiles "Windows Defender\MpCmdRun.exe")
    foreach ($c in $cands) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }
    return ""
}

function Get-DefenderStatus {
    for ($i = 1; $i -le 6; $i++) {
        try {
            return (Get-MpComputerStatus -ErrorAction Stop)
        } catch {
            Write-Host "  Get-MpComputerStatus 실패 ($i/6): $($_.Exception.Message)"
            Start-Sleep -Seconds 10
        }
    }
    Fail "Get-MpComputerStatus가 계속 실패합니다 — Defender가 응답하지 않습니다"
}

function Show-Status($s) {
    foreach ($k in @("AMRunningMode", "AMServiceEnabled", "AntivirusEnabled", "AntispywareEnabled",
            "RealTimeProtectionEnabled", "IsTamperProtected", "AMProductVersion", "AMEngineVersion",
            "AntivirusSignatureVersion", "AntivirusSignatureLastUpdated", "AntivirusSignatureAge",
            "DefenderSignaturesOutOfDate", "ComputerState")) {
        Write-Host ("  {0,-30} {1}" -f $k, (Get-Prop $s $k))
    }
}

function Invoke-MpScan([string]$mp, [string]$path) {
    return (Invoke-Native $mp @('-Scan', '-ScanType', '3', '-File', $path, '-DisableRemediation'))
}

# 위협·오류 자세히 — MpCmdRun 출력, 위협 자원의 sha256 (같은 커밋을 로컬에서 빌드해 비교할 수 있게)
function Show-ScanProblem([string]$path, $r) {
    Write-Host "  MpCmdRun 종료 코드 $($r.Code) — $path"
    Show-Output $r.Output
    foreach ($p in @(Get-MpCmdRunResources $r.Output)) {
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            Write-Host ("  위협 파일 {0}  sha256 {1}  {2} 바이트" -f $p, (Get-FileSha256 $p), (Get-Item -LiteralPath $p).Length)
        }
    }
    if ((Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Host ("  검사한 파일 sha256 {0}" -f (Get-FileSha256 $path))
    }
}

# Get-MpThreatDetection — 없으면 빈 목록 (없음을 '찾지 못함' 오류로 내는 버전도 있다). 그 밖의 실패는 실패
function Get-Detections {
    try {
        return @(Get-MpThreatDetection -ErrorAction Stop)
    } catch {
        if ("$($_.FullyQualifiedErrorId)" -match 'NotFound') { return @() }
        Fail "Get-MpThreatDetection 실패: $($_.Exception.Message)"
    }
}

# ── 0. 인자 ──────────────────────────────────────────────────────────────
if ($AssetsDir -eq "" -or $ReportPath -eq "" -or $WorkDir -eq "") {
    Fail "-AssetsDir, -ReportPath, -WorkDir가 필요합니다"
}
if (-not (Test-Path -LiteralPath $AssetsDir -PathType Container)) { Fail "-AssetsDir 폴더가 없습니다: $AssetsDir" }
$AssetsDir = (Resolve-Path -LiteralPath $AssetsDir).Path.TrimEnd('\')
$extraDirs = @()
foreach ($d in @($ExtraDir)) {
    if ("$d" -eq "") { continue }
    if (-not (Test-Path -LiteralPath $d -PathType Container)) { Fail "-ExtraDir 폴더가 없습니다: $d" }
    $extraDirs += (Resolve-Path -LiteralPath $d).Path.TrimEnd('\')
}
$ReportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
if (Test-Path -LiteralPath $ReportPath) { Fail "보고서가 이미 있습니다 (검사 전에 생긴 보고서는 믿지 않는다): $ReportPath" }
$reportDir = Split-Path -Parent $ReportPath
if (-not (Test-Path -LiteralPath $reportDir -PathType Container)) { Fail "보고서 폴더가 없습니다: $reportDir" }
$WorkDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDir).TrimEnd('\')
if (Test-Path -LiteralPath $WorkDir) { Fail "작업 폴더가 이미 있습니다 (새 폴더여야 한다): $WorkDir" }
# 자체 시험 파일은 대상과 같은 드라이브에 — 그 위치가 제외돼 있지 않다는 증거가 되게
if ((Split-Path -Qualifier $WorkDir) -ne (Split-Path -Qualifier $AssetsDir)) {
    Fail "작업 폴더($WorkDir)가 검사 대상($AssetsDir)과 다른 드라이브입니다"
}
$assetFiles = @(Get-ChildItem -LiteralPath $AssetsDir -Force | Sort-Object Name)
foreach ($f in $assetFiles) {
    if ($f.PSIsContainer) { Fail "assets에 폴더가 있습니다: $($f.Name)" }
}
if ($assetFiles.Count -eq 0) { Fail "assets가 비었습니다: $AssetsDir" }
if (@($assetFiles | Where-Object { $_.Extension -eq '.zip' }).Count -eq 0) { Fail "assets에 플러그인 zip이 없습니다" }
$extraFiles = @()
foreach ($d in $extraDirs) {
    $extraFiles += @(Get-ChildItem -LiteralPath $d -Recurse -File -Force | Sort-Object FullName)
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$selfDir = Join-Path $WorkDir "selftest"
$extractRoot = Join-Path $WorkDir "extract"
New-Item -ItemType Directory -Force -Path $selfDir | Out-Null
New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
# 검사 대상이 놓이는 곳 — 제외 판정·탐지 기록 대조에 쓴다 (자체 시험 폴더는 뺀다)
$targetRoots = @($AssetsDir, $extractRoot) + $extraDirs
Write-Host "검사 대상: $AssetsDir ($($assetFiles.Count)개)$(if ($extraDirs.Count -gt 0) { ', ' + ($extraDirs -join ', ') + ' (' + $extraFiles.Count + '개)' })"
Write-Host "작업 폴더: $WorkDir (자체 시험·압축 풀기, 아티팩트에 올리지 않는다)"

# ── 1. Defender 서비스·상태 ──────────────────────────────────────────────
Step "Microsoft Defender 서비스·상태"
$svc = Get-Service -Name WinDefend -ErrorAction SilentlyContinue
if ($null -eq $svc) { Fail "WinDefend 서비스가 없습니다 — 이 러너에 Microsoft Defender가 없습니다" }
if ("$($svc.Status)" -ne "Running") {
    Write-Host "WinDefend: $($svc.Status) — 시작합니다"
    try {
        Start-Service -Name WinDefend -ErrorAction Stop
        $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(90))
    } catch {
        Fail "WinDefend 서비스를 시작하지 못했습니다: $($_.Exception.Message)"
    }
    $svc.Refresh()
    if ("$($svc.Status)" -ne "Running") { Fail "WinDefend 서비스가 Running이 아닙니다 ($($svc.Status))" }
}
Write-Host "WinDefend: Running (시작 유형 $(Get-Prop $svc 'StartType'))"
if ($null -eq (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue)) {
    Fail "Defender PowerShell 모듈(Get-MpComputerStatus)이 없습니다"
}
$mp = Find-MpCmdRun
if ($mp -eq "") { Fail "MpCmdRun.exe를 찾지 못했습니다" }
Write-Host "MpCmdRun: $mp"
$status = Get-DefenderStatus
Show-Status $status
$mode = "$(Get-Prop $status 'AMRunningMode')"
if ($mode -eq "Not running") { Fail "Defender 실행 모드가 'Not running'입니다" }
if ((Get-Prop $status 'AMServiceEnabled') -eq $false) { Fail "AMServiceEnabled=False — Defender 엔진 서비스가 꺼져 있습니다" }
if ((Get-Prop $status 'AntivirusEnabled') -eq $false) {
    Warn "AntivirusEnabled=False — 수동 검사가 되는지는 아래 EICAR 자체 시험이 판정한다"
}
if ($mode -ne "" -and $mode -ne "Normal") {
    Warn "Defender 실행 모드가 '$mode' — 수동 검사가 되는지는 아래 EICAR 자체 시험이 판정한다"
}

# ── 2. 제외 항목 ─────────────────────────────────────────────────────────
Step "제외 항목 지우기 (일회용 러너 — 검사 대상이 빠지지 않게)"
$pref = Get-MpPreference
$removed = 0
foreach ($kind in @("ExclusionPath", "ExclusionExtension", "ExclusionProcess")) {
    $vals = @(Get-Prop $pref $kind | Where-Object { "$_" -ne "" })
    foreach ($v in $vals) {
        if ("$v" -like "N/A*") { Fail "$kind 를 읽지 못했습니다 ('$v' — 관리자 권한 필요)" }
    }
    if ($vals.Count -eq 0) {
        Write-Host "  $kind : 없음"
        continue
    }
    Write-Host "  $kind : $($vals -join '; ') → 지움"
    $p = @{}
    $p[$kind] = [string[]]$vals
    try {
        Remove-MpPreference @p -ErrorAction Stop
        $removed += $vals.Count
    } catch {
        Warn "$kind 지우기 실패: $($_.Exception.Message) — 아래에서 남은 항목이 대상을 덮는지 본다"
    }
}
$pref = Get-MpPreference
$leftPaths = @(Get-Prop $pref "ExclusionPath" | Where-Object { "$_" -ne "" })
$leftExts = @(Get-Prop $pref "ExclusionExtension" | Where-Object { "$_" -ne "" })
$leftProcs = @(Get-Prop $pref "ExclusionProcess" | Where-Object { "$_" -ne "" })
$coverProbe = @($targetRoots) + @($selfDir) + @($assetFiles | ForEach-Object { $_.FullName }) + @($extraFiles | ForEach-Object { $_.FullName })
foreach ($e in $leftPaths) {
    foreach ($t in $coverProbe) {
        if (Test-ExclusionCovers $e $t) { Fail "제외 경로 '$e'가 지워지지 않았고 검사 대상($t)을 덮습니다" }
    }
    foreach ($r in $targetRoots) {
        if (Test-ExclusionInside $e $r) { Fail "제외 경로 '$e'가 지워지지 않았고 검사 대상 폴더($r) 안에 있습니다" }
    }
    Warn "제외 경로 '$e'가 남았지만 검사 대상과 무관합니다"
}
if ($leftExts.Count -gt 0) { Fail "제외 확장자가 지워지지 않았습니다: $($leftExts -join '; ')" }
if ($leftProcs.Count -gt 0) { Warn "제외 프로세스가 남았습니다 (실시간 보호에만 해당, 수동 검사와 무관): $($leftProcs -join '; ')" }
Write-Host "  지운 항목 ${removed}개, 대상을 덮는 제외 없음"

# ── 3. 압축 파일 검사 ────────────────────────────────────────────────────
Step "압축 파일 검사 켜기"
Set-MpPreference -DisableArchiveScanning $false
$pref = Get-MpPreference
if ((Get-Prop $pref "DisableArchiveScanning") -ne $false) {
    Fail "압축 파일 검사를 켜지 못했습니다 (DisableArchiveScanning=$(Get-Prop $pref 'DisableArchiveScanning'))"
}
foreach ($k in @("DisableArchiveScanning", "DisableRealtimeMonitoring", "ScanAvgCPULoadFactor", "SignatureDisableUpdateOnStartupWithoutEngine")) {
    Write-Host ("  {0,-46} {1}" -f $k, (Get-Prop $pref $k))
}

# ── 4. 서명 갱신 → 신선도 ────────────────────────────────────────────────
Step "서명 갱신"
$sigBefore = "$(Get-Prop $status 'AntivirusSignatureVersion')"
$updateHow = ""
for ($round = 1; $round -le 3 -and $updateHow -eq ""; $round++) {
    try {
        Write-Host "  [$round/3] Update-MpSignature"
        Update-MpSignature -ErrorAction Stop
        $updateHow = "Update-MpSignature"
        break
    } catch {
        Write-Host "    실패: $($_.Exception.Message)"
    }
    Write-Host "  [$round/3] MpCmdRun -SignatureUpdate"
    $r = Invoke-Native $mp @('-SignatureUpdate')
    Show-Output $r.Output
    if ($r.Code -eq 0) { $updateHow = "MpCmdRun -SignatureUpdate"; break }
    Write-Host "  [$round/3] MpCmdRun -SignatureUpdate -MMPC"
    $r = Invoke-Native $mp @('-SignatureUpdate', '-MMPC')
    Show-Output $r.Output
    if ($r.Code -eq 0) { $updateHow = "MpCmdRun -SignatureUpdate -MMPC"; break }
    Start-Sleep -Seconds (20 * $round)
}
$fresh = $false
for ($i = 1; $i -le 4; $i++) {
    $status = Get-DefenderStatus
    $sigTime = Get-Prop $status 'AntivirusSignatureLastUpdated'
    if (Test-SignatureFresh $sigTime (Get-Date) $MaxSignatureAgeDays) { $fresh = $true; break }
    Start-Sleep -Seconds 10
}
Show-Status $status
if ($updateHow -eq "") {
    Warn "서명 갱신이 세 방법 모두 실패했습니다 — 서명이 $MaxSignatureAgeDays 일 이내인지만 본다"
} else {
    Write-Host "  갱신: $updateHow (서명 $sigBefore → $(Get-Prop $status 'AntivirusSignatureVersion'))"
}
if (-not $fresh) {
    Fail "Defender 서명이 오래됐습니다 (AntivirusSignatureLastUpdated=$sigTime, 허용 $MaxSignatureAgeDays 일) — 갱신이 되지 않았습니다"
}
$sigIso = ConvertTo-IsoUtc ([datetime]$sigTime)
$engine = "$(Get-Prop $status 'AMEngineVersion')"
$product = "$(Get-Prop $status 'AMProductVersion')"
$sigVer = "$(Get-Prop $status 'AntivirusSignatureVersion')"
if ($engine -eq "" -or $sigVer -eq "") { Fail "엔진·서명 버전을 읽지 못했습니다" }
# 서명 갱신이 플랫폼을 바꿨을 수 있다
$mp2 = Find-MpCmdRun
if ($mp2 -ne "" -and $mp2 -ne $mp) {
    Write-Host "MpCmdRun: $mp2 (갱신 뒤)"
    $mp = $mp2
}

# ── 5. 자체 시험 (EICAR) ─────────────────────────────────────────────────
Step "자체 시험 — 대상 옆 같은 드라이브의 EICAR 시험 파일이 탐지되는지"
[byte[]]$eicar = Get-EicarBytes
if ($eicar.Length -ne 68 -or (Get-BytesSha256 $eicar) -ne $EicarSha256) { Fail "EICAR 조립 결과가 표준과 다릅니다" }
$eicarPath = Join-Path $selfDir "eicar-selftest.com"
try {
    [System.IO.File]::WriteAllBytes($eicarPath, $eicar)
} catch {
    Fail "EICAR 시험 파일을 쓰지 못했습니다 (실시간 보호가 막았을 수 있다): $($_.Exception.Message)"
}
if (-not (Test-Path -LiteralPath $eicarPath -PathType Leaf)) {
    Fail "EICAR 시험 파일이 쓰자마자 사라졌습니다 (실시간 보호가 격리한 듯) — 수동 검사 자체 시험을 할 수 없어 멈춥니다"
}
$r = Invoke-MpScan $mp $eicarPath
$selfVerdict = Get-ScanVerdict $r.Code $r.Output
Show-Output $r.Output
Remove-Item -LiteralPath $eicarPath -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $eicarPath) { Warn "EICAR 시험 파일을 지우지 못했습니다: $eicarPath (아티팩트 밖)" }
if ($selfVerdict -ne "threat") {
    Fail "검사기가 동작하지 않습니다 — EICAR 시험 파일을 탐지하지 못했습니다 (종료 코드 $($r.Code), 판정 $selfVerdict). 이 상태의 '깨끗함'은 믿을 수 없어 릴리스를 멈춥니다"
}
$selfThreat = (@(Get-MpCmdRunThreats $r.Output) + @("(이름 없음)"))[0]
Write-Host "  ✓ 탐지: $selfThreat (종료 코드 2)"

# zip 안의 EICAR — 압축 파일 검사가 되는지 기록 (압축은 따로 풀어 검사하므로 실패로 보지 않는다)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$eicarZip = Join-Path $selfDir "eicar-selftest.zip"
$archiveVerdict = "not_tested"
try {
    $za = [System.IO.Compression.ZipFile]::Open($eicarZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $s = $za.CreateEntry("eicar-selftest.com").Open()
        try { $s.Write($eicar, 0, $eicar.Length) } finally { $s.Dispose() }
    } finally {
        $za.Dispose()
    }
    $r = Invoke-MpScan $mp $eicarZip
    $archiveVerdict = Get-ScanVerdict $r.Code $r.Output
} catch {
    Write-Host "  zip 시험 파일을 만들거나 검사하지 못했습니다: $($_.Exception.Message)"
    $r = @{ Code = -1; Output = @() }
}
Remove-Item -LiteralPath $eicarZip -Force -ErrorAction SilentlyContinue
if ($archiveVerdict -eq "threat") {
    $selfArchive = "detected"
    Write-Host "  ✓ zip 안의 EICAR도 탐지 (압축 파일 검사 동작)"
} else {
    $selfArchive = "not_detected"
    Show-Output $r.Output
    Warn "zip 안의 EICAR를 탐지하지 못했습니다 (판정 $archiveVerdict) — zip은 풀어서 내용까지 검사하므로 계속한다"
}

# ── 6. 대상 검사 ─────────────────────────────────────────────────────────
Step "대상 검사 (MpCmdRun -Scan -ScanType 3 -File <경로> -DisableRemediation)"
$scanStart = Get-Date
$problems = @()
$scanned = 0
$extracted = 0

function Invoke-TargetScan([string]$path, [string]$label) {
    $r = Invoke-MpScan $mp $path
    $v = Get-ScanVerdict $r.Code $r.Output
    if ($v -eq "clean") {
        Write-Host "  ✓ 깨끗함: $label"
        return ""
    }
    Show-ScanProblem $path $r
    if ($v -eq "threat") { return "위협 탐지: $label ($(@(Get-MpCmdRunThreats $r.Output) -join ', '))" }
    return "검사 오류: $label (종료 코드 $($r.Code))"
}

# zip을 새 폴더에 풀어 폴더째 검사 — 풀린 파일 수가 zip의 파일 항목 수와 같아야 한다
function Invoke-ZipContentScan([string]$zipPath, [int]$index) {
    $dest = Join-Path $extractRoot ("{0:D2}-{1}" -f $index, [System.IO.Path]::GetFileNameWithoutExtension($zipPath))
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $dest
    $za = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $want = @($za.Entries | Where-Object { -not $_.FullName.EndsWith('/') -and -not $_.FullName.EndsWith('\') }).Count
    } finally {
        $za.Dispose()
    }
    $files = @(Get-ChildItem -LiteralPath $dest -Recurse -File -Force)
    if ($files.Count -ne $want) {
        return @{ Problem = "압축 풀기 결과가 다릅니다: $(Split-Path -Leaf $zipPath) (항목 $want, 풀린 파일 $($files.Count))"; Files = $files.Count }
    }
    $msg = Invoke-TargetScan $dest ("{0} 압축 푼 내용 {1}개" -f (Split-Path -Leaf $zipPath), $files.Count)
    return @{ Problem = $msg; Files = $files.Count }
}

$zipIndex = 0
foreach ($f in $assetFiles) {
    $msg = Invoke-TargetScan $f.FullName $f.Name
    $scanned++
    if ($msg -ne "") { $problems += $msg }
}
foreach ($f in @($assetFiles | Where-Object { $_.Extension -eq '.zip' })) {
    $zipIndex++
    $z = Invoke-ZipContentScan $f.FullName $zipIndex
    $extracted += [int]$z["Files"]
    if ($z["Problem"] -ne "") { $problems += $z["Problem"] }
}
foreach ($f in $extraFiles) {
    $msg = Invoke-TargetScan $f.FullName $f.FullName
    $scanned++
    if ($msg -ne "") { $problems += $msg }
}
foreach ($f in @($extraFiles | Where-Object { $_.Extension -eq '.zip' })) {
    $zipIndex++
    $z = Invoke-ZipContentScan $f.FullName $zipIndex
    $extracted += [int]$z["Files"]
    if ($z["Problem"] -ne "") { $problems += $z["Problem"] }
}

# 탐지 기록에 대상 경로가 있으면 실패 (자체 시험 폴더는 대상이 아니다)
$detections = @(Get-Detections)
foreach ($d in $detections) {
    foreach ($res in @(Get-Prop $d 'Resources')) {
        $p = ConvertFrom-MpResource "$res"
        foreach ($root in $targetRoots) {
            if (Test-ExclusionCovers $root $p) {
                $problems += "Defender 탐지 기록에 검사 대상이 있습니다: $res (ThreatID $(Get-Prop $d 'ThreatID'), $(Get-Prop $d 'InitialDetectionTime'))"
            }
        }
    }
}
Write-Host "  Get-MpThreatDetection 탐지 기록 $($detections.Count)건을 검사 대상 경로와 대조"

if ($problems.Count -gt 0) {
    Summary "### ❌ Microsoft Defender 검사 — 릴리스 중단"
    Summary ""
    foreach ($p in $problems) { Summary "- $p" }
    Summary ""
    Summary "오탐일 수 있다 — 게시하지 말고 docs/DEVELOPMENT.md의 '백신이 파일을 잡으면' 절차를 따른다."
    foreach ($p in $problems) { Write-Host "::error::$p" }
    Fail "Microsoft Defender 검사 실패 $($problems.Count)건 — 릴리스를 멈춥니다 (게시하지 않는다)"
}

# ── 7. 보고서 ────────────────────────────────────────────────────────────
Step "보고서 ($ReportPath)"
$info = @{
    Product = $product; Engine = $engine; SigVer = $sigVer; SigIso = $sigIso
    UpdateHow = $(if ($updateHow -ne "") { $updateHow } else { "failed" })
    Mode = $(if ($mode -ne "") { $mode } else { "unknown" })
    ScanIso = (ConvertTo-IsoUtc $scanStart); Removed = $removed
    SelfThreat = $selfThreat; SelfArchive = $selfArchive; Scanned = $scanned; Extracted = $extracted
}
$lines = @(New-AvReportLines $info $assetFiles $extraDirs)
$text = Format-AvReport $lines
[System.IO.File]::WriteAllText($ReportPath, $text, $Utf8NoBom)
foreach ($l in $lines) { Write-Host "  $l" }

Summary "### ✅ Microsoft Defender 검사 — 위협 없음"
Summary ""
Summary "- 엔진 $engine · 서명 $sigVer (갱신 $sigIso) · 플랫폼 $product"
Summary "- 자체 시험: EICAR 탐지 ($selfThreat), zip 안의 EICAR: $selfArchive"
Summary "- 검사: 파일 ${scanned}개 + 압축 푼 파일 ${extracted}개 (제외 항목 ${removed}개 지움, 압축 파일 검사 켬)"
foreach ($f in $assetFiles) {
    Summary ("- ``{0}`` sha256 ``{1}``" -f $f.Name, (Get-FileSha256 $f.FullName))
}
Write-Host ""
Write-Host "✅ Microsoft Defender 검사 통과 — 파일 ${scanned}개, 압축 푼 파일 ${extracted}개, 위협 없음"
exit 0
