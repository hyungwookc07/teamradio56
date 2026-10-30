# teamradio56 릴리스 스크립트 (메인테이너 전용) — release.bat 더블클릭 또는:
#
#   .\release.bat                                   기본 SimHub 경로로 빌드 → 릴리스
#   .\release.bat -SimHubPath "D:\SimHub"           SimHub이 다른 경로면
#   .\release.bat -AudioDir "D:\packs"              음성 팩 zip도 이번 릴리스에 올릴 때
#                                                   (voice_lines\audio_pack.txt의 tag가 이번 태그일 때만)
#   .\release.bat -Notes "한 줄 요약"                설정 화면에 보일 변경 사항
#   .\release.bat -NoPublish                        파일만 만들고 업로드는 손으로
#
# 하는 일: 버전 읽기(TeamRadio56Plugin.cs) → 실제 SimHub으로 Release 빌드 → 두 DLL zip
#          → sha256 → 매니페스트(teamradio56-release.txt) → dist\release\<버전>\
#          → gh가 있고 로그인돼 있으면 릴리스 생성, 없으면 새 릴리스 페이지와 폴더를 열고 올릴 파일 안내.
#
# Windows PowerShell 5 호환으로 쓴다 (?: ?? && 같은 PowerShell 7 문법 금지 — 검증 게이트가 막는다).
param(
    [string]$SimHubPath = "C:\Program Files (x86)\SimHub",
    [string]$AudioDir = "",
    [string]$Notes = "",
    [switch]$NoPublish,
    [string]$OutDir = "",
    # 검증 게이트 전용: 빌드 대신 이 폴더의 DLL을 쓴다. 게시·네트워크·창 열기는 전부 꺼진다.
    [string]$TestDllDir = ""
)

$ErrorActionPreference = "Stop"

$Repo = "hyungwookc07/teamradio56"
$RepoUrl = "https://github.com/$Repo"
$ManifestName = "teamradio56-release.txt"
$DllNames = @("TeamRadio56.Core.dll", "TeamRadio56.SimHub.dll")
$TestMode = $TestDllDir -ne ""
# 매니페스트 이름 규칙 — Core ReleaseManifest.IsSafeName과 같아야 한다
$SafeNamePattern = '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'

function Pause-IfInteractive {
    if (-not $TestMode) { Read-Host "Enter를 누르면 닫힙니다" | Out-Null }
}

function Fail($msg) {
    Write-Host ""
    Write-Host "❌ $msg" -ForegroundColor Red
    Pause-IfInteractive
    exit 1
}

function Step($msg) { Write-Host "▶ $msg" -ForegroundColor Cyan }
function Warn($msg) { Write-Host "⚠ $msg" -ForegroundColor Yellow }

function Test-SafeName([string]$s) {
    return ($s -match $SafeNamePattern) -and (-not $s.Contains(".."))
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

# 네이티브 명령의 stderr를 2>&1로 받으면 PowerShell 5는 Stop 모드에서 예외로 바꾼다
function Invoke-Quiet([scriptblock]$block) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    # 명령이 아예 없으면 종료 코드가 갱신되지 않으므로 실패로 시작한다
    $global:LASTEXITCODE = -1
    try {
        $out = & $block 2>&1 | ForEach-Object { "$_" }
        return @{ Code = $LASTEXITCODE; Output = @($out) }
    } finally {
        $ErrorActionPreference = $old
    }
}

