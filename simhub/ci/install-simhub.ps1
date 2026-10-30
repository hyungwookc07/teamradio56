# SimHub 무인 설치 (GitHub Actions Windows 러너 전용) — 릴리스 DLL을 실제 SimHub 어셈블리로 빌드하려고.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File simhub\ci\install-simhub.ps1 -Version 9.13.3 -Dir C:\simhub-ci\9.13.3
#   ... -VerifyOnly       설치(캐시) 폴더에 필요한 파일이 있는지만 확인
#
# SHWotever/SimHub 릴리스 zip(Inno Setup 설치 파일 하나) → 무인 설치(작업 전부 끔: 대시보드·드라이버·
# 방화벽·재배포 패키지) → 루트의 *.dll/*.exe/*.exe.config만 -Dir로 복사(캐시 크기 줄이기).
# SimHub 파일은 아티팩트로 올리지 않는다 (재배포 금지). 실패하면 설치 로그만 남긴다.
#
# Windows PowerShell 5 호환으로 쓴다 (검증 게이트 ps-check가 막는다).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Dir,
    [string]$Sha256 = "",
    [string]$LogDir = "",
    [int]$TimeoutSec = 900,
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# 릴리스 zip sha256 — 새 버전은 첫 실행 로그에 찍힌 값을 확인하고 여기에 더한다
$KnownZipSha256 = @{
    "9.13.3" = "5a7a3f747e06d6f11dae2baa6db096718bc540f91512b54ffa55dc4652a725bd"
}
$Required = @("SimHubWPF.exe", "SimHub.Plugins.dll", "GameReaderCommon.dll")
# 설치 프로그램이 띄울 수 있는 것 — setfileassociations가 멈추면 이것부터 끈다
$Helpers = @("SimHubWPF", "SimHubWpf", "SimHub.PackageManager.Standalone", "SimHub.BitmapDisplay.Subprocess.X86")
# Inno Setup 종료 코드
$InnoExit = @{
    1 = "초기화 실패"; 2 = "시작 전 취소"; 3 = "준비 단계 치명적 오류"; 4 = "설치 중 치명적 오류"
    5 = "설치 중 취소"; 6 = "디버거로 강제 종료"; 7 = "PrepareToInstall이 진행을 거부"
    8 = "PrepareToInstall이 거부 (재부팅 필요)"
}

function Fail($msg) {
    Write-Host "::error::$msg"
    Write-Host "❌ $msg"
    exit 1
}

