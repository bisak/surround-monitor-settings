@echo off
setlocal
rem Builds bin\MonitorControl.exe with the C# compiler that ships with Windows
rem (.NET Framework 4.8). Nothing is downloaded and no SDK is needed.

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4 compiler at %CSC%
  goto :fail
)

cd /d "%~dp0"
if not exist bin mkdir bin

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:bin\MonitorControl.exe ^
  /win32manifest:assets\app.manifest /win32icon:assets\icon.ico ^
  /lib:"%FW%\WPF" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xaml.dll ^
  /r:WindowsBase.dll /r:PresentationCore.dll /r:PresentationFramework.dll ^
  /resource:src\Styles.xaml,MonitorControl.Styles.xaml /resource:src\Layout.xaml,MonitorControl.Layout.xaml ^
  src\*.cs
if errorlevel 1 goto :fail

echo.
echo Built bin\MonitorControl.exe
if /i not "%~1"=="/nopause" pause
exit /b 0

:fail
echo.
echo Build failed.
if /i not "%~1"=="/nopause" pause
exit /b 1
