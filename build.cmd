@echo off
rem Compila o NotaCerta.exe (interface + linha de comando) com o compilador C# do proprio .NET Framework.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set CSC=%FW%\csc.exe
if not exist "%CSC%" (echo csc.exe nao encontrado em %CSC% & exit /b 1)
if not exist "%~dp0dist" mkdir "%~dp0dist"
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ ^
  /out:"%~dp0dist\NotaCerta.exe" ^
  /lib:"%FW%\WPF" ^
  /r:System.Security.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Core.dll /r:System.Xaml.dll ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll ^
  /resource:"%~dp0src\Gui\Principal.xaml",NotaCerta.Principal.xaml ^
  "%~dp0src\*.cs" "%~dp0src\Gui\*.cs"
if errorlevel 1 exit /b 1
copy /y "%~dp0duplicidade.ini" "%~dp0dist\duplicidade.ini" >nul
if exist "%~dp0dist\DuplicidadeNfce.exe" del "%~dp0dist\DuplicidadeNfce.exe"
echo OK: %~dp0dist\NotaCerta.exe
