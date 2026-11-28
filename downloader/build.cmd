@echo off
rem Builds midnightOS-Setup.exe (the Windows downloader) with the C# compiler that ships with Windows.
rem   build.cmd [URL of release.json]     default: the GitHub Pages site in ..\site-config.txt
setlocal
cd /d "%~dp0"
set URL=%~1
if "%URL%"=="" if exist "..\site-config.txt" for /f "usebackq delims=" %%u in ("..\site-config.txt") do set URL=%%u/release.json
if "%URL%"=="" set URL=https://YOUR-GITHUB-NAME.github.io/midnightos/release.json
echo Release information: %URL%
powershell -NoProfile -Command "(Get-Content -Raw -Encoding UTF8 'MidnightSetup.cs').Replace('@MANIFEST_URL@', '%URL%') | Set-Content -Encoding UTF8 'build.tmp.cs'"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:"..\downloads\midnightOS-Setup.exe" ^
  /win32icon:midnight.ico /win32manifest:app.manifest /resource:logo.png,logo.png ^
  /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll build.tmp.cs
set RESULT=%ERRORLEVEL%
del build.tmp.cs
exit /b %RESULT%
