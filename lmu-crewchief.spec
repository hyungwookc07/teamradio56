# -*- mode: python ; coding: utf-8 -*-
# PyInstaller 스펙 — 단일 exe 패키징 (Windows)
#
#   venv\Scripts\activate
#   pip install pyinstaller
#   pyinstaller lmu-crewchief.spec
#
# 결과물: dist\lmu-crewchief.exe
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
    name='lmu-crewchief',
    debug=False,
    strip=False,
    upx=False,
    console=True,      # UI 없는 콘솔 앱 — 로그가 곧 인터페이스
    icon=None,
)
