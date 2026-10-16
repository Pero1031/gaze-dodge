@echo off
cd /d %~dp0
rem --open: the bridge opens a dedicated full-screen game window after it is ready.
rem Press ESC in the game to close the window and this program together.
python -X utf8 tobii_bridge.py --open %*
rem keep the console open only when something went wrong, so the message can be read
if errorlevel 1 pause
