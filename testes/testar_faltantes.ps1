# Testes da seção Notas Faltantes, com XMLs SINTÉTICOS (CNPJ fictício) gerados aqui e uma SEFAZ simulada local
# (testes\sefaz_respostas.ps1). Nenhum teste chama a SEFAZ real nem usa dados de cliente.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File testes\testar_faltantes.ps1
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$exe = if ($env:NOTACERTA_EXE) { $env:NOTACERTA_EXE } else { Join-Path $raiz 'dist\NotaCerta.exe' }
$tmp = Join-Path $env:TEMP ('falt_teste_' + [guid]::NewGuid().ToString('N'))
$falhas = 0
$execucao = 0
$utf8 = New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Cnpj = '11222333000181'       # fictício
$Outro = '99888777000166'      # fictício (outro emitente, mesma série)
$Serie = 7
$Ns = 'http://www.portalfiscal.inf.br/nfe'

function Verdade($cond, $descricao) {
    if ($cond) { Write-Host "OK    $descricao" } else { Write-Host "FALHA $descricao"; $script:falhas++ }
}

function Chave($cnpj, $aamm, $numero, $cnf) {
    $b = '31' + $aamm + $cnpj + '65' + $Serie.ToString('000') + ([long]$numero).ToString('000000000') + '9' + $cnf
    $peso = 2; $soma = 0
    for ($i = $b.Length - 1; $i -ge 0; $i--) { $soma += [int][string]$b[$i] * $peso; $peso = if ($peso -eq 9) { 2 } else { $peso + 1 } }
    $dv = 11 - ($soma % 11); if ($dv -ge 10) { $dv = 0 }
    $b + $dv
}

