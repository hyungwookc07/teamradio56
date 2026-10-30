# PowerShell 스크립트 정적 검사 — 사용자·메인테이너 PC의 Windows PowerShell 5에서 돌아야 한다.
#
# 여기(리눅스)엔 pwsh 7만 있어 실행해 보는 것으로는 PowerShell 7 전용 문법이 안 걸린다.
# 그래서 파싱 오류 + 7 전용 토큰/매개변수 + BOM(없으면 5가 한글을 ANSI로 읽어 깨뜨린다)을 본다.
#
# 사용법:  pwsh -NoProfile -File simhub/verify/ps-check.ps1 update.ps1 release.ps1
$ErrorActionPreference = "Stop"

# 7에서 생긴 연산자: && || ?: ?? ??= ?. ?[
$ps7Tokens = @("AndAnd", "OrOr", "QuestionMark", "QuestionQuestion", "QuestionQuestionEquals",
               "QuestionDot", "QuestionLBracket")
# 5.1 내장 cmdlet에 없는 매개변수
$ps7Params = @("AsByteStream", "Parallel", "AdditionalChildPath", "SkipCertificateCheck",
               "SkipHttpErrorCheck", "ResponseHeadersVariable", "StatusCodeVariable")

$bad = 0
foreach ($path in $args) {
    $problems = @()
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $nonAscii = $false
    foreach ($b in $bytes) { if ($b -ge 0x80) { $nonAscii = $true; break } }
    if ($nonAscii -and -not $hasBom) { $problems += "UTF-8 BOM 없음 (Windows PowerShell 5가 한글을 깨뜨림)" }

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        (Resolve-Path -LiteralPath $path).Path, [ref]$tokens, [ref]$errors)
    foreach ($e in $errors) {
        $problems += "파싱 오류 줄 $($e.Extent.StartLineNumber): $($e.Message)"
    }
    foreach ($t in $tokens) {
        if ($ps7Tokens -contains $t.Kind.ToString()) {
            $problems += "PowerShell 7 전용 연산자 '$($t.Text)' 줄 $($t.Extent.StartLineNumber)"
        }
    }
    $paramAsts = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandParameterAst] }, $true)
    foreach ($p in $paramAsts) {
        if ($ps7Params -contains $p.ParameterName) {
            $problems += "PowerShell 7 전용 매개변수 -$($p.ParameterName) 줄 $($p.Extent.StartLineNumber)"
        }
    }
    # Join-Path 위치 인자 3개 이상은 6+ 전용 (AdditionalChildPath)
    $cmds = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
    foreach ($c in $cmds) {
        if ($c.GetCommandName() -eq "Join-Path" -and $c.CommandElements.Count -gt 3) {
            $problems += "Join-Path 인자 3개 이상 (PowerShell 6+) 줄 $($c.Extent.StartLineNumber)"
        }
    }

    if ($problems.Count -gt 0) {
        Write-Host "❌ $path"
        foreach ($p in $problems) { Write-Host "   - $p" }
        $bad++
    } else {
        Write-Host "✅ $path — 파싱·PowerShell 5 호환 검사 통과"
    }
}
if ($bad -gt 0) { exit 1 }
exit 0
