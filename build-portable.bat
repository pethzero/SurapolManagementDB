@echo off
setlocal
cd /d "%~dp0"
echo === Building SurapolManagementDB Demo - portable (no .NET install needed) ===
dotnet --list-sdks | findstr /b "11." >nul || (echo .NET 11 SDK not found. Download: https://dotnet.microsoft.com/download/dotnet/11.0 & pause & exit /b 1)
dotnet publish SurapolManagementDB\SurapolManagementDB.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish-portable || goto :err
echo.
echo Done: %~dp0publish-portable\SurapolManagementDB-Demo.exe
start "" "%~dp0publish-portable"
pause
exit /b 0
:err
echo BUILD FAILED
pause
exit /b 1