function Write-Utf8NoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Test-PeImage([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        if ($fs.Length -lt 2) { return $false }
        $a = $fs.ReadByte(); $b = $fs.ReadByte()
        return ($a -eq 0x4D) -and ($b -eq 0x5A)
    } finally {
        $fs.Dispose()
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 상대 경로는 실행한 위치 기준 — 작업 폴더를 옮기기 전에 절대 경로로 바꾼다
# (.NET 호출은 PowerShell 현재 위치가 아니라 프로세스 작업 폴더 기준이라 절대 경로가 필요하다)
if ($AudioDir -ne "") {
    if (-not (Test-Path -LiteralPath $AudioDir -PathType Container)) { Fail "-AudioDir 폴더가 없습니다: $AudioDir" }
    $AudioDir = (Resolve-Path -LiteralPath $AudioDir).Path
}
if ($TestMode) {
    if (-not (Test-Path -LiteralPath $TestDllDir -PathType Container)) { Fail "-TestDllDir 폴더가 없습니다: $TestDllDir" }
    $TestDllDir = (Resolve-Path -LiteralPath $TestDllDir).Path
}
if ($OutDir -ne "") { $OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir) }
Set-Location $PSScriptRoot

# ── 1. 버전 ──────────────────────────────────────────────────────────────
$pluginSource = Join-Path $PSScriptRoot "simhub\src\TeamRadio56.SimHub\TeamRadio56Plugin.cs"
if (-not (Test-Path -LiteralPath $pluginSource)) { Fail "플러그인 소스가 없습니다: $pluginSource" }
$src = Get-Content -LiteralPath $pluginSource -Raw -Encoding UTF8
$m = [regex]::Match($src, 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"')
if (-not $m.Success) { Fail "TeamRadio56Plugin.cs에서 Version 상수를 찾지 못했습니다" }
$FullVersion = $m.Groups[1].Value
$vm = [regex]::Match($FullVersion, '^(\d+)\.(\d+)\.(\d+)')
if (-not $vm.Success) { Fail "Version이 x.y.z로 시작하지 않습니다: $FullVersion" }
$Version = $vm.Value
$Tag = "v$Version"
$PluginZipName = "teamradio56-plugin-$Version.zip"
Write-Host "teamradio56 $FullVersion  →  태그 $Tag" -ForegroundColor Green

# ── 2. 음성 팩 기술자 ────────────────────────────────────────────────────
$packFile = Join-Path $PSScriptRoot "voice_lines\audio_pack.txt"
if (-not (Test-Path -LiteralPath $packFile)) { Fail "음성 팩 기술자가 없습니다: $packFile" }
$PackId = ""
$AudioTag = ""
$AudioAssets = @()
foreach ($raw in (Get-Content -LiteralPath $packFile -Encoding UTF8)) {
    $line = $raw.Trim()
    if ($line -eq "" -or $line.StartsWith("#")) { continue }
    $eq = $line.IndexOf("=")
    if ($eq -le 0) { Fail "audio_pack.txt 줄 형식이 잘못됨: $line" }
    $key = $line.Substring(0, $eq).Trim()
    $val = $line.Substring($eq + 1).Trim()
    switch ($key) {
        "pack" { $PackId = $val }
        "tag" { $AudioTag = $val }
        "asset" {
            $parts = $val.Split(",")
            if ($parts.Count -ne 3) { Fail "asset 줄은 이름,sha256,크기 세 칸이어야 합니다: $line" }
            $name = $parts[0].Trim()
            $sha = $parts[1].Trim().ToLowerInvariant()
            $sizeText = $parts[2].Trim()
            if (-not (Test-SafeName $name) -or -not $name.ToLowerInvariant().EndsWith(".zip")) {
                Fail "asset 이름이 잘못됨: $name"
            }
            if ($sha -notmatch '^[0-9a-f]{64}$') { Fail "asset sha256이 64자리 16진수가 아닙니다: $name" }
            if ($sizeText -notmatch '^[0-9]+$' -or [int64]$sizeText -le 0) { Fail "asset 크기가 잘못됨: $name" }
            foreach ($a in $AudioAssets) {
                if ($a.Name -eq $name) { Fail "asset 이름 중복: $name" }
            }
            $AudioAssets += New-Object PSObject -Property @{ Name = $name; Sha = $sha; Size = [int64]$sizeText }
        }
        default { Fail "audio_pack.txt에 모르는 키: $key" }
    }
}
if (-not (Test-SafeName $PackId)) { Fail "audio_pack.txt의 pack이 비었거나 잘못됨: '$PackId'" }
if (-not (Test-SafeName $AudioTag)) { Fail "audio_pack.txt의 tag가 비었거나 잘못됨: '$AudioTag'" }
if ($AudioAssets.Count -eq 0) { Fail "audio_pack.txt에 asset 줄이 없습니다" }
$AudioHere = $AudioTag -eq $Tag
$audioTotal = [int64]0
foreach ($a in $AudioAssets) { $audioTotal += $a.Size }
Write-Host ("음성 팩 {0}: zip {1}개, {2:N1} MB — 자산 위치 {3}{4}" -f $PackId, $AudioAssets.Count,
    ($audioTotal / 1MB), $AudioTag, $(if ($AudioHere) { " (이번 릴리스)" } else { "" }))

# ── 3. 출력 폴더 ─────────────────────────────────────────────────────────
if ($OutDir -eq "") {
    $OutDir = Join-Path $PSScriptRoot (Join-Path "dist\release" $Version)
    if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
} elseif ((Test-Path -LiteralPath $OutDir) -and (Get-ChildItem -LiteralPath $OutDir -Force | Select-Object -First 1)) {
    Fail "-OutDir 폴더가 비어 있지 않습니다: $OutDir"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

# ── 4. 최신 릴리스보다 높은 버전인지 (사용자 플러그인은 더 높은 버전만 제안한다) ──
if (-not $TestMode) {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $resp = Invoke-WebRequest -UseBasicParsing -TimeoutSec 20 -Uri "$RepoUrl/releases/latest/download/$ManifestName"
        $text = $resp.Content
        if ($text -is [byte[]]) { $text = [System.Text.Encoding]::UTF8.GetString($text) }
        $lm = [regex]::Match($text, '(?m)^\s*version\s*=\s*(\d+\.\d+\.\d+)\s*$')
        if ($lm.Success) {
            $latest = $lm.Groups[1].Value
            if ([version]$Version -le [version]$latest) {
                Fail "이번 버전 $Version 이(가) 게시된 최신 $latest 보다 높지 않습니다 — 사용자에게 업데이트가 안 뜹니다. TeamRadio56Plugin.Version을 올리세요"
            }
            Write-Host "게시된 최신 버전: $latest → 이번 $Version"
        }
    } catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 404) {
            Write-Host "게시된 매니페스트 없음 — 첫 업데이터 릴리스입니다"
        } else {
            Warn "최신 릴리스를 확인하지 못했습니다 (버전 비교 생략): $($_.Exception.Message)"
        }
    }
}

