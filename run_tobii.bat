@echo off
cd /d %~dp0
rem --open: the bridge opens the browser after it is ready
python -X utf8 tobii_bridge.py --open %*
pause
