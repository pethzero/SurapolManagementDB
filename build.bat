@echo off
setlocal
cd /d "%~dp0"
echo === Building SurapolManagementDB Demo (.NET 11) ===
dotnet --list-sdks | findstr /b "11." >nul || (echo .NET 11 SDK not found. Download: https://dotnet.microsoft.com/download/dotnet/11.0 & pause & exit /b 1)
dotnet publish SurapolManagementDB\SurapolManagementDB.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish || goto :err
echo.
echo Done: %~dp0publish\SurapolManagementDB-Demo.exe  (needs .NET 11 Desktop Runtime)
start "" "%~dp0publish"
pause
exit /b 0
:err
echo BUILD FAILED
pause
exit /b 1
