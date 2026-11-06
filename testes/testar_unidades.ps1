# Caracterização das funções puras: compila testes\TesteUnidades.cs com as fontes do app e compara a saída com
# testes\unidades.esperado.txt (gerado pela versão anterior à refatoração). -Gravar regrava o esperado.
param([switch]$Gravar)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
$tmp = Join-Path $env:TEMP ('unidades_teste_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $exe = Join-Path $tmp 'teste_unidades.exe'
    $fontes = @(Get-ChildItem "$raiz\src\*.cs", "$raiz\src\Gui\*.cs" | ForEach-Object { $_.FullName }) + "$PSScriptRoot\TesteUnidades.cs"
    & "$fw\csc.exe" /nologo /codepage:65001 /target:exe /platform:anycpu /main:DuplicidadeNfce.TesteUnidades "/out:$exe" "/lib:$fw\WPF" `
        /r:System.Security.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Core.dll /r:System.Xaml.dll `
        /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:UIAutomationClient.dll /r:UIAutomationTypes.dll `
        "/resource:$raiz\src\Gui\Principal.xaml,NotaCerta.Principal.xaml" @fontes
    if ($LASTEXITCODE -ne 0) { throw "falha ao compilar o teste" }
    $saida = Join-Path $tmp 'saida.txt'
    & $exe $saida
    $rc = $LASTEXITCODE
    $esperado = Join-Path $PSScriptRoot 'unidades.esperado.txt'
    if ($Gravar) { Copy-Item $saida $esperado -Force; Write-Host "esperado gravado: $esperado"; exit $rc }
    $dif = Compare-Object (Get-Content $esperado -Encoding UTF8) (Get-Content $saida -Encoding UTF8)
    if ($dif) { Write-Host "FALHA saída diferente do esperado:"; $dif | ForEach-Object { Write-Host ("  " + $_.SideIndicator + " " + $_.InputObject) }; exit 1 }
    Write-Host ("OK    " + (Get-Content $saida).Count + " resultado(s) iguais ao esperado")
    exit $rc
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