function Summary([string]$line) {
    if ($env:GITHUB_STEP_SUMMARY) {
        [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $line + "`n", (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Get-Sha([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Stop-Helpers {
    foreach ($n in ($Helpers + @("ngen", "mscorsvw"))) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

function Show-LogTail([string]$log) {
    if ($log -and (Test-Path -LiteralPath $log)) {
        Write-Host "── 설치 로그 마지막 60줄 ($log) ──"
        Get-Content -LiteralPath $log -Tail 60 | ForEach-Object { Write-Host "  $_" }
    }
}

function Test-SimHubDir([string]$path) {
    $missing = @()
    foreach ($n in $Required) {
        if (-not (Test-Path -LiteralPath (Join-Path $path $n) -PathType Leaf)) { $missing += $n }
    }
    if ($missing.Count -gt 0) {
        foreach ($n in $missing) { Write-Host "❌ 없음: $(Join-Path $path $n)" }
        # 설치가 다른 곳으로 갔는지 찾아 준다
        $where = @($path, "C:\Program Files (x86)\SimHub", "C:\Program Files\SimHub")
        foreach ($k in @("HKLM:\Software\SimHub", "HKLM:\Software\WOW6432Node\SimHub")) {
            $v = Get-ItemProperty -Path $k -Name InstallDirectory -ErrorAction SilentlyContinue
            if ($v -and $v.InstallDirectory) { $where += $v.InstallDirectory }
        }
        foreach ($w in $where) {
            if (Test-Path -LiteralPath ([System.IO.Path]::Combine($w, "SimHubWPF.exe"))) { Write-Host "  SimHubWPF.exe 발견: $w" }
        }
        Fail ("SimHub 폴더에 필요한 파일이 없습니다: " + ($missing -join ", ") + " ($path)")
    }
    Summary "### SimHub $Version ($path)"
    Summary ""
    Summary "| 파일 | FileVersion | ProductVersion | sha256 |"
    Summary "|---|---|---|---|"
    foreach ($n in $Required) {
        $p = Join-Path $path $n
        $vi = (Get-Item -LiteralPath $p).VersionInfo
        $sha = Get-Sha $p
        Write-Host ("  {0}  FileVersion={1}  ProductVersion={2}  sha256={3}" -f $n, $vi.FileVersion, $vi.ProductVersion, $sha)
        Summary ("| {0} | {1} | {2} | {3} |" -f $n, $vi.FileVersion, $vi.ProductVersion, $sha)
        if ($n -eq "SimHubWPF.exe") {
            $fv = "" + $vi.FileVersion
            $pv = "" + $vi.ProductVersion
            if (-not ($fv.StartsWith($Version) -or $pv.StartsWith($Version))) {
                Write-Host "::warning::SimHubWPF.exe 버전($fv / $pv)이 요청한 $Version 으로 시작하지 않습니다"
            }
        }
    }
    Write-Host "✅ SimHub $Version 확인: $path"
}

if ($Version -notmatch '^[0-9]+(\.[0-9]+){1,3}$') { Fail "SimHub 버전 형식이 잘못됨: $Version" }
$Dir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Dir)

if ($VerifyOnly) {
    Test-SimHubDir $Dir
    exit 0
}

# ── 설치 ──
if ((Test-Path -LiteralPath $Dir) -and (Get-ChildItem -LiteralPath $Dir -Force | Select-Object -First 1)) {
    Fail "-Dir 폴더가 비어 있지 않습니다: $Dir"
}
$temp = $env:RUNNER_TEMP
if (-not $temp) { $temp = [System.IO.Path]::GetTempPath() }
if ($LogDir -eq "") { $LogDir = Join-Path $temp "simhub-log" }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$log = Join-Path $LogDir "simhub-setup.log"
$zip = Join-Path $temp "SimHub.$Version.zip"
$unz = Join-Path $temp "simhub-setup-$Version"
$inst = Join-Path $temp "simhub-install"
foreach ($p in @($unz, $inst)) {
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

$url = "https://github.com/SHWotever/SimHub/releases/download/$Version/SimHub.$Version.zip"
Write-Host "▶ 내려받기: $url"
& curl.exe -fsSL --retry 5 --retry-all-errors -o $zip $url
if ($LASTEXITCODE -ne 0) { Fail "SimHub $Version 내려받기 실패 (curl $LASTEXITCODE): $url" }

$got = Get-Sha $zip
$want = $Sha256.ToLowerInvariant()
if ($want -eq "" -and $KnownZipSha256.ContainsKey($Version)) { $want = $KnownZipSha256[$Version] }
if ($want -ne "") {
    if ($got -ne $want) { Fail "SimHub.$Version.zip sha256 불일치: $got (기대 $want)" }
    Write-Host "  sha256 확인: $got"
} else {
    Write-Host "::warning::SimHub $Version zip sha256을 고정하지 않았습니다 — 이번 값 $got 을 확인하고 install-simhub.ps1 표에 더하세요"
}
Summary "SimHub.$Version.zip sha256 ``$got``"

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $unz)
$setups = @(Get-ChildItem -LiteralPath $unz -Recurse -File -Filter "SimHubSetup_*.exe")
$all = @(Get-ChildItem -LiteralPath $unz -Recurse -File)
if ($setups.Count -ne 1 -or $all.Count -ne 1) {
    Fail ("zip에 SimHubSetup_*.exe 하나만 있어야 합니다: " + (($all | ForEach-Object { $_.Name }) -join ", "))
}
$setup = $setups[0].FullName
$sig = Get-AuthenticodeSignature -LiteralPath $setup
Write-Host ("  서명: {0} {1}" -f $sig.Status, $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "" }))

# 작업은 전부 끈다(!): 대시보드·모션·방화벽·재배포 패키지(웹 설치 포함)·USB 디스플레이 드라이버·바탕화면 아이콘.
# /RELAUNCH는 절대 주지 않는다 — 설치 끝에 SimHub을 띄우는 유일한 무인 경로다.
$tasks = "!dashsandoverlays,!enablemotion,!firewall,!redist,!usbdisplays,!desktopicon"
$argLine = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL /NOICONS /NOCLOSEAPPLICATIONS " +
    "/NORESTARTAPPLICATIONS /DIR=`"$inst`" /MERGETASKS=`"$tasks`" /LOG=`"$log`""
Write-Host "▶ 무인 설치: $(Split-Path -Leaf $setup) $argLine"
$proc = Start-Process -FilePath $setup -ArgumentList $argLine -PassThru
$null = $proc.Handle   # 핸들을 먼저 잡아야 끝난 뒤 ExitCode를 읽을 수 있다
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    Write-Host "::warning::설치가 ${TimeoutSec}초 안에 끝나지 않음 — SimHub 보조 프로세스를 끄고 120초 더 기다립니다"
    foreach ($n in $Helpers) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    if (-not $proc.WaitForExit(120000)) {
        & taskkill.exe /T /F /PID $proc.Id | Out-Null
        Stop-Helpers
        Show-LogTail $log
        Fail "설치가 $($TimeoutSec + 120)초 안에 끝나지 않음 (프로세스 트리 강제 종료)"
    }
}
$code = $proc.ExitCode
Stop-Helpers
if ($code -ne 0) {
    $why = "알 수 없음"
    if ($InnoExit.ContainsKey([int]$code)) { $why = $InnoExit[[int]$code] }
    Show-LogTail $log
    Fail "SimHub 설치 실패: 종료 코드 $code ($why)"
}
Write-Host "  설치 끝 (종료 코드 0): $inst"

# 컴파일에 필요한 건 루트 어셈블리뿐 — MSBuild는 HintPath 폴더만 본다
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
$copied = 0
foreach ($f in (Get-ChildItem -LiteralPath $inst -File)) {
    $n = $f.Name.ToLowerInvariant()
    if ($n.EndsWith(".dll") -or $n.EndsWith(".exe") -or $n.EndsWith(".exe.config")) {
        Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $Dir $f.Name)
        $copied++
    }
}
Write-Host "  $copied 개 파일 → $Dir"
Test-SimHubDir $Dir
foreach ($p in @($zip, $unz)) {
    Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