# NFe "assinada" (DigestValue calculado como na NF-e; a SignatureValue é fictícia, o NotaCerta não a usa).
function NFe($chave, $numero, $dhEmi, $vNF, $cnpj = $Cnpj) {
    $inf = "<infNFe versao=`"4.00`" Id=`"NFe$chave`"><ide><cUF>31</cUF><cNF>$($chave.Substring(35,8))</cNF><natOp>VENDA</natOp><mod>65</mod><serie>$Serie</serie><nNF>$numero</nNF><dhEmi>$dhEmi</dhEmi><tpNF>1</tpNF><tpEmis>9</tpEmis><cDV>$($chave.Substring(43,1))</cDV><tpAmb>1</tpAmb></ide><emit><CNPJ>$cnpj</CNPJ><xNome>EMPRESA TESTE</xNome></emit><total><ICMSTot><vNF>$vNF</vNF></ICMSTot></total></infNFe>"
    $d = New-Object Xml.XmlDocument; $d.PreserveWhitespace = $true
    $d.LoadXml("<NFe xmlns=`"$Ns`">$inf</NFe>")
    $d2 = New-Object Xml.XmlDocument; $d2.PreserveWhitespace = $true; $d2.LoadXml($d.DocumentElement.FirstChild.OuterXml)
    $t = New-Object Security.Cryptography.Xml.XmlDsigC14NTransform; $t.LoadInput($d2)
    $dig = [Convert]::ToBase64String([Security.Cryptography.SHA1]::Create().ComputeHash($t.GetOutput([IO.Stream])))
    $nfe = "<NFe xmlns=`"$Ns`">$inf<Signature xmlns=`"http://www.w3.org/2000/09/xmldsig#`"><SignedInfo><Reference URI=`"#NFe$chave`"><DigestValue>$dig</DigestValue></Reference></SignedInfo><SignatureValue>AAAA</SignatureValue></Signature></NFe>"
    @{ Xml = $nfe; Digest = $dig; Chave = $chave }
}

function Prot($chave, $digVal, $nProt) {
    "<protNFe versao=`"4.00`"><infProt><tpAmb>1</tpAmb><verAplic>TESTE</verAplic><chNFe>$chave</chNFe><dhRecbto>2026-09-10T10:00:00-03:00</dhRecbto><nProt>$nProt</nProt><digVal>$digVal</digVal><cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo></infProt></protNFe>"
}

function EventoCanc($chave, $nProt, $comNs) {
    $x = if ($comNs) { " xmlns=`"$Ns`"" } else { '' }
    "<procEventoNFe versao=`"1.00`"$x><evento versao=`"1.00`"><infEvento Id=`"ID110111$($chave)01`"><cOrgao>31</cOrgao><tpAmb>1</tpAmb><CNPJ>$Cnpj</CNPJ><chNFe>$chave</chNFe><dhEvento>2026-09-10T11:00:00-03:00</dhEvento><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><verEvento>1.00</verEvento><detEvento versao=`"1.00`"><descEvento>Cancelamento</descEvento><nProt>1</nProt><xJust>cancelamento de teste</xJust></detEvento></infEvento></evento><retEvento versao=`"1.00`"><infEvento><tpAmb>1</tpAmb><verAplic>TESTE</verAplic><cOrgao>31</cOrgao><cStat>135</cStat><xMotivo>Evento registrado e vinculado a NF-e</xMotivo><chNFe>$chave</chNFe><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><dhRegEvento>2026-09-10T11:00:01-03:00</dhRegEvento><nProt>$nProt</nProt></infEvento></retEvento></procEventoNFe>"
}

function Gravar($caminho, $texto) {
    New-Item -ItemType Directory -Force -Path (Split-Path $caminho) | Out-Null
    [IO.File]::WriteAllText($caminho, '<?xml version="1.0" encoding="UTF-8"?>' + $texto, $utf8)
}

function Resposta($chave, $texto) { [IO.File]::WriteAllText((Join-Path $respostas "$chave.xml"), $texto, $utf8) }

function Ret100($chave, $digVal, $nProt) {
    "<cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo><cUF>31</cUF><dhRecbto>2026-10-22T12:00:00-03:00</dhRecbto><chNFe>$chave</chNFe>" + (Prot $chave $digVal $nProt)
}

function NovoPfx($arquivo, $cnpj, $senha) {
    New-Item -ItemType Directory -Force -Path (Split-Path $arquivo) | Out-Null
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $req = New-Object Security.Cryptography.X509Certificates.CertificateRequest("CN=EMPRESA TESTE:$cnpj", $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $c = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(1))
    [IO.File]::WriteAllBytes($arquivo, $c.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $senha))
}

# Roda o exe; devolve as linhas do CSV indexadas pelo número.
function Rodar($extra, $opcoes) {
    $script:execucao++
    $rel = Join-Path $tmp "rel_$execucao"
    $ini = Join-Path $tmp "cfg_$execucao.ini"
    "PastaRaiz=$xmls`r`nPastaRelatorios=$rel`r`nPastaNotasFaltantes=$base`r`nPastaDestino=$tmp\dup`r`nIniMetre=$tmp\nenhum.ini`r`n$extra" | Set-Content -Path $ini -Encoding UTF8
    $saida = & { $ErrorActionPreference = 'Continue'; & $exe --config $ini --faltantes --cnpj $Cnpj --mes 09/2026 @opcoes 2>&1 } | ForEach-Object { "$_" }
    $script:codigo = $LASTEXITCODE
    $script:ultimaSaida = $saida -join "`n"
    if ($codigo -ne 0) { return @{} }
    $script:ultimoCsv = [regex]::Match($ultimaSaida, 'Relatório: (.+\.csv)').Groups[1].Value.Trim()
    $script:ultimaPasta = [regex]::Match($ultimaSaida, 'Entrega:\s+(.+)').Groups[1].Value.Trim()
    $script:ultimaSaida += "`n" + (Get-Content ([regex]::Match($ultimaSaida, 'Log:\s+(.+\.log)').Groups[1].Value.Trim()) -Raw)
    $h = @{}
    Import-Csv $ultimoCsv -Delimiter ';' -Encoding UTF8 | ForEach-Object { $h[$_.Numero] = $_ }
    $h
}

