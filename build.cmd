@echo off
rem Compila o NFCe-Contingencias.exe (interface + linha de comando) com o compilador C# do proprio .NET Framework.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set CSC=%FW%\csc.exe
set SAIDA=%~dp0NFCe-Contingencias
if not exist "%CSC%" (echo csc.exe nao encontrado em %CSC% & exit /b 1)
if not exist "%SAIDA%" mkdir "%SAIDA%"
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ ^
  /out:"%SAIDA%\NFCe-Contingencias.exe" ^
  /lib:"%FW%\WPF" ^
  /r:System.Security.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Core.dll /r:System.Xaml.dll ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:UIAutomationClient.dll /r:UIAutomationTypes.dll ^
  /resource:"%~dp0src\Gui\Principal.xaml",NotaCerta.Principal.xaml /resource:"%~dp0src\Gui\logo.png",NotaCerta.logo.png ^
  "%~dp0src\*.cs" "%~dp0src\Gui\*.cs"
if errorlevel 1 exit /b 1
rem O ini da pasta de saida guarda a senha do banco criptografada: so e copiado do modelo quando ainda nao existe.
rem Na primeira compilacao com o nome novo, aproveita o do antigo dist\ (mesma maquina, mesma senha).
if not exist "%SAIDA%\duplicidade.ini" if exist "%~dp0dist\duplicidade.ini" copy "%~dp0dist\duplicidade.ini" "%SAIDA%\duplicidade.ini" >nul
if not exist "%SAIDA%\duplicidade.ini" copy "%~dp0duplicidade.ini" "%SAIDA%\duplicidade.ini" >nul
echo OK: %SAIDA%\NFCe-Contingencias.exe
