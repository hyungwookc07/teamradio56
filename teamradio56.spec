# -*- mode: python ; coding: utf-8 -*-
# PyInstaller 스펙 — 단일 exe 패키징 (Windows)
#
#   pipenv install --dev
#   pipenv run pyinstaller teamradio56.spec
#
# 결과물: dist\teamradio56.exe
# config.yaml, audio_cache/, data/ 는 exe 옆(실행 위치)에 둔다.

a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=[],
    datas=[
        ('voice_lines', 'voice_lines'),   # 긴급 콜 변형 멘트 풀
    ],
    hiddenimports=[
        'edge_tts',
        'anthropic',
        'pygame',
        'numpy',          # 무전기 효과 (radiofx)
        'soundfile',      # mp3/wav 디코드 (radiofx)
    ],
    hookspath=[],
    runtime_hooks=[],
    excludes=['tkinter', 'matplotlib', 'numpy.tests'],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='teamradio56',
    debug=False,
    strip=False,
    upx=False,
    console=True,      # UI 없는 콘솔 앱 — 로그가 곧 인터페이스
    icon=None,
)
