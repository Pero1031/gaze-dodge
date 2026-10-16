@echo off
cd /d %~dp0
start "" http://localhost:8000
python -X utf8 tobii_bridge.py %*
pause
