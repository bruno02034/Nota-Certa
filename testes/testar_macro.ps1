# Macro do Metre: compila testes\TesteMacro.cs com as fontes do app e roda contra uma janela de rejeição simulada.
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
$tmp = Join-Path $env:TEMP ('macro_teste_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $exe = Join-Path $tmp 'teste_macro.exe'
    $fontes = @(Get-ChildItem "$raiz\src\*.cs", "$raiz\src\Gui\*.cs" | ForEach-Object { $_.FullName }) + "$PSScriptRoot\TesteMacro.cs"
    & "$fw\csc.exe" /nologo /codepage:65001 /target:exe /platform:anycpu /main:DuplicidadeNfce.TesteMacro "/out:$exe" "/lib:$fw\WPF" `
        /r:System.Security.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Core.dll /r:System.Xaml.dll `
        /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:UIAutomationClient.dll /r:UIAutomationTypes.dll `
        "/resource:$raiz\src\Gui\Principal.xaml,NotaCerta.Principal.xaml" @fontes
    if ($LASTEXITCODE -ne 0) { throw "falha ao compilar o teste" }
    & $exe "$PSScriptRoot\janela_rejeicao.ps1"
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