function Confere($r, $numero, $status, $entregues, $motivo = '') {
    $l = $r["$numero"]
    $ok = $l -and $l.Status -eq $status -and $l.'Arquivo(s) entregue(s)' -eq $entregues -and $l.Motivo.Contains($motivo)
    if ($ok) { Write-Host "OK    $numero -> $status $entregues" }
    else {
        Write-Host "FALHA $numero esperado [$status|$entregues|$motivo]"
        Write-Host "      obtido  [$($l.Status)|$($l.'Arquivo(s) entregue(s)')|$($l.Motivo)]"
        $script:falhas++
    }
}

function Hash($f) { (Get-FileHash $f -Algorithm SHA256).Hash }
function HashArvore($d) { (Get-ChildItem $d -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName + '=' + (Hash $_.FullName) }) -join "`n" }

New-Item -ItemType Directory -Path $tmp | Out-Null
$xmls = Join-Path $tmp 'NFCe'
$base = Join-Path $tmp 'NotasFaltantes'
$respostas = Join-Path $tmp 'respostas'
New-Item -ItemType Directory -Path $respostas | Out-Null
$servidor = $null
try {
    # ---------- Massa de teste ----------
    $dia = Join-Path $xmls '10_2026\22'          # notas de setembro guardadas na pasta de outubro (como no cliente)
    $aut = Join-Path $dia 'Autorizadas'

    # 501: só o ASSINADA (enviNFe, sem protocolo) -> copia o ASSINADA e monta o procNFe com o protocolo da SEFAZ
    $n501 = NFe (Chave $Cnpj '2609' 501 '10000501') 501 '2026-09-12T13:20:16-03:00' '33.00'
    Gravar "$dia\NOTA_501_SERIE_7_ASSINADA_12_09_2026_13_20_16.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n501.Xml)</enviNFe>"
    Gravar "$dia\NOTA_501_SERIE_7_REJEICAO_NOTA.xml" "<nfeProc versao=`"4.00`" xmlns=`"$Ns`">$($n501.Xml)</nfeProc>"
    Gravar "$dia\NOTA_501_SERIE_7_Temp.xml" "<nfeProc versao=`"4.00`" xmlns=`"$Ns`">$($n501.Xml)</nfeProc>"
    Resposta $n501.Chave (Ret100 $n501.Chave $n501.Digest '131260000000501')

    # 502: NFCE completo (nfeProc com protocolo) em Autorizadas + ASSINADA igual -> vai só o NFCE
    $n502 = NFe (Chave $Cnpj '2609' 502 '10000502') 502 '2026-09-13T10:00:00-03:00' '12.50'
    Gravar "$dia\NOTA_502_SERIE_7_ASSINADA_13_09_2026_10_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n502.Xml)</enviNFe>"
    Gravar "$aut\NOTA_502_SERIE_7_NFCE.xml" "<nfeProc versao=`"4.00`" xmlns=`"$Ns`">$($n502.Xml)$(Prot $n502.Chave $n502.Digest '131260000000502')</nfeProc>"
    Resposta $n502.Chave (Ret100 $n502.Chave $n502.Digest '131260000000502')

    # 503: duas chaves (uma nunca transmitida, 217; outra autorizada) -> vale a autorizada; solta na raiz
    $n503a = NFe (Chave $Cnpj '2609' 503 '10000531') 503 '2026-09-14T09:00:00-03:00' '20.00'
    $n503b = NFe (Chave $Cnpj '2609' 503 '10000532') 503 '2026-09-14T09:05:00-03:00' '20.00'
    Gravar "$dia\NOTA_503_SERIE_7_ASSINADA_14_09_2026_09_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n503a.Xml)</enviNFe>"
    Gravar "$xmls\NOTA_503_SERIE_7_ASSINADA_14_09_2026_09_05_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n503b.Xml)</enviNFe>"
    Resposta $n503b.Chave (Ret100 $n503b.Chave $n503b.Digest '131260000000503')

    # 504: cancelada; o evento vem na resposta da SEFAZ
    $n504 = NFe (Chave $Cnpj '2609' 504 '10000504') 504 '2026-09-15T09:00:00-03:00' '40.00'
    Gravar "$dia\NOTA_504_SERIE_7_ASSINADA_15_09_2026_09_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n504.Xml)</enviNFe>"
    Resposta $n504.Chave ("<cStat>101</cStat><xMotivo>Cancelamento de NF-e homologado</xMotivo><cUF>31</cUF><dhRecbto>2026-10-22T12:00:00-03:00</dhRecbto><chNFe>$($n504.Chave)</chNFe>" +
        (Prot $n504.Chave $n504.Digest '131260000000504') + (EventoCanc $n504.Chave '131260000009504' $false))

    # 505: cancelada; o evento está no disco (nome fora do padrão NOTA_)
    $n505 = NFe (Chave $Cnpj '2609' 505 '10000505') 505 '2026-09-16T09:00:00-03:00' '41.00'
    Gravar "$aut\NOTA_505_SERIE_7_NFCE.xml" "<nfeProc versao=`"4.00`" xmlns=`"$Ns`">$($n505.Xml)$(Prot $n505.Chave $n505.Digest '131260000000505')</nfeProc>"
    Gravar "$xmls\Eventos\110111$($n505.Chave)01-procEventoNFe.xml" (EventoCanc $n505.Chave '131260000009505' $true)
    Resposta $n505.Chave ("<cStat>101</cStat><xMotivo>Cancelamento de NF-e homologado</xMotivo><cUF>31</cUF><dhRecbto>2026-10-22T12:00:00-03:00</dhRecbto><chNFe>$($n505.Chave)</chNFe>" +
        (Prot $n505.Chave $n505.Digest '131260000000505'))

    # 506: cancelada, sem evento em lugar nenhum
    $n506 = NFe (Chave $Cnpj '2609' 506 '10000506') 506 '2026-09-17T09:00:00-03:00' '42.00'
    Gravar "$dia\NOTA_506_SERIE_7_ASSINADA_17_09_2026_09_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n506.Xml)</enviNFe>"
    Resposta $n506.Chave ("<cStat>101</cStat><xMotivo>Cancelamento de NF-e homologado</xMotivo><cUF>31</cUF><dhRecbto>2026-10-22T12:00:00-03:00</dhRecbto><chNFe>$($n506.Chave)</chNFe>" +
        (Prot $n506.Chave $n506.Digest '131260000000506'))

    # 507: autorizada, mas emitida em agosto
    $n507 = NFe (Chave $Cnpj '2608' 507 '10000507') 507 '2026-08-31T23:50:00-03:00' '7.00'
    Gravar "$dia\NOTA_507_SERIE_7_ASSINADA_31_08_2026_23_50_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n507.Xml)</enviNFe>"
    Resposta $n507.Chave (Ret100 $n507.Chave $n507.Digest '131260000000507')

    # 508: a SEFAZ não conhece (217)
    $n508 = NFe (Chave $Cnpj '2609' 508 '10000508') 508 '2026-09-18T09:00:00-03:00' '8.00'
    Gravar "$dia\NOTA_508_SERIE_7_ASSINADA_18_09_2026_09_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n508.Xml)</enviNFe>"

    # 509 a 511: inutilização homologada (cStat 102), um arquivo só
    Gravar "$xmls\Inutilizadas\INUT_509_511.xml" ("<procInutNFe versao=`"4.00`" xmlns=`"$Ns`"><inutNFe versao=`"4.00`"><infInut Id=`"ID31261122233300018165007000000509000000511`"><tpAmb>1</tpAmb><xServ>INUTILIZAR</xServ><cUF>31</cUF><ano>26</ano><CNPJ>$Cnpj</CNPJ><mod>65</mod><serie>7</serie><nNFIni>509</nNFIni><nNFFin>511</nNFFin><xJust>numeracao pulada por falha</xJust></infInut></inutNFe>" +
        "<retInutNFe versao=`"4.00`"><infInut><tpAmb>1</tpAmb><verAplic>TESTE</verAplic><cStat>102</cStat><xMotivo>Inutilizacao de numero homologado</xMotivo><cUF>31</cUF><ano>26</ano><CNPJ>$Cnpj</CNPJ><mod>65</mod><serie>7</serie><nNFIni>509</nNFIni><nNFFin>511</nNFFin><dhRecbto>2026-09-30T10:00:00-03:00</dhRecbto><nProt>131260000000509</nProt></infInut></retInutNFe></procInutNFe>")
    # 512: inutilização só pedida (sem retorno 102) não comprova
    Gravar "$xmls\Inutilizadas\pedido_512.xml" "<inutNFe versao=`"4.00`" xmlns=`"$Ns`"><infInut><tpAmb>1</tpAmb><xServ>INUTILIZAR</xServ><cUF>31</cUF><ano>26</ano><CNPJ>$Cnpj</CNPJ><mod>65</mod><serie>7</serie><nNFIni>512</nNFIni><nNFFin>512</nNFFin><xJust>teste sem retorno</xJust></infInut></inutNFe>"

    # 513: o ASSINADA não tem o conteúdo autorizado (digVal diferente)
    $n513 = NFe (Chave $Cnpj '2609' 513 '10000513') 513 '2026-09-19T09:00:00-03:00' '13.00'
    Gravar "$dia\NOTA_513_SERIE_7_ASSINADA_19_09_2026_09_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n513.Xml)</enviNFe>"
    Resposta $n513.Chave (Ret100 $n513.Chave 'OutroConteudoAutorizado0000000=' '131260000000513')

    # 514: só existe nota de OUTRO emitente com a mesma série/número
    $n514 = NFe (Chave $Outro '2609' 514 '10000514') 514 '2026-09-20T09:00:00-03:00' '14.00' $Outro
    Gravar "$dia\NOTA_514_SERIE_7_ASSINADA_20_09_2026_09_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n514.Xml)</enviNFe>"

    # 515: denegada (cStat 302) -> pendente com a resposta da SEFAZ
    $n515 = NFe (Chave $Cnpj '2609' 515 '10000515') 515 '2026-09-21T09:00:00-03:00' '15.00'
    Gravar "$dia\NOTA_515_SERIE_7_ASSINADA_21_09_2026_09_00_00.xml" "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($n515.Xml)</enviNFe>"
    Resposta $n515.Chave "<cStat>302</cStat><xMotivo>Uso Denegado: Irregularidade fiscal do destinatario</xMotivo><cUF>31</cUF><dhRecbto>2026-10-22T12:00:00-03:00</dhRecbto><chNFe>$($n515.Chave)</chNFe>"

    $antes = HashArvore $xmls
    $Faixas = @('--faixas', '501 506 7, 507 a 507 7, 508 508 7 NFC-e, 509 512 7, 513 515 7')

    # ---------- Entrada ----------
    Write-Host '--- Entrada'
    $r = Rodar '' @('--faixas', '501 a 502')
    Verdade ($codigo -eq 2 -and $ultimaSaida.Contains('não reconhecida')) 'faixa sem série e recusada'
    $r = Rodar '' @('--faixas', '510 509 7')
    Verdade ($codigo -eq 2 -and $ultimaSaida.Contains('Final menor')) 'Final menor que Inicial e recusado'
    $r = Rodar 'LimiteNumerosFaltantes=10' @('--faixas', '1 50 7')
    Verdade ($codigo -eq 2 -and $ultimaSaida.Contains('passam do limite')) 'trava de quantidade de numeros'

    # ---------- Sem certificado: a busca local funciona, nada é copiado ----------
    Write-Host '--- Sem certificado (SEFAZ nao consultada)'
    $semCert = "CertificadoPfx=$tmp\semcert`r`nPastaEmpresas=$tmp\semcert`r`nUrlConsulta=http://127.0.0.1:1/x`r`nPastaNotasFaltantes=$tmp\NF_semcert"
    $r = Rodar $semCert ($Faixas + '--copiar')
    Verdade ($codigo -eq 0) 'roda sem certificado'
    Confere $r 501 'PENDENTE' '' 'SEFAZ não consultada'
    Verdade ($r['501'].Chave -match $n501.Chave -and $r['501'].Valor -eq '33,00') '501: chave e valor do candidato aparecem mesmo sem SEFAZ'
    Confere $r 509 'INUTILIZADA' 'INUT_509_511.xml' 'INUT_509_511.xml'
    Confere $r 512 'NÃO ENCONTRADA' '' 'sem retorno 102'
    Confere $r 514 'NÃO ENCONTRADA' '' 'outro emitente'
    Verdade (@(Get-ChildItem $ultimaPasta -Filter *.xml).Count -eq 1) 'sem SEFAZ so a inutilizacao (local) e copiada'

    # ---------- SEFAZ simulada ----------
    Write-Host '--- SEFAZ simulada'
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $porta = $l.LocalEndpoint.Port; $l.Stop()
    $chamadas = Join-Path $tmp 'chamadas.txt'; $pronto = Join-Path $tmp 'pronto'; $parar = Join-Path $tmp 'parar'
    $servidor = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $PSScriptRoot 'sefaz_respostas.ps1'), '-Porta', $porta, '-Respostas', $respostas,
        '-ArqChamadas', $chamadas, '-ArqPronto', $pronto, '-ArqParar', $parar)
    for ($i = 0; $i -lt 200 -and -not (Test-Path $pronto); $i++) { Start-Sleep -Milliseconds 50 }
    $senha = 'Senha#Teste123'
    NovoPfx "$tmp\emp\1\Certificado\cert.pfx" $Cnpj $senha
    $cfg = "UrlConsulta=http://127.0.0.1:$porta/nfce/services/NFeConsultaProtocolo4`r`nCertificadoPfx=$tmp\emp\1\Certificado`r`nPastaEmpresas=$tmp\emp`r`nCertificadoSenha=$senha"

    $r = Rodar $cfg $Faixas
    Confere $r 501 'ENCONTRADA' '' 'procNFe montado'
    Verdade ($r['501'].Copia -eq 'não copiado (só relatório)') 'sem --copiar nada e copiado'
    Verdade (-not $ultimaSaida.Contains($senha)) 'senha do certificado fora da tela e do log'
    $ch = @(Get-Content $chamadas)
    Verdade (($ch | Group-Object | Where-Object Count -gt 1).Count -eq 0) 'uma consulta por chave'
    Verdade ($ch -notcontains $n514.Chave) 'nota de outro emitente nao e consultada'

    $r = Rodar $cfg ($Faixas + '--copiar' + '--zip')
    $p = $ultimaPasta
    Verdade ((Split-Path $p -Leaf) -like "${Cnpj}_2026-09_*") 'pasta da entrega <CNPJ>_<AAAA-MM>_<data-hora>'
    Confere $r 501 'ENCONTRADA' "NOTA_501_SERIE_7_ASSINADA_12_09_2026_13_20_16.xml | $($n501.Chave)-procNFe.xml" 'digest = digVal'
    Verdade ((Hash "$p\NOTA_501_SERIE_7_ASSINADA_12_09_2026_13_20_16.xml") -eq (Hash "$dia\NOTA_501_SERIE_7_ASSINADA_12_09_2026_13_20_16.xml")) '501: copia identica ao original'
    $proc = [xml](Get-Content "$p\$($n501.Chave)-procNFe.xml" -Raw -Encoding UTF8)
    Verdade ($proc.nfeProc.protNFe.infProt.nProt -eq '131260000000501' -and $proc.nfeProc.NFe.infNFe.Id -eq "NFe$($n501.Chave)") '501: procNFe com a NFe e o protocolo da SEFAZ'
    Verdade ($ultimaSaida.Contains('REJEICAO') -and $ultimaSaida.Contains('descartado')) '501: REJEICAO e Temp registrados no log como descartados'
    Confere $r 502 'ENCONTRADA' 'NOTA_502_SERIE_7_NFCE.xml'
    Confere $r 503 'ENCONTRADA' "NOTA_503_SERIE_7_ASSINADA_14_09_2026_09_05_00.xml | $($n503b.Chave)-procNFe.xml"
    Verdade ($r['503'].Chave -match $n503b.Chave) '503: vale a chave autorizada (a outra e 217)'
    Confere $r 504 'CANCELADA' "NOTA_504_SERIE_7_ASSINADA_15_09_2026_09_00_00.xml | $($n504.Chave)-procNFe.xml | $($n504.Chave)-procEventoNFe-110111.xml" 'obtido da SEFAZ'
    $ev = [xml](Get-Content "$p\$($n504.Chave)-procEventoNFe-110111.xml" -Raw -Encoding UTF8)
    Verdade ($ev.procEventoNFe.retEvento.infEvento.nProt -eq '131260000009504' -and $ev.DocumentElement.NamespaceURI -eq $Ns) '504: evento da SEFAZ gravado com namespace e protocolo'
    Confere $r 505 'CANCELADA' "NOTA_505_SERIE_7_NFCE.xml | 110111$($n505.Chave)01-procEventoNFe.xml" 'do disco'
    Confere $r 506 'CANCELADA – SEM XML DO EVENTO' "NOTA_506_SERIE_7_ASSINADA_17_09_2026_09_00_00.xml | $($n506.Chave)-procNFe.xml"
    Confere $r 507 'MÊS DIFERENTE' '' 'emitida em 08/2026'
    Confere $r 508 'NÃO CONSTA NA SEFAZ' '' 'nunca transmitida'
    Confere $r 509 'INUTILIZADA' 'INUT_509_511.xml'
    Confere $r 510 'INUTILIZADA' 'INUT_509_511.xml'
    Confere $r 511 'INUTILIZADA' 'INUT_509_511.xml'
    Verdade (@(Get-ChildItem $p -Filter 'INUT_*').Count -eq 1) 'inutilizacao copiada uma vez so'
    Confere $r 512 'NÃO ENCONTRADA' '' 'inutilizado'
    Confere $r 513 'PENDENTE' '' 'nenhum candidato com digest igual'
    Confere $r 514 'NÃO ENCONTRADA' ''
    Confere $r 515 'PENDENTE' '' '302'
    Verdade (Test-Path "$p\relatorio_${Cnpj}_2026-09.csv") 'relatorio CSV na pasta da entrega'
    $zip = "$p.zip"
    Verdade ((Test-Path $zip) -and ([IO.Compression.ZipFile]::OpenRead($zip).Entries.Count -eq @(Get-ChildItem $p -File).Count)) 'zip com todos os arquivos da entrega'
    Verdade ((HashArvore $xmls) -eq $antes) 'XMLs originais intactos (somente leitura)'
    $hist = Import-Csv "$base\historico.csv" -Delimiter ';' -Encoding UTF8
    Verdade (@($hist | Where-Object Status -eq 'COPIADO').Count -eq 15) 'historico com uma linha por arquivo copiado'

    $r = Rodar $cfg ($Faixas + '--copiar')
    Verdade ($r['501'].Copia -eq 'JÁ COPIADO' -and @(Get-ChildItem $ultimaPasta -Filter *.xml).Count -eq 0) 'segunda busca: nada copiado de novo (historico)'
    $r = Rodar $cfg ($Faixas + '--copiar' + '--recopiar')
    Verdade ($r['501'].Copia -eq 'COPIADO' -and @(Get-ChildItem $ultimaPasta -Filter *.xml).Count -eq 13) '--recopiar copia de novo'
}
finally {
    if ($servidor) { New-Item -ItemType File -Path (Join-Path $tmp 'parar') -Force | Out-Null; $servidor.WaitForExit(5000) | Out-Null }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($falhas -eq 0) { Write-Host 'TODOS OS TESTES PASSARAM'; exit 0 } else { Write-Host "$falhas FALHA(S)"; exit 1 }
