@echo off
rem Baut eine portable Version nach .\publish\MultiServerView.exe  (braucht .NET 8 SDK und Node.js)
setlocal
cd /d "%~dp0\.."

echo [1/3] Tests...
dotnet test tests\MultiServerView.Core.Tests -c Release || goto :fail

echo [2/3] UI...
pushd ui
call npm ci || (popd & goto :fail)
call npm run build || (popd & goto :fail)
popd

echo [3/3] App...
dotnet publish src\MultiServerView.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o publish || goto :fail

echo.
echo Fertig: %CD%\publish\MultiServerView.exe
exit /b 0

:fail
echo.
echo FEHLER - siehe oben.
exit /b 1
