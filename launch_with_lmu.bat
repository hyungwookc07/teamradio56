@echo off
rem ============================================================
rem teamradio56 + LMU 동시 실행 런처 (Steam 시작 옵션용)
rem
rem 설정 방법:
rem   Steam 라이브러리 > Le Mans Ultimate 우클릭 > 속성 > 일반 >
rem   시작 옵션에 아래 한 줄 입력 (경로는 실제 위치로):
rem
rem     "D:\teamradio56\launch_with_lmu.bat" %command%
rem
rem 동작:
rem   1. teamradio56을 최소화 창으로 백그라운드 실행
rem   2. 게임 실행 (Steam이 %command%로 전달)
rem   3. 게임 종료 후 20초 대기 (디브리핑/저장 마무리) 뒤 teamradio56 종료
rem
rem SimHub 플러그인을 쓰면 이 런처는 필요 없다 — Steam 시작 옵션에서
rem 지우는 것을 권장. 지우지 않아도, 엔진은 teamradio56 플러그인이 무전을
rem 맡고 있으면 이를 감지해 스스로 물러난다 (두 목소리 겹침 방지).
rem ============================================================
chcp 65001 >nul
cd /d "%~dp0"

start "teamradio56" /min cmd /c "pipenv run python main.py"

rem 게임 실행 (Steam이 전달한 원래 명령)
%*

rem 게임 종료 → 디브리핑/레이스 저장이 끝나도록 잠시 기다린 뒤 종료
timeout /t 20 /nobreak >nul
taskkill /fi "WINDOWTITLE eq teamradio56*" /t /f >nul 2>&1
