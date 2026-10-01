@echo off
REM Quick publish: single-file win-x64, no full package verification.
REM For complete build + checks use build.bat (same folder).
echo Publishing SnapActions Release win-x64 single-file...
cd /d "%~dp0.."
dotnet publish SnapActions/SnapActions.csproj -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -p:RestoreLockedMode=true ^
    -o "%~dp0bin\publish"
if %ERRORLEVEL% EQU 0 (
    echo.
    echo Published: %~dp0bin\publish\SnapActions.exe
) else (
    echo.
    echo Publish failed!
)
pause