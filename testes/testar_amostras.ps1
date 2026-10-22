# Valida o DuplicidadeNfce.exe com as amostras. Nenhum teste chama a SEFAZ real:
# usa --sem-sefaz ou a SEFAZ simulada local (testes\sefaz_simulada.ps1).
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File testes\testar_amostras.ps1
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$exe = if ($env:NOTACERTA_EXE) { $env:NOTACERTA_EXE } else { Join-Path $raiz 'dist\NotaCerta.exe' }
$amostras = Join-Path $raiz 'amostras'
$tmp = Join-Path $env:TEMP ('dup_teste_' + [guid]::NewGuid().ToString('N'))
$falhas = 0
$execucao = 0
$hoje = (Get-Date).ToString('yyyy-MM-dd')

# Chaves de acesso e CNPJ das amostras reais ficam em testes\casos.local.ps1, fora do git (dados do cliente).
# Ele define: $CnpjEmitente, $K88866a, $K88866b, $K88867, $K88867sem539, $K88868, $Aut88866, $Aut88867, $Aut88868.
$casos = Join-Path $PSScriptRoot 'casos.local.ps1'
if (-not (Test-Path $casos) -or -not (Test-Path $amostras)) {
    Write-Host "Faltam as amostras do cliente (amostras\ e testes\casos.local.ps1), que nao vao para o git."
    exit 1
}
. $casos
$Todas = @('--notas', '6/88866,6/88867,6/88868')

# $extra: linhas a mais no .ini; $opcoes: argumentos. Devolve as linhas do CSV.
function Rodar($pasta, $extra = '', $opcoes = (@('--sem-sefaz') + $Todas), $destino = (Join-Path $tmp 'destino')) {
    $script:execucao++
    $rel = Join-Path $tmp "rel_$execucao"
    $ini = Join-Path $tmp "cfg_$execucao.ini"
    "PastaRelatorios=$rel`r`nPastaDestino=$destino`r`n$extra" | Set-Content -Path $ini -Encoding UTF8
    $base = @('--config', $ini, '--pasta', $pasta)
    if ($opcoes -notcontains '--de') { $base += '--todas-datas' }
    $saida = & { $ErrorActionPreference = 'Continue'; & $exe @base @opcoes 2>&1 } | ForEach-Object { "$_" }
    $script:codigo = $LASTEXITCODE
    $script:ultimaSaida = ($saida -join "`n")
    if ($codigo -ne 0) { return @() }
    $script:ultimaSaida += "`n" + (Get-Content (Get-ChildItem $rel -Filter *.log | Select-Object -First 1).FullName -Raw)
    $script:ultimoCsv = (Get-ChildItem $rel -Filter *.csv | Select-Object -First 1).FullName
    Import-Csv $ultimoCsv -Delimiter ';' -Encoding UTF8 | ForEach-Object {
        $_.'Chave do sistema' = $_.'Chave do sistema' -replace '^="|"$', ''
        $_
    }
}

function Confere($linhas, $chave, $status, $arquivo, $valorSefaz, $motivoContem = '') {
    $l = $linhas | Where-Object { $_.'Chave do sistema' -eq $chave }
    $ok = $l -and $l.Status -eq $status -and $l.'Arquivo escolhido' -eq $arquivo -and $l.'Valor SEFAZ' -eq $valorSefaz -and $l.Motivo.Contains($motivoContem)
    if ($ok) { Write-Host "OK    $($chave.Substring(25,9)) -> $status $arquivo" }
    else {
        Write-Host "FALHA $chave esperado [$status|$arquivo|$valorSefaz|$motivoContem]"
        Write-Host "      obtido  [$($l.Status)|$($l.'Arquivo escolhido')|$($l.'Valor SEFAZ')|$($l.Motivo)]"
        $script:falhas++
    }
}

function Verdade($cond, $descricao) {
    if ($cond) { Write-Host "OK    $descricao" } else { Write-Host "FALHA $descricao"; $script:falhas++ }
}

function CopiaAmostras($nome) {
    $d = Join-Path $tmp $nome
    Copy-Item $amostras $d -Recurse
    $d
}

