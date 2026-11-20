@echo off
cd /d %~dp0
rem Unity version of Gaze Dodge: starts the game (unity\Build\GazeDodge.exe) and the bridge.
rem Build the game first in Unity: menu "Gaze Dodge > Build Windows".
rem Press ESC in the game to close the game and this program together. Extra options go to the bridge (e.g. --mock).
if not exist "unity\Build\GazeDodge.exe" (
  echo unity\Build\GazeDodge.exe was not found. Build it first in Unity: menu "Gaze Dodge > Build Windows".
  pause
  exit /b 1
)
start "" "unity\Build\GazeDodge.exe"
python -X utf8 tobii_bridge.py %*
rem keep the console open only when something went wrong, so the message can be read
if errorlevel 1 pause