# ── 5. 빌드 ──────────────────────────────────────────────────────────────
if ($TestMode) {
    Warn "검증 모드 — 빌드하지 않고 $TestDllDir 의 DLL을 씁니다 (게시하지 않음)"
    $dllDir = $TestDllDir
} else {
    # 끝의 \ 는 PowerShell 5가 따옴표를 붙여 넘길 때 \" 로 바뀌어 인자가 깨진다
    $SimHubPath = $SimHubPath.TrimEnd('\', '/')
    # 스텁(simhub\verify\stubout)에는 SimHubWPF.exe가 없다 — 릴리스 DLL은 실제 SimHub으로만 빌드
    if (-not (Test-Path -LiteralPath (Join-Path $SimHubPath "SimHub.Plugins.dll")) -or
        -not (Test-Path -LiteralPath (Join-Path $SimHubPath "SimHubWPF.exe"))) {
        Fail "실제 SimHub을 찾을 수 없습니다: $SimHubPath  (다른 경로면 -SimHubPath 로 지정)"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail "dotnet이 없습니다 — winget install Microsoft.DotNet.SDK.8 후 창을 새로 여세요"
    }
    $gitState = Invoke-Quiet { git status --porcelain }
    if ($gitState.Code -eq 0 -and $gitState.Output.Count -gt 0) {
        Warn "커밋하지 않은 변경이 있습니다 — 릴리스는 지금 작업 폴더 그대로 빌드됩니다"
    }

    Step "Release 빌드 (SimHub: $SimHubPath)"
    $buildStart = (Get-Date).AddSeconds(-2)
    dotnet build simhub\src\TeamRadio56.SimHub\TeamRadio56.SimHub.csproj -c Release --no-incremental `
        -p:SimHubPath="$SimHubPath" -v m --nologo
    if ($LASTEXITCODE -ne 0) { Fail "빌드 실패 — 위 에러를 확인하세요" }
    $dllDir = Join-Path $PSScriptRoot "simhub\src\TeamRadio56.SimHub\bin\Release\net48"
    foreach ($n in $DllNames) {
        $p = Join-Path $dllDir $n
        if ((Test-Path -LiteralPath $p) -and (Get-Item -LiteralPath $p).LastWriteTime -lt $buildStart) {
            Fail "$n 이(가) 이번 빌드에서 새로 쓰이지 않았습니다 — 빌드 출력을 확인하세요"
        }
    }
}
foreach ($n in $DllNames) {
    $p = Join-Path $dllDir $n
    if (-not (Test-Path -LiteralPath $p)) { Fail "DLL이 없습니다: $p" }
    if (-not (Test-PeImage $p)) { Fail "DLL 형식이 아닙니다: $p" }
}

# ── 6. 플러그인 zip (루트에 두 DLL만) ────────────────────────────────────
Step "플러그인 zip → $PluginZipName"
$pluginZip = Join-Path $OutDir $PluginZipName
$zip = [System.IO.Compression.ZipFile]::Open($pluginZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($n in $DllNames) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, (Join-Path $dllDir $n), $n, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $zip.Dispose()
}
$pluginSha = Get-Sha256 $pluginZip

# ── 7. 음성 팩 zip 확인 (-AudioDir) ──────────────────────────────────────
$uploadAudio = @()
if ($AudioDir -ne "") {
    Step "음성 팩 zip 확인 ($AudioDir)"
    foreach ($a in $AudioAssets) {
        $p = Join-Path $AudioDir $a.Name
        if (-not (Test-Path -LiteralPath $p)) { Fail "음성 팩 zip이 없습니다: $p" }
        if ((Get-Item -LiteralPath $p).Length -ne $a.Size) { Fail "크기가 audio_pack.txt와 다릅니다: $($a.Name)" }
        if ((Get-Sha256 $p) -ne $a.Sha) { Fail "sha256이 audio_pack.txt와 다릅니다: $($a.Name)" }
        # 플러그인은 zip 루트의 *.wav만 푼다 — 폴더째 묶은 zip은 아무것도 설치되지 않는다
        $za = [System.IO.Compression.ZipFile]::OpenRead($p)
        try {
            $wav = 0
            foreach ($e in $za.Entries) {
                $isRootWav = ($e.FullName -notmatch '[\\/]') -and $e.FullName.ToLowerInvariant().EndsWith(".wav")
                if (-not $isRootWav) { Fail "$($a.Name)에 루트 *.wav가 아닌 항목이 있습니다: $($e.FullName)" }
                $wav++
            }
        } finally {
            $za.Dispose()
        }
        Write-Host ("  ✓ {0} — wav {1}개" -f $a.Name, $wav)
        if ($AudioHere) { $uploadAudio += $p }
    }
    if (-not $AudioHere) {
        Warn "음성 팩은 이미 $AudioTag 릴리스에 있으므로 이번 릴리스에는 올리지 않습니다"
    }
} elseif ($AudioHere) {
    Warn "audio_pack.txt의 tag가 이번 태그($Tag)입니다 — 팩 zip $($AudioAssets.Count)개를 이 릴리스에 함께 올려야 합니다 (-AudioDir <zip 폴더>)"
}

# ── 8. 매니페스트 ────────────────────────────────────────────────────────
$noteLine = ($Notes -replace '[\r\n]+', ' ').Trim()
$lines = @(
    "# teamradio56 릴리스 매니페스트 — release.ps1이 생성 ($FullVersion)",
    "format=1",
    "version=$Version",
    "tag=$Tag",
    "plugin_asset=$PluginZipName",
    "plugin_sha256=$pluginSha",
    "audio_pack=$PackId",
    "audio_tag=$AudioTag",
    ("audio_assets=" + (($AudioAssets | ForEach-Object { $_.Name }) -join ",")),
    ("audio_sha256=" + (($AudioAssets | ForEach-Object { $_.Sha }) -join ",")),
    ("audio_size=" + (($AudioAssets | ForEach-Object { $_.Size }) -join ","))
)
if ($noteLine -ne "") { $lines += "notes=$noteLine" }
$manifestPath = Join-Path $OutDir $ManifestName
Write-Utf8NoBom $manifestPath (($lines -join "`n") + "`n")

$files = @($pluginZip, $manifestPath)
foreach ($p in $uploadAudio) {
    # 손으로 올릴 때 폴더 하나만 끌어다 놓으면 되게 복사해 둔다
    $dst = Join-Path $OutDir (Split-Path -Leaf $p)
    Copy-Item -LiteralPath $p -Destination $dst -Force
    $files += $dst
}

Write-Host ""
Write-Host "릴리스 파일 ($OutDir):" -ForegroundColor Green
foreach ($f in $files) {
    Write-Host ("  {0}  ({1:N1} MB)" -f (Split-Path -Leaf $f), ((Get-Item -LiteralPath $f).Length / 1MB))
}
Write-Host "  plugin_sha256 = $pluginSha"

if ($TestMode) {
    Write-Host "검증 모드 — 여기서 끝냅니다"
    exit 0
}

# ── 9. 게시 ──────────────────────────────────────────────────────────────
$title = "teamradio56 $FullVersion"
$bodyText = "teamradio56 $FullVersion"
if ($noteLine -ne "") { $bodyText += "`n`n$noteLine" }
$bodyText += "`n`n설치된 플러그인(0.16 이상)은 SimHub 설정 화면 [업데이트 확인]으로 받습니다. " +
    "처음 설치하거나 0.15 이하에서 올라올 때는 $PluginZipName 의 두 DLL을 SimHub 폴더에 복사하세요."

$useGh = $false
if (-not $NoPublish -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $auth = Invoke-Quiet { gh auth status }
    if ($auth.Code -eq 0) { $useGh = $true } else { Warn "gh가 로그인돼 있지 않습니다 (gh auth login) — 수동 업로드로 진행합니다" }
}

if ($useGh) {
    if ($AudioHere -and $uploadAudio.Count -eq 0) {
        Fail "이번 릴리스가 음성 팩 자산 위치($AudioTag)입니다 — -AudioDir <팩 zip 폴더> 를 함께 주세요"
    }
    if (-not $AudioHere) {
        $view = Invoke-Quiet { gh release view $AudioTag --repo $Repo --json assets -q ".assets[].name" }
        if ($view.Code -ne 0) { Fail "음성 팩 릴리스 $AudioTag 를 찾지 못했습니다 — 지우지 않았는지 확인하세요" }
        foreach ($a in $AudioAssets) {
            if ($view.Output -notcontains $a.Name) { Fail "$AudioTag 릴리스에 $($a.Name) 이(가) 없습니다" }
        }
    }
    $exists = Invoke-Quiet { gh release view $Tag --repo $Repo }
    if ($exists.Code -eq 0) { Fail "릴리스 $Tag 가 이미 있습니다 — 버전을 올리거나 GitHub에서 지운 뒤 다시 실행하세요" }

    $head = Invoke-Quiet { git rev-parse HEAD }
    if ($head.Code -ne 0) { Fail "git 커밋을 확인하지 못했습니다" }
    $commit = $head.Output[0].Trim()
    $remote = Invoke-Quiet { git branch -r --contains $commit }
    if ($remote.Code -ne 0 -or $remote.Output.Count -eq 0) {
        Fail "현재 커밋 $commit 이(가) GitHub에 push되지 않았습니다 — push 후 다시 실행하세요"
    }

    $notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "teamradio56-release-notes.txt"
    Write-Utf8NoBom $notesFile $bodyText
    Step "GitHub 릴리스 생성: $Tag"
    gh release create $Tag @files --repo $Repo --target $commit --title $title --notes-file $notesFile
    $code = $LASTEXITCODE
    Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
    if ($code -ne 0) { Fail "gh release create 실패 — 위 메시지를 확인하세요 (파일은 $OutDir 에 있습니다)" }

    Write-Host ""
    Write-Host "✅ 릴리스 게시 완료: $RepoUrl/releases/tag/$Tag" -ForegroundColor Green
    Write-Host "   확인: $RepoUrl/releases/latest/download/$ManifestName"
    Pause-IfInteractive
    exit 0
}

# gh 없음 / -NoPublish — 새 릴리스 페이지와 폴더를 열고 손으로 올린다
$branch = Invoke-Quiet { git rev-parse --abbrev-ref HEAD }
$branchName = "main"
if ($branch.Code -eq 0 -and $branch.Output.Count -gt 0) { $branchName = $branch.Output[0].Trim() }
Start-Process "$RepoUrl/releases/new?tag=$Tag"
Invoke-Item -LiteralPath $OutDir

Write-Host ""
Write-Host "수동 업로드 순서 (브라우저에 새 릴리스 페이지, 탐색기에 릴리스 폴더를 열었습니다):" -ForegroundColor Cyan
Write-Host "  0. 이 커밋을 먼저 push 하세요 (git push)"
Write-Host "  1. Choose a tag: $Tag  (없으면 'Create new tag'), Target: $branchName"
Write-Host "  2. Release title: $title"
Write-Host "  3. 설명란에 붙여 넣기:"
foreach ($l in $bodyText.Split("`n")) { Write-Host "       $l" }
Write-Host "  4. 아래 파일을 전부 'Attach binaries' 칸에 끌어다 놓기 (이름 바꾸지 말 것):"
foreach ($f in $files) { Write-Host "       $(Split-Path -Leaf $f)" }
if ($AudioHere -and $uploadAudio.Count -eq 0) {
    Write-Host "       + 음성 팩 zip $($AudioAssets.Count)개 (이번 릴리스가 팩 자산 위치 — 빠뜨리면 음성 팩 업데이트가 404):" -ForegroundColor Yellow
    foreach ($a in $AudioAssets) { Write-Host "         $($a.Name)" -ForegroundColor Yellow }
}
Write-Host "  5. 'Set as a pre-release'는 끄고 'Set as the latest release'는 켠 채 Publish release"
Write-Host "  6. 확인: $RepoUrl/releases/latest/download/$ManifestName 이 이번 버전을 보여야 합니다"
Pause-IfInteractive