function Hash($f) { (Get-FileHash $f -Algorithm SHA256).Hash }
function Destino($linhas, $chave) { ($linhas | Where-Object { $_.'Chave do sistema' -eq $chave }).'Caminho destino' }

# Certificado autoassinado em memória, exportado para .pfx (não toca no repositório do Windows).
function NovoPfx($arquivo, $cnpj, $senha) {
    New-Item -ItemType Directory -Force -Path (Split-Path $arquivo) | Out-Null
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $req = New-Object Security.Cryptography.X509Certificates.CertificateRequest("CN=EMPRESA TESTE:$cnpj", $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $c = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(1))
    [IO.File]::WriteAllBytes($arquivo, $c.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $senha))
}

New-Item -ItemType Directory -Path $tmp | Out-Null
$servidor = $null
try {
    Write-Host '--- Sem lista de notas: nao roda'
    $r = Rodar $amostras '' @('--sem-sefaz')
    Verdade ($codigo -eq 1 -and $ultimaSaida.Contains('Informe as notas')) 'sem --notas mostra o uso e sai com codigo 1'
    $r = Rodar $amostras '' @('--sem-sefaz', '--notas', '88867')
    Verdade ($codigo -eq 2 -and $ultimaSaida.Contains('nota inválida')) 'nota sem serie e rejeitada'

    Write-Host '--- Amostras originais (--sem-sefaz)'
    $r = Rodar $amostras
    Confere $r $K88867 'A COPIAR' 'NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '10,00'
    Confere $r $K88868 'A COPIAR' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00'
    Confere $r $K88866a 'A COPIAR' 'NOTA_88866_SERIE_6_ASSINADA_19_10_2026_17_05_40.xml' '10,00'
    Confere $r $K88866b 'A COPIAR' 'NOTA_88866_SERIE_6_ASSINADA_19_10_2026_17_05_40.xml' '10,00'
    Confere $r $K88867sem539 'PENDENTE' '' '' 'sem retorno 539'
    Verdade (-not ($r | Where-Object { $_.'Arquivo escolhido' -like '*_NFCE*' })) 'nenhum NFCE escolhido (so ASSINADA)'

    Write-Host '--- 88869: vale o ASSINADA da chave autorizada com o valor da SEFAZ'
    $r = Rodar $amostras '' @('--sem-sefaz', '--notas', '6/88869')
    Confere $r $K88869 'A COPIAR' 'NOTA_88869_SERIE_6_ASSINADA_19_10_2026_17_12_40.xml' '14,00' 'ASSINADA da chave autorizada'
    Confere $r $K88869sem539 'PENDENTE' '' '' 'sem retorno 539'

    Write-Host '--- Contingencia sem arquivo 539 (casos do zip de outubro)'
    $r = Rodar $amostras '' @('--sem-sefaz', '--notas', '6/89244,6/89098,6/89090,6/89257,6/84643,6/85710')
    # 89244: autorizada a de 08:23 (R$ 18,90); o ASSINADA dela difere do autorizado, vale o NFCE
    Confere $r $K89244b 'PENDENTE' '' '' 'sem retorno 539: duplicidade provável'
    Confere $r $K89244c 'PENDENTE' '' '' 'sem retorno 539'
    Confere $r $K89244d 'PENDENTE' '' '' 'sem retorno 539'
    $l = $r | Where-Object { $_.'Chave do sistema' -eq $K89244b }
    Verdade ($l.Classificacao -eq 'Duplicidade (deduzida pelos arquivos)') '89244 classificada como duplicidade deduzida (sem REJEICAO)'
    # 89098: o ASSINADA da autorizada (15:57) e igual ao autorizado, mas so se copia o NFCE autorizado
    Confere $r $K89098b 'PENDENTE' '' '' 'sem retorno 539'
    Confere $r $K89090b 'PENDENTE' '' '' 'sem retorno 539'
    # 89257: numero nunca autorizado -> em contingencia sem retorno, nada a copiar
    Confere $r $K89257b 'EM CONTINGÊNCIA' '' '' 'sem autorização'
    Verdade (@($r | Where-Object { $_.Numero -eq '89257' -and $_.Status -eq 'EM CONTINGÊNCIA' }).Count -eq 3) '89257: as 3 tentativas ficam em contingencia'
    # 84643: numero usado 14 vezes em 05/10, ja resolvido no Metre (nao tem ONTIGENCIA_ENVIA)
    $l = @($r | Where-Object { $_.Numero -eq '84643' })
    Verdade ($l.Count -eq 1 -and $l[0].Status -eq 'JÁ AUTORIZADA') '84643: sem ONTIGENCIA_ENVIA (fora da contingencia do Metre) nao entra na lista'
    # 85710: rejeitada com 537 e depois autorizada (cStat 150) com a mesma chave
    $l = @($r | Where-Object { $_.Numero -eq '85710' })
    Verdade ($l.Count -eq 1 -and $l[0].Status -eq 'JÁ AUTORIZADA' -and $l[0].Motivo.Contains('cStat 150')) '85710: rejeicao 537 depois autorizada -> JA AUTORIZADA'
    $r = Rodar $amostras '' @('--sem-sefaz', '--notas', '6/88867')
    $l = $r | Where-Object { $_.'Chave do sistema' -eq $K88867 }
    Verdade ($l.Classificacao -eq 'Duplicidade (retorno 539)') '88867 17:20 continua classificada pelo retorno 539'

    Write-Host '--- Lista da contingencia vinda do Metre (CSV exportado do HeidiSQL)'
    $csvMetre = Join-Path $tmp 'contingencia_metre.csv'
    $cab = '"id_nota_saida";"numero";"serie";"num_pedido";"chave_acesso";"valor_total_nota";"dt_emissao";"data_contigencia";"dt_tentativa_emissao";"motivo_nao_transmissao";"notas_importadas"'
    @($cab,
      "`"1`";`"89244`";`"6`";`"258958`";`"$K89244b`";`"13,95`";`"2026-10-21`";`"2026-10-21 08:45:08`";`"2026-10-22 16:23:13`";`"Lote processado`";\N",
      "`"2`";`"89257`";`"6`";`"258976`";`"$K89257b`";`"7,00`";`"2026-10-21`";`"2026-10-21 08:58:36`";`"2026-10-22 16:23:11`";`"Lote processado`";\N",
      "`"3`";`"89244`";`"6`";\N;`"$Aut89244`";`"18,90`";`"2026-10-21`";`"2026-10-21 08:23:20`";`"2026-10-22 15:51:37`";\N;\N",
      "`"4`";`"88867`";`"6`";`"258155`";`"$K88867sem539`";`"15,50`";`"2026-10-19`";`"2026-10-19 17:56:51`";`"2026-10-22 16:22:13`";`"Lote processado`";\N"
    ) | Set-Content $csvMetre -Encoding UTF8
    $r = Rodar $amostras "FonteContingencia=csv:$csvMetre" @('--sem-sefaz', '--notas', '6/89244,6/89257,6/88867')
    Verdade ($r.Count -eq 4) 'so as 4 notas da contingencia do Metre entram (as outras tentativas dos XMLs ficam de fora)'
    Confere $r $K89244b 'A COPIAR' 'NOTA_89244_SERIE_6_ASSINADA_21_10_2026_08_23_20.xml' '18,90' 'sistema 13,95'
    Verdade (($r | Where-Object { $_.'Chave do sistema' -eq $K89244b }).Pedido -eq '258958') 'pedido vem do Metre'
    Confere $r $K89257b 'PENDENTE' '' '' 'número sem XML autorizado'
    Confere $r $Aut89244 'JÁ AUTORIZADA' '' '' 'criada ao importar'
    Confere $r $K88867sem539 'A COPIAR' 'NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '10,00' 'sistema 15,50'

    # Historico antigo (ex.: o _NFCE copiado por engano) nao bloqueia nota que continua na contingencia do Metre
    $destH = Join-Path $tmp 'destino_hist'
    New-Item -ItemType Directory -Path $destH | Out-Null
    @('Data/hora;Serie;Numero;Chave autorizada;Arquivo;Caminho origem;Caminho destino;Tamanho;SHA256;Status',
      "22/10/2026 15:49:00;6;89244;=`"`"$Aut89244`"`";NOTA_89244_SERIE_6_NFCE.xml;x;y;1;z;COPIADO") | Set-Content (Join-Path $destH 'historico.csv') -Encoding UTF8
    $r = Rodar $amostras "FonteContingencia=csv:$csvMetre" @('--sem-sefaz', '--notas', '6/89244') $destH
    Confere $r $K89244b 'A COPIAR' 'NOTA_89244_SERIE_6_ASSINADA_21_10_2026_08_23_20.xml' '18,90' 'sistema 13,95'

    Write-Host '--- Temp_ANTES_ENVIO envolto em enviNFe e lido sem erro'
    $d = CopiaAmostras 'envinfe'
    $nfe = [regex]::Match([IO.File]::ReadAllText((Join-Path $d 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_22_32.xml')), '<NFe .*</NFe>', 'Singleline').Value
    [IO.File]::WriteAllText((Join-Path $d 'NOTA_88868_SERIE_6_CHAVE_X_Temp_ANTES_ENVIO_19_10_2026_17_22_33.xml'),
        '<?xml version="1.0" encoding="UTF-8"?><enviNFe versao="4.00" xmlns="http://www.portalfiscal.inf.br/nfe"><idLote>1</idLote><indSinc>0</indSinc>' + $nfe + '</enviNFe>')
    $r = Rodar $d '' @('--sem-sefaz', '--notas', '6/88868')
    Verdade (-not $ultimaSaida.Contains('Temp_ANTES_ENVIO_19_10_2026_17_22_33.xml -')) 'enviNFe lido sem erro de leitura'
    Confere $r $K88868 'A COPIAR' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00'

    Write-Host '--- Lista restringe as notas'
    $r = Rodar $amostras '' @('--sem-sefaz', '--notas', '6/88867')
    Verdade (-not ($r | Where-Object { $_.Numero -ne '88867' })) 'so a 6/88867 aparece no relatorio'
    $lista = Join-Path $tmp 'notas.txt'
    "# notas do dia 19`r`n6/88868`r`n`r`n6/99999" | Set-Content $lista -Encoding UTF8
    $r = Rodar $amostras '' @('--sem-sefaz', '--notas-arquivo', $lista)
    Confere $r $K88868 'A COPIAR' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00'
    $l = $r | Where-Object { $_.Numero -eq '99999' }
    Verdade ($l -and $l.Status -eq 'PENDENTE' -and $l.Motivo.StartsWith('nenhuma tentativa')) 'nota inexistente sai PENDENTE "nenhuma tentativa"'
    Verdade (-not ($r | Where-Object { $_.Numero -eq '88867' })) 'notas fora do arquivo ignoradas'

    Write-Host '--- XMLs soltos na raiz, filtrados pela data de emissao'
    $d = CopiaAmostras 'raizsolta'
    New-Item -ItemType Directory -Path (Join-Path $d '09_2026\02') | Out-Null
    $r = Rodar $d '' @('--sem-sefaz', '--notas', '6/88867,6/88868', '--de', '19/10/2026', '--ate', '19/10/2026')
    Confere $r $K88867 'A COPIAR' 'NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '10,00'
    Confere $r $K88868 'A COPIAR' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00'
    Verdade ($ultimaSaida.Contains('solto(s) na raiz')) 'log informa os XMLs soltos lidos'
    $r = Rodar $d '' @('--sem-sefaz', '--notas', '6/88867', '--de', '20/10/2026', '--ate', '22/10/2026')
    $l = @($r | Where-Object { $_.Numero -eq '88867' })
    Verdade ($l.Count -eq 1 -and $l[0].Motivo.StartsWith('nenhuma tentativa')) 'fora do periodo de emissao (20 a 22/10) a nota nao entra'
    $r = Rodar $d '' @('--sem-sefaz', '--notas', '6/88867', '--de', '17/10/2026', '--ate', '18/10/2026')
    Verdade (@($r | Where-Object { $_.Status -eq 'A COPIAR' }).Count -eq 0) 'periodo anterior (17 a 18/10) tambem nao pega a nota do dia 19'

    Write-Host '--- Adulteracao: vNF da 88868 alterado mantendo o DigestValue'
    $d = CopiaAmostras 'adulterada'
    $f = Join-Path $d 'NOTA_88868_SERIE_6_NFCE.xml'
    [IO.File]::WriteAllText($f, [IO.File]::ReadAllText($f).Replace('<vNF>5.00</vNF>', '<vNF>7.00</vNF>'))
    Confere (Rodar $d) $K88868 'PENDENTE' '' '' 'nenhum dos'

    Write-Host '--- Valor do sistema igual ao da SEFAZ: ganha a sua copia como as outras linhas'
    $d = CopiaAmostras 'mesmovalor'
    # Todos os arquivos da tentativa (chave ...9846322375) passam a ter 5,00, senão a divergência gera PENDENTE.
    foreach ($n in 'NOTA_88868_SERIE_6_REJEICAO_NOTA.xml', 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_22_32.xml', 'NOTA_ONTIGENCIA_ENVIA_88868_SERIE_6_Temp.xml') {
        $f = Join-Path $d $n
        [IO.File]::WriteAllText($f, [IO.File]::ReadAllText($f).Replace('<vNF>70.00</vNF>', '<vNF>5.00</vNF>'))
    }
    Confere (Rodar $d) $K88868 'A COPIAR' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00' 'mesmo valor'

    Write-Host '--- Sem .pfx: SEFAZ nao consultada'
    $emp = Join-Path $tmp 'Empresa'
    $r = Rodar $amostras "CertificadoPfx=$emp\1\Certificado`r`nPastaEmpresas=$emp`r`nCertificadoSenha=x" $Todas
    Confere $r $K88868 'PENDENTE' '(indicado) NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '' 'SEFAZ não consultada: nenhum .pfx'
    Verdade (($r | Where-Object { $_.'Situacao SEFAZ' -ne 'NÃO CONSULTADA' -and $_.'Situacao SEFAZ' -ne '' }).Count -eq 0) 'situacao NAO CONSULTADA em todas as linhas'

    Write-Host '--- .pfx sem senha em lugar nenhum'
    NovoPfx "$emp\1\Certificado\cert.pfx" $CnpjEmitente 'SenhaCerta#456'
    $r = Rodar $amostras "CertificadoPfx=$emp\1\Certificado`r`nPastaEmpresas=$emp`r`nIniMetre=$tmp\naoexiste.ini" $Todas
    Confere $r $K88867 'PENDENTE' '(indicado) NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '' 'senha do certificado não encontrada'

    Write-Host '--- .pfx com senha errada (e senha fora do log)'
    $senhaFalsa = 'SenhaErrada#123'
    $r = Rodar $amostras "CertificadoPfx=$emp\1\Certificado`r`nPastaEmpresas=$emp`r`nCertificadoSenha=$senhaFalsa" $Todas
    Confere $r $K88867 'PENDENTE' '(indicado) NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '' 'senha incorreta'
    Verdade (-not $ultimaSaida.Contains($senhaFalsa)) 'senha nao aparece na tela nem no log'
    Verdade (-not (Get-Content $ultimoCsv -Raw).Contains($senhaFalsa)) 'senha nao aparece no relatorio'

    Write-Host '--- .pfx de outro CNPJ em outra empresa'
    NovoPfx "$emp\2\Certificado\outro.pfx" '11222333000181' 'SenhaCerta#456'
    Remove-Item "$emp\1" -Recurse -Force
    $r = Rodar $amostras "CertificadoPfx=$emp\1\Certificado`r`nPastaEmpresas=$emp`r`nCertificadoSenha=SenhaCerta#456" $Todas
    Confere $r $K88867 'PENDENTE' '(indicado) NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '' "nenhum certificado válido do CNPJ $CnpjEmitente"
    Verdade ($ultimaSaida.Contains('é de outro CNPJ')) 'log registra certificado de outro CNPJ'

    Write-Host '--- Pasta de destino dentro da raiz fica fora da varredura'
    $d = CopiaAmostras 'destinodentro'
    $dest = Join-Path $d 'Duplicidade'
    New-Item -ItemType Directory -Path $dest | Out-Null
    Copy-Item (Join-Path $d 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml') (Join-Path $dest 'NOTA_88868_SERIE_6_NFCE_falsa.xml')
    $r = Rodar $d '' (@('--sem-sefaz') + $Todas) $dest
    Verdade (-not $ultimaSaida.Contains('NFCE_falsa')) 'arquivo da pasta de destino ignorado'
    $r = Rodar $d '' @('--sem-sefaz', '--notas', '6/88868', '--copiar')
    Verdade ($codigo -eq 2 -and $ultimaSaida.Contains('não pode ser usado com --sem-sefaz')) '--copiar com --sem-sefaz e recusado'

    Write-Host '--- SEFAZ simulada local: relatorio, copia e historico'
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $porta = $l.LocalEndpoint.Port; $l.Stop()
    $chamadas = Join-Path $tmp 'chamadas.txt'; $pronto = Join-Path $tmp 'pronto'; $parar = Join-Path $tmp 'parar'
    # A SEFAZ simulada responde com os protNFe das amostras e, como no servidor, também autoriza a 6/89257
    # das 08:42, cujo _NFCE o Metre nunca gravou (digVal = DigestValue do ASSINADA dessa tentativa).
    $dadosSefaz = Join-Path $tmp 'dados_sefaz'
    Copy-Item $amostras $dadosSefaz -Recurse
    "<protNFe versao=`"4.00`"><infProt><tpAmb>1</tpAmb><verAplic>TESTE</verAplic><chNFe>$K89257a</chNFe><dhRecbto>2026-10-21T08:45:00-03:00</dhRecbto><nProt>131262699999999</nProt><digVal>$DigVal89257a</digVal><cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo></infProt></protNFe>" |
        Set-Content (Join-Path $dadosSefaz 'protocolo_89257.xml') -Encoding UTF8
    $servidor = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $PSScriptRoot 'sefaz_simulada.ps1'), '-Porta', $porta, '-Amostras', $dadosSefaz,
        '-ArqChamadas', $chamadas, '-ArqPronto', $pronto, '-ArqParar', $parar)
    for ($i = 0; $i -lt 200 -and -not (Test-Path $pronto); $i++) { Start-Sleep -Milliseconds 50 }
    # Senha e caminho do .pfx lidos de um NFCe.ini no formato de INI comum (nomes de chave provisórios).
    $senhaIni = 'SenhaDoIni#789'
    NovoPfx "$emp\3\Certificado\cert.pfx" $CnpjEmitente $senhaIni
    $iniEmissor = Join-Path $tmp 'NFCe.ini'
    "[CERTIFICADO]`r`nCAMINHO=$emp\3\Certificado\cert.pfx`r`nSENHA=$senhaIni" | Set-Content $iniEmissor -Encoding Default
    $cfgSefaz = "UrlConsulta=http://127.0.0.1:$porta/nfce/services/NFeConsultaProtocolo4`r`nCertificadoPfx=$emp\9\Certificado`r`nPastaEmpresas=$tmp\nada`r`nIniMetre=$iniEmissor;$tmp\Config.ini"
    $destC = Join-Path $tmp 'destino_copia'
    $Lista = @('--notas', '6/88867,6/88868')

    $r = Rodar $amostras $cfgSefaz $Lista $destC
    Confere $r $K88867 'A COPIAR' 'NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '10,00'
    Confere $r $K88868 'A COPIAR' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00'
    $ch = @(Get-Content $chamadas)
    Verdade ($ch.Count -eq 2 -and $ch -contains $Aut88867 -and $ch -contains $Aut88868 -and $ch -notcontains $Aut88866) 'SEFAZ consultada so para as notas da lista (sem 88866)'
    Verdade (-not (Test-Path $destC) -or -not (Get-ChildItem $destC -Filter *.xml -Recurse)) 'sem --copiar nada e copiado'
    Verdade ($ultimaSaida.Contains('aberto com a senha de ' + $iniEmissor + ' [CERTIFICADO] SENHA')) 'senha lida do NFCe.ini (log mostra so a origem)'
    Verdade (-not $ultimaSaida.Contains($senhaIni) -and -not (Get-Content $ultimoCsv -Raw).Contains($senhaIni)) 'senha do NFCe.ini fora da tela, do log e do relatorio'

    $r = Rodar $amostras $cfgSefaz ($Lista + '--copiar') $destC
    Confere $r $K88867 'COPIADO' 'NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '10,00' 'cópia conferida'
    Confere $r $K88868 'COPIADO' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00' 'cópia conferida'
    Confere $r $K88867sem539 'PENDENTE' '' '' 'sem retorno 539'
    Verdade ((Hash (Destino $r $K88867)) -eq (Hash (Join-Path $amostras 'NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml'))) 'copia da 88867 identica ao original'
    Verdade ((Hash (Destino $r $K88868)) -eq (Hash (Join-Path $amostras 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml'))) 'copia da 88868 identica ao original'
    Verdade (Test-Path (Join-Path $amostras 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml')) 'original continua no lugar (copia, nao move)'
    $hist = Import-Csv (Join-Path $destC 'historico.csv') -Delimiter ';' -Encoding UTF8
    Verdade (@($hist | Where-Object { $_.Status -eq 'COPIADO' }).Count -eq 2) 'historico com 2 registros COPIADO'
    $copiaDemo = $ultimoCsv
    Copy-Item $copiaDemo (Join-Path $tmp 'demo_copiar.csv')

    $antes = @(Get-Content $chamadas).Count
    $r = Rodar $amostras $cfgSefaz ($Lista + '--copiar') $destC
    Confere $r $K88867 'JÁ PROCESSADA' 'NOTA_88867_SERIE_6_ASSINADA_19_10_2026_17_06_37.xml' '' 'copiado em'
    Confere $r $K88868 'JÁ PROCESSADA' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '' 'copiado em'
    Confere $r $K88867sem539 'PENDENTE' '' '' 'sem retorno 539'
    Verdade (@(Get-Content $chamadas).Count -eq $antes) 'ja processadas nao consultam a SEFAZ de novo'
    Verdade (@(Import-Csv (Join-Path $destC 'historico.csv') -Delimiter ';').Count -eq 2) 'historico nao ganhou linhas novas'

    $r = Rodar $amostras $cfgSefaz @('--notas', '6/88869', '--copiar') $destC
    Confere $r $K88869 'COPIADO' 'NOTA_88869_SERIE_6_ASSINADA_19_10_2026_17_12_40.xml' '14,00' 'cópia conferida'
    Verdade ((Hash (Destino $r $K88869)) -eq (Hash (Join-Path $amostras 'NOTA_88869_SERIE_6_ASSINADA_19_10_2026_17_12_40.xml'))) 'copia da 88869 e o ASSINADA da chave autorizada'

    Write-Host '--- Mesmo numero 3 vezes na contingencia: uma copia por rodada (importar-1, -2, -3)'
    $csv3 = Join-Path $tmp 'contingencia_89244.csv'
    @($cab,
      "`"11`";`"89244`";`"6`";`"258958`";`"$K89244b`";`"13,95`";`"2026-10-21`";`"2026-10-21 08:45:08`";\N;\N;\N",
      "`"12`";`"89244`";`"6`";`"258986`";`"$K89244c`";`"11,25`";`"2026-10-21`";`"2026-10-21 09:08:05`";\N;\N;\N",
      "`"13`";`"89244`";`"6`";`"259000`";`"$K89244d`";`"46,35`";`"2026-10-21`";`"2026-10-21 09:27:51`";\N;\N;\N"
    ) | Set-Content $csv3 -Encoding UTF8
    $destR = Join-Path $tmp 'destino_rodadas'
    $r = Rodar $amostras "$cfgSefaz`r`nFonteContingencia=csv:$csv3" @('--notas', '6/89244', '--copiar') $destR
    $assinada = 'NOTA_89244_SERIE_6_ASSINADA_21_10_2026_08_23_20.xml'
    $ok = $true
    $i = 1
    foreach ($k in @($K89244b, $K89244c, $K89244d)) {
        $l = $r | Where-Object { $_.'Chave do sistema' -eq $k }
        if (-not ($l.Status -eq 'COPIADO' -and $l.'Caminho destino' -like "*\importar-$i\$assinada" -and (Hash $l.'Caminho destino') -eq (Hash (Join-Path $amostras $assinada)))) { $ok = $false }
        $i++
    }
    Verdade $ok '89244: 08:45 em importar-1, 09:08 em importar-2, 09:27 em importar-3, todas com o ASSINADA autorizado'

    Write-Host '--- 89257: autorizada na SEFAZ sem _NFCE no computador (confirmada pela consulta)'
    $csv57 = Join-Path $tmp 'contingencia_89257.csv'
    @($cab,
      "`"21`";`"89257`";`"6`";`"258976`";`"$K89257b`";`"7,00`";`"2026-10-21`";`"2026-10-21 08:58:36`";\N;\N;\N",
      "`"22`";`"89257`";`"6`";`"259021`";`"$K89257c`";`"31,00`";`"2026-10-21`";`"2026-10-21 09:58:05`";\N;\N;\N"
    ) | Set-Content $csv57 -Encoding UTF8
    $r = Rodar $amostras "$cfgSefaz`r`nFonteContingencia=csv:$csv57" @('--notas', '6/89257', '--copiar') (Join-Path $tmp 'destino_89257')
    $a57 = 'NOTA_89257_SERIE_6_ASSINADA_21_10_2026_08_42_18.xml'
    Confere $r $K89257b 'COPIADO' $a57 '6,75' 'cópia conferida'
    Confere $r $K89257c 'COPIADO' $a57 '6,75' 'cópia conferida'
    Verdade ((Destino $r $K89257b) -like "*\importar-1\$a57" -and (Destino $r $K89257c) -like "*\importar-2\$a57") '89257: 08:58 em importar-1 e 09:58 em importar-2'
    Verdade (($r | Where-Object { $_.'Chave do sistema' -eq $K89257b }).Classificacao -like '*confirmada na SEFAZ*') '89257 classificada como autorizada sem XML, confirmada na SEFAZ'

    Write-Host '--- Arquivo antigo na pasta do dia: nao e tocado (a copia vai na pasta da execucao)'
    $destX = Join-Path $tmp 'destino_existente'
    New-Item -ItemType Directory -Path (Join-Path $destX $hoje) | Out-Null
    $alvo = Join-Path $destX "$hoje\NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml"
    Set-Content $alvo 'conteudo antigo'
    $r = Rodar $amostras $cfgSefaz @('--notas', '6/88868', '--copiar') $destX
    Confere $r $K88868 'COPIADO' 'NOTA_88868_SERIE_6_ASSINADA_19_10_2026_17_07_37.xml' '5,00' 'cópia conferida'
    Verdade ((Destino $r $K88868) -like (Join-Path $destX "$hoje\*\importar-1\*")) 'copia vai na pasta da execucao (horario\importar-1), nao em cima do arquivo antigo'
    Verdade ((Get-Content $alvo -Raw).Trim() -eq 'conteudo antigo') 'arquivo existente nao foi alterado'

    New-Item -ItemType File -Path $parar | Out-Null
    $servidor.WaitForExit(5000) | Out-Null
    $erros = Get-Content $chamadas | Where-Object { $_ -like 'ERRO*' }
    Verdade (-not $erros) 'SEFAZ simulada sem erros'
    if ($env:DUP_DEMO) { Copy-Item (Join-Path $tmp 'demo_copiar.csv') $env:DUP_DEMO }
}
finally {
    if ($servidor -and -not $servidor.HasExited) { $servidor.Kill() }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($falhas) { Write-Host "$falhas falha(s)"; exit 1 } else { Write-Host 'Todos os testes passaram' }
