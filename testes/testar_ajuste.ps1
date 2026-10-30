# Testes do "Ajustar chave no banco" num MariaDB DE TESTE criado aqui (pasta temporária, porta livre, só 127.0.0.1),
# com a SEFAZ simulada (testes\sefaz_respostas.ps1). Nunca toca o banco do Metre: o servidor de teste é outro processo,
# com outra pasta de dados, e é desligado no fim.
# Por padrão usa dados FICTÍCIOS. Para repetir um caso real, passe os parâmetros na linha de comando (não grave aqui).
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File testes\testar_ajuste.ps1
param(
    [string]$Cnpj = '11222333000181',     # fictício
    [string]$Aamm = '2610',
    [int]$Serie = 1,
    [long]$Numero = 2001,
    [string]$CnfSistema = '11111111',
    [string]$CnfAutorizada = '22222222',
    [string]$Valor = '37.50',
    [long]$IdMetre = 900001,
    [long]$Pedido = 1001,
    [long]$IdXml = 800001,
    [string]$NProt = '131260000000001',
    [string]$DigVal = 'AAAAAAAAAAAAAAAAAAAAAAAAAAA=',
    [string]$CStatProt = '150',
    [string]$NRec = '310000000000001',
    [string]$PastaMariaDb = 'C:\Chef\Banco de Dados',
    # Deixa o banco e a SEFAZ de teste no ar no fim (para abrir a interface com --config <pasta>\teste.ini).
    [switch]$Manter
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$tmp = Join-Path $env:TEMP ('ajuste_teste_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$Ns = 'http://www.portalfiscal.inf.br/nfe'
$bin = Join-Path $PastaMariaDb 'bin'

function Chave($numero, $cnf) {
    $b = '31' + $Aamm + $Cnpj + '65' + $Serie.ToString('000') + ([long]$numero).ToString('000000000') + '9' + $cnf
    $peso = 2; $soma = 0
    for ($i = $b.Length - 1; $i -ge 0; $i--) { $soma += [int][string]$b[$i] * $peso; $peso = if ($peso -eq 9) { 2 } else { $peso + 1 } }
    $dv = 11 - ($soma % 11); if ($dv -ge 10) { $dv = 0 }
    $b + $dv
}

# NFe "assinada" (DigestValue calculado como na NF-e; a SignatureValue é fictícia, o NotaCerta não a usa).
function NFe($chave, $numero, $vNF, $dhEmi = '2026-10-19T13:47:50-03:00', $pedido = $null) {
    $det = if ($pedido) { "<det nItem=`"1`"><prod><xPed>$pedido</xPed></prod></det>" } else { '' }
    $inf = "<infNFe versao=`"4.00`" Id=`"NFe$chave`"><ide><cUF>31</cUF><cNF>$($chave.Substring(35,8))</cNF><natOp>VENDA</natOp><mod>65</mod><serie>$Serie</serie><nNF>$numero</nNF><dhEmi>$dhEmi</dhEmi><tpNF>1</tpNF><tpEmis>9</tpEmis><cDV>$($chave.Substring(43,1))</cDV><tpAmb>1</tpAmb></ide><emit><CNPJ>$Cnpj</CNPJ><xNome>EMPRESA TESTE</xNome></emit>$det<total><ICMSTot><vNF>$vNF</vNF></ICMSTot></total></infNFe>"
    $d2 = New-Object Xml.XmlDocument; $d2.PreserveWhitespace = $true; $d2.LoadXml("<NFe xmlns=`"$Ns`">$inf</NFe>")
    $d3 = New-Object Xml.XmlDocument; $d3.PreserveWhitespace = $true; $d3.LoadXml($d2.DocumentElement.FirstChild.OuterXml)
    $t = New-Object Security.Cryptography.Xml.XmlDsigC14NTransform; $t.LoadInput($d3)
    $dig = [Convert]::ToBase64String([Security.Cryptography.SHA1]::Create().ComputeHash($t.GetOutput([IO.Stream])))
    @{ Xml = "<NFe xmlns=`"$Ns`">$inf<Signature xmlns=`"http://www.w3.org/2000/09/xmldsig#`"><SignedInfo><Reference URI=`"#NFe$chave`"><DigestValue>$dig</DigestValue></Reference></SignedInfo><SignatureValue>AAAA</SignatureValue></Signature></NFe>"; Digest = $dig }
}

function Gravar($caminho, $texto) {
    New-Item -ItemType Directory -Force -Path (Split-Path $caminho) | Out-Null
    [IO.File]::WriteAllText($caminho, '<?xml version="1.0" encoding="UTF-8"?>' + $texto, $utf8)
}

# Tentativa do sistema rejeitada com 539 apontando a chave autorizada (gravada na pasta do dia $dia de 10/2026).
function Rejeicao539($numero, $chave, $autorizada, $vNF, $dia = '19') {
    $n = NFe $chave $numero $vNF
    $p = "<protNFe versao=`"4.00`"><infProt><tpAmb>1</tpAmb><verAplic>TESTE</verAplic><chNFe>$chave</chNFe><dhRecbto>2026-10-21T17:40:00-03:00</dhRecbto><cStat>539</cStat><xMotivo>Rejeicao: Duplicidade de NF-e com diferenca na Chave de Acesso [chNFe:$autorizada][nRec:$NRec]</xMotivo></infProt></protNFe>"
    Gravar (Join-Path $xmls "10_2026\$dia\NOTA_${numero}_SERIE_${Serie}_REJEICAO_NOTA.xml") "<nfeProc versao=`"4.00`" xmlns=`"$Ns`">$($n.Xml)$p</nfeProc>"
}

# $extra vai depois do xMotivo do protocolo (ex.: cMsg/xMsg do "autorizado com alerta").
function Resposta($chave, $digVal, $nProt, $cStatProt, $xMotivoProt = 'Autorizado o uso da NF-e', $extra = '', $dhRecbto = '2026-10-21T17:42:05-03:00') {
    $x = "<cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo><cUF>31</cUF><dhRecbto>2026-10-26T15:34:30-03:00</dhRecbto><chNFe>$chave</chNFe>" +
        "<protNFe versao=`"4.00`"><infProt Id=`"ID$nProt`"><tpAmb>1</tpAmb><verAplic>TESTE</verAplic><chNFe>$chave</chNFe><dhRecbto>$dhRecbto</dhRecbto><nProt>$nProt</nProt><digVal>$digVal</digVal><cStat>$cStatProt</cStat><xMotivo>$xMotivoProt</xMotivo>$extra</infProt></protNFe>"
    [IO.File]::WriteAllText((Join-Path $respostas "$chave.xml"), $x, $utf8)
}

function SqlN($v) { if ($null -eq $v) { 'NULL' } else { "$v" } }

function Hex($s) { -join ($utf8.GetBytes($s) | ForEach-Object { $_.ToString('X2') }) }

function NovoPfx($arquivo, $senha) {
    New-Item -ItemType Directory -Force -Path (Split-Path $arquivo) | Out-Null
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $req = New-Object Security.Cryptography.X509Certificates.CertificateRequest("CN=EMPRESA TESTE:$Cnpj", $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $c = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(1))
    [IO.File]::WriteAllBytes($arquivo, $c.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $senha))
}

function Senha() { -join ((48..57 + 65..90 + 97..122) | Get-Random -Count 20 | ForEach-Object { [char]$_ }) }

$mysqld = $null; $servidor = $null
$porta = 0; $senhaRoot = Senha; $senhaMetre = Senha
try {
    Add-Type -AssemblyName System.Security
    $xmls = Join-Path $tmp 'xmls'; $respostas = Join-Path $tmp 'respostas'
    New-Item -ItemType Directory -Force $xmls, $respostas | Out-Null

    # ---------- MariaDB de teste ----------
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $porta = $l.LocalEndpoint.Port; $l.Stop()
    $dados = Join-Path $tmp 'mariadb'
    & { $ErrorActionPreference = 'Continue'; & "$bin\mysql_install_db.exe" "--datadir=$dados" "--port=$porta" "--password=$senhaRoot" 2>&1 | Out-Null }
    if (-not (Test-Path "$dados\my.ini")) { throw "mysql_install_db falhou" }
    $mysqld = Start-Process "$bin\mysqld.exe" -PassThru -WindowStyle Hidden -ArgumentList @("--defaults-file=`"$dados\my.ini`"", '--bind-address=127.0.0.1', '--console') `
        -RedirectStandardError (Join-Path $tmp 'mysqld.err')
    $env:MYSQL_PWD = $senhaRoot
    for ($i = 0; $i -lt 100; $i++) {
        & { $ErrorActionPreference = 'Continue'; & "$bin\mysql.exe" --user=root --host=127.0.0.1 "--port=$porta" -e 'SELECT 1' 2>&1 | Out-Null }
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "MariaDB de teste em 127.0.0.1:$porta ($dados)"

    # ---------- Notas ----------
    $aSis = Chave $Numero $CnfSistema; $aAut = Chave $Numero $CnfAutorizada
    $notas = @(
        # numero, cNF sistema, cNF autorizada, id
        @{ N = 2002; S = '30000001'; A = '30000002'; Id = $IdMetre + 1; V = '50.00' },   # B: XML autorizado no chefxml
        @{ N = 2003; S = '30000003'; A = '30000004'; Id = $IdMetre + 2; V = '18.00' },   # C: XML autorizado em outra pasta
        @{ N = 2004; S = '30000005'; A = '30000006'; Id = $IdMetre + 3; V = '20.90' },   # D: chave autorizada já em outra linha
        @{ N = 2005; S = '30000007'; A = '30000008'; Id = $IdMetre + 4; V = '25.00' },   # E: valor autorizado diferente
        @{ N = 2006; S = '30000009'; A = '30000010'; Id = $IdMetre + 5; V = '30.00' },   # F: ASSINADA autorizado na pasta, mesmo valor
        @{ N = 2007; S = '30000011'; A = '30000012'; Id = $IdMetre + 6; V = '40.00' },   # G: ASSINADA autorizado na pasta, valor diferente
        @{ N = 2008; S = '30000013'; A = '30000014'; Id = $IdMetre + 7; V = '35.90' },   # H: só o Temp da chave autorizada, mesmo valor
        @{ N = 2009; S = '30000015'; A = '30000016'; Id = $IdMetre + 8; V = '12.00' }    # I: só o Temp da chave autorizada, valor diferente
    )
    foreach ($n in $notas) { $n.Sis = Chave $n.N $n.S; $n.Aut = Chave $n.N $n.A }
    $B, $C, $D, $E, $F, $G, $H, $I = $notas

    Rejeicao539 $Numero $aSis $aAut $Valor
    foreach ($n in $notas) { Rejeicao539 $n.N $n.Sis $n.Aut $n.V }
    Resposta $aAut $DigVal $NProt $CStatProt
    $bXml = NFe $B.Aut $B.N $B.V; Resposta $B.Aut $bXml.Digest '131260000000002' '100'
    $cXml = NFe $C.Aut $C.N $C.V; Resposta $C.Aut $cXml.Digest '131260000000003' '100'
    Gravar (Join-Path $xmls 'Outros\qualquer\outro_nome_2003.xml') $cXml.Xml
    $dXml = NFe $D.Aut $D.N $D.V; Resposta $D.Aut $dXml.Digest '131260000000004' '100'
    $eXml = NFe $E.Aut $E.N '99.00'; Resposta $E.Aut $eXml.Digest '131260000000005' '100'
    # F e G: o ASSINADA da chave autorizada está na pasta do dia (fluxo normal); só o valor decide o caminho.
    $fXml = NFe $F.Aut $F.N '30.00'; Resposta $F.Aut $fXml.Digest '131260000000006' '100'
    Gravar (Join-Path $xmls "10_2026\19\NOTA_$($F.N)_SERIE_${Serie}_ASSINADA_19_10_2026_13_00_00.xml") $fXml.Xml
    $gXml = NFe $G.Aut $G.N '45.00'; Resposta $G.Aut $gXml.Digest '131260000000007' '100'
    Gravar (Join-Path $xmls "10_2026\19\NOTA_$($G.N)_SERIE_${Serie}_ASSINADA_19_10_2026_13_10_00.xml") $gXml.Xml
    # H e I: da chave autorizada só existe o Temp_ANTES_ENVIO (lote <enviNFe>), que confere com o digVal; nenhum ASSINADA.
    foreach ($x in @(@{ N = $H; V = '35.90'; Hora = '13_20_00' }, @{ N = $I; V = '15.00'; Hora = '13_30_00' })) {
        $t = NFe $x.N.Aut $x.N.N $x.V; Resposta $x.N.Aut $t.Digest ('13126000000' + $x.N.N) '100'
        Gravar (Join-Path $xmls "10_2026\19\NOTA_$($x.N.N)_SERIE_${Serie}_CHAVE_$($x.N.Aut)_Temp_ANTES_ENVIO_19_10_2026_$($x.Hora).xml") `
            "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($t.Xml)</enviNFe>"
    }
    $aTentativa = NFe $aSis $Numero $Valor

    # ---------- XML autorizado só no chefxml, pelo pedido (como no cliente) ----------
    # O REJEICAO com a 539 está no servidor, sem ASSINADA da chave autorizada. A linha aponta (XML) para a tentativa
    # (PtrId); o XML autorizado (AutId) tem id_nota_saida NULL e, se PedAut = PedPtr, o mesmo id_pedido.
    $porPedido = @(
        @{ N = 2010; S = '30000017'; A = '30000018'; V = '61.00'; VAut = '61.00'; PtrId = 700010; AutId = 700007; PedPtr = 5010; PedAut = 5010; CStat = '120' },  # J: (a) valor igual; protocolo 120
        @{ N = 2011; S = '30000019'; A = '30000020'; V = '22.00'; VAut = '27.50'; PtrId = 700040; AutId = 700038; PedPtr = 5011; PedAut = 5011 },   # K: (b) valor diferente
        @{ N = 2012; S = '30000021'; A = '30000022'; V = '14.00'; VAut = '14.00'; PtrId = 700070; AutId = 700073; PedPtr = 5012; PedAut = 5012 },   # L: (c) depois do ponteiro
        @{ N = 2013; S = '30000023'; A = '30000024'; V = '16.00'; VAut = '16.00'; PtrId = 700100; AutId = 700098; PedPtr = $null; PedAut = $null }, # M: (d) sem id_pedido
        @{ N = 2014; S = '30000025'; A = '30000026'; V = '19.00'; VAut = '19.00'; PtrId = 700130; AutId = 700128; PedPtr = 5014; PedAut = 5014; FalsoId = 700126 }, # N: (e) dois da chave
        @{ N = 2015; S = '30000027'; A = '30000028'; V = '11.00'; VAut = '11.00'; PtrId = 700160; AutId = 700157; PedPtr = 5015; PedAut = 5015; DiaRej = '28'; Tent = "'2026-10-28 15:00:00'" }, # O: (f)
        @{ N = 2019; S = '30000035'; A = '30000036'; V = '23.00'; VAut = '23.00'; PtrId = 700190; AutId = 700188; PedPtr = 5019; PedAut = 7777 },   # T: outro pedido
        # U: (f) REJEICAO num dia de tentativa dentro dos dias extras de candidatos (caso visto no cliente)
        @{ N = 2020; S = '30000037'; A = '30000038'; V = '29.00'; VAut = '29.00'; PtrId = 700220; AutId = 700217; PedPtr = 5020; PedAut = 5020; DiaRej = '21'; Tent = "'2026-10-21 09:00:00'" }
    )
    $sqlNotas = @(); $sqlXmls = @(); $idNovo = $IdMetre + 100
    foreach ($x in $porPedido) {
        $x.Id = $idNovo++; $x.Sis = Chave $x.N $x.S; $x.ChAut = Chave $x.N $x.A
        $dia = if ($x.DiaRej) { $x.DiaRej } else { '19' }
        Rejeicao539 $x.N $x.Sis $x.ChAut $x.V $dia
        $tent = NFe $x.Sis $x.N $x.V; $aut = NFe $x.ChAut $x.N $x.VAut
        $cs = if ($x.CStat) { $x.CStat } else { '100' }
        if ($cs -eq '120') {
            Resposta $x.ChAut $aut.Digest ('13126000000' + $x.N) $cs 'Autorizado o uso da NF-e, com alerta' `
                '<cMsg>172</cMsg><xMsg>Alerta: Situacao do CNPJ destinatario inabilitado no momento da autorizacao</xMsg>'
        }
        else { Resposta $x.ChAut $aut.Digest ('13126000000' + $x.N) $cs }
        $sqlNotas += "($($x.Id), $($x.PtrId), $($x.N), $Serie, '$($x.Sis)', '2026-10-19', $(if ($x.PedPtr) { $x.PedPtr } else { 0 }), $($x.V), '2026-10-19 14:00:00', $(if ($x.Tent) { $x.Tent } else { 'NULL' }), 'Lote processado', 'Em contigencia', NULL)"
        $sqlXmls += "($($x.PtrId), CONVERT(UNHEX('$(Hex $tent.Xml)') USING utf8), $($x.Id), $(SqlN $x.PedPtr))"
        # Como o Metre grava as tentativas no chefxml: lote de envio, sem protocolo.
        $lote = '<enviNFe versao="4.00" xmlns="' + $Ns + '"><idLote>1</idLote><indSinc>1</indSinc>' + $aut.Xml + '</enviNFe>'
        $sqlXmls += "($($x.AutId), CONVERT(UNHEX('$(Hex $lote)') USING utf8), NULL, $(SqlN $x.PedAut))"
        if ($x.FalsoId) {
            # Mesma chave e mesmo pedido, outro conteúdo (valor 91,00): o DigestValue não é o digVal da SEFAZ.
            $falso = NFe $x.ChAut $x.N '91.00'
            $sqlXmls += "($($x.FalsoId), CONVERT(UNHEX('$(Hex $falso.Xml)') USING utf8), NULL, $(SqlN $x.PedAut))"
        }
    }
    # P: REJEICAO num dia fora do período e sem tentativa nesse dia (não é lido); Q: sem REJEICAO (não transmitida);
    # R: SEFAZ com o cStat do protocolo inválido (999) apesar do topo 100.
    $P = @{ N = 2016; Sis = (Chave 2016 '30000029'); ChAut = (Chave 2016 '30000030'); V = '9.00'; Id = $idNovo++ }
    $Q = @{ N = 2017; Sis = (Chave 2017 '30000031'); V = '8.00'; Id = $idNovo++ }
    $R = @{ N = 2018; Sis = (Chave 2018 '30000033'); ChAut = (Chave 2018 '30000034'); V = '7.00'; Id = $idNovo++ }
    # V: REJEICAO num dia extra de candidatos (22) sem tentativa nesse dia: continua só candidato, como antes.
    $V = @{ N = 2021; Sis = (Chave 2021 '30000039'); ChAut = (Chave 2021 '30000040'); V = '6.00'; Id = $idNovo++ }
    Rejeicao539 $P.N $P.Sis $P.ChAut $P.V '27'
    Rejeicao539 $V.N $V.Sis $V.ChAut $V.V '22'
    Rejeicao539 $R.N $R.Sis $R.ChAut $R.V
    $rXml = NFe $R.ChAut $R.N $R.V; Resposta $R.ChAut $rXml.Digest '131260000002018' '999' 'Situacao invalida'
    foreach ($x in $P, $Q, $R, $V) {
        $sqlNotas += "($($x.Id), NULL, $($x.N), $Serie, '$($x.Sis)', '2026-10-19', 0, $($x.V), '2026-10-19 15:00:00', NULL, 'Lote processado', 'Em contigencia', NULL)"
    }

    # Número reaproveitado: o XML autorizado é de outra venda, na pasta do dia da autorização (12/10), fora do período.
    # W: valor diferente; X: valor igual (por coincidência); Y: mesmo pedido da linha; Z: o Temp de 12/10 não confere com o digVal.
    $reaprov = @(
        @{ N = 2022; S = '30000041'; A = '30000042'; V = '100.56'; VAut = '77.00'; Ped = 6022; PedAut = 6122 },
        @{ N = 2023; S = '30000043'; A = '30000044'; V = '25.00'; VAut = '25.00'; Ped = 6023; PedAut = 6123 },
        @{ N = 2024; S = '30000045'; A = '30000046'; V = '31.00'; VAut = '31.00'; Ped = 6024; PedAut = 6024 },
        @{ N = 2025; S = '30000047'; A = '30000048'; V = '33.00'; VAut = '44.00'; Ped = 6025; PedAut = 6125; DigestErrado = $true }
    )
    foreach ($x in $reaprov) {
        $x.Id = $idNovo++; $x.Sis = Chave $x.N $x.S; $x.ChAut = Chave $x.N $x.A
        Rejeicao539 $x.N $x.Sis $x.ChAut $x.V
        $aut = NFe $x.ChAut $x.N $x.VAut '2026-10-12T14:53:42-03:00' $x.PedAut
        $dig = if ($x.DigestErrado) { 'BBBBBBBBBBBBBBBBBBBBBBBBBBB=' } else { $aut.Digest }
        Resposta $x.ChAut $dig ('13126000000' + $x.N) '100' 'Autorizado o uso da NF-e' '' '2026-10-12T14:53:45-03:00'
        Gravar (Join-Path $xmls "10_2026\12\NOTA_$($x.N)_MODELO_65_SERIE_${Serie}_CHAVE_$($x.ChAut)_Temp_ANTES_ENVIO_12_10_2026_14_53_43.xml") `
            "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($aut.Xml)</enviNFe>"
        $sqlNotas += "($($x.Id), NULL, $($x.N), $Serie, '$($x.Sis)', '2026-10-19', $($x.Ped), $($x.V), '2026-10-19 12:09:00', NULL, 'Lote processado', 'Em contigencia', NULL)"
    }

    # Nota emitida no caixa: 2026 só na pasta do caixa (estrutura de dias); 2027 solto na pasta do pen drive;
    # 2028 só o _NFCE aqui e o Temp no caixa; 2029 só o _NFCE aqui e o XML no chefxml.
    $caixa = Join-Path $tmp 'caixa'; $pendrive = Join-Path $tmp 'pendrive'
    $fora = @(
        @{ N = 2026; S = '30000049'; A = '30000050'; V = '50.00'; VAut = '55.00'; Onde = "$caixa\10_2026\12" },
        @{ N = 2027; S = '30000051'; A = '30000052'; V = '60.00'; VAut = '66.00'; Onde = $pendrive },
        @{ N = 2028; S = '30000053'; A = '30000054'; V = '70.00'; VAut = '77.00'; Onde = "$caixa\10_2026\12"; NfceLocal = $true },
        @{ N = 2029; S = '30000055'; A = '30000056'; V = '80.00'; VAut = '88.00'; NfceLocal = $true; XmlId = 700300 }
    )
    foreach ($x in $fora) {
        $x.Id = $idNovo++; $x.Sis = Chave $x.N $x.S; $x.ChAut = Chave $x.N $x.A
        Rejeicao539 $x.N $x.Sis $x.ChAut $x.V
        $aut = NFe $x.ChAut $x.N $x.VAut
        Resposta $x.ChAut $aut.Digest ('13126000000' + $x.N) '100' 'Autorizado o uso da NF-e' '' '2026-10-12T14:53:45-03:00'
        $lote = "<enviNFe versao=`"4.00`" xmlns=`"$Ns`"><idLote>1</idLote><indSinc>1</indSinc>$($aut.Xml)</enviNFe>"
        if ($x.Onde) { Gravar (Join-Path $x.Onde "NOTA_$($x.N)_MODELO_65_SERIE_${Serie}_CHAVE_$($x.ChAut)_Temp_ANTES_ENVIO_12_10_2026_14_53_43.xml") $lote }
        if ($x.NfceLocal) {
            $p = "<protNFe versao=`"4.00`"><infProt><tpAmb>1</tpAmb><chNFe>$($x.ChAut)</chNFe><dhRecbto>2026-10-12T14:53:45-03:00</dhRecbto><nProt>$('13126000000' + $x.N)</nProt><digVal>$($aut.Digest)</digVal><cStat>100</cStat><xMotivo>Autorizado</xMotivo></infProt></protNFe>"
            Gravar (Join-Path $xmls "10_2026\19\Autorizadas\NOTA_$($x.N)_MODELO_65_SERIE_${Serie}_NFCE.xml") "<nfeProc versao=`"4.00`" xmlns=`"$Ns`">$($aut.Xml)$p</nfeProc>"
        }
        if ($x.XmlId) { $sqlXmls += "($($x.XmlId), CONVERT(UNHEX('$(Hex $lote)') USING utf8), $($x.Id), NULL)" }
        $sqlNotas += "($($x.Id), NULL, $($x.N), $Serie, '$($x.Sis)', '2026-10-19', 0, $($x.V), '2026-10-19 12:09:00', NULL, 'Lote processado', 'Em contigencia', NULL)"
    }

    $sql = @"
CREATE DATABASE chef; CREATE DATABASE chefxml;
CREATE TABLE chef.nota_saida (
  id_nota_saida INT NOT NULL AUTO_INCREMENT PRIMARY KEY, data_quitacao DATETIME NULL, XML INT NULL, tipo VARCHAR(10) NULL,
  numero INT NULL, serie INT NULL, chave_acesso VARCHAR(44) NULL, dt_emissao DATE NULL, num_pedido INT NULL,
  valor_total_nota DECIMAL(15,2) NULL, data_contigencia DATETIME NULL, dt_tentativa_emissao DATETIME NULL,
  motivo_nao_transmissao VARCHAR(255) NULL, situacao VARCHAR(30) NULL, chave_acesso_devolucao VARCHAR(44) NULL, observacao TEXT NULL,
  KEY IX_DT_EMISSAO (dt_emissao), KEY IX_SITUACAO (situacao)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
CREATE TABLE chefxml.xml (id_xml INT NOT NULL AUTO_INCREMENT PRIMARY KEY, XML LONGTEXT, id_nota_saida INT, id_pedido INT, KEY Index_2 (id_nota_saida)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
INSERT INTO chef.nota_saida (id_nota_saida, XML, numero, serie, chave_acesso, dt_emissao, num_pedido, valor_total_nota, data_contigencia, dt_tentativa_emissao, motivo_nao_transmissao, situacao, observacao) VALUES
  ($IdMetre, $IdXml, $Numero, $Serie, '$aSis', '2026-10-19', $Pedido, $Valor, '2026-10-19 13:47:52', '2026-10-21 17:40:00', 'Lote processado', 'Em contigencia', CONCAT('linha 1', CHAR(13), CHAR(10), 'linha 2')),
  ($($B.Id), NULL, $($B.N), $Serie, '$($B.Sis)', '2026-10-19', 1, $($B.V), '2026-10-19 10:00:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  ($($C.Id), NULL, $($C.N), $Serie, '$($C.Sis)', '2026-10-19', 2, $($C.V), '2026-10-19 11:00:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  ($($D.Id), NULL, $($D.N), $Serie, '$($D.Sis)', '2026-10-19', 3, $($D.V), '2026-10-19 12:00:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  ($($E.Id), NULL, $($E.N), $Serie, '$($E.Sis)', '2026-10-19', 4, $($E.V), '2026-10-19 12:30:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  ($($F.Id), NULL, $($F.N), $Serie, '$($F.Sis)', '2026-10-19', 5, $($F.V), '2026-10-19 13:00:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  ($($G.Id), NULL, $($G.N), $Serie, '$($G.Sis)', '2026-10-19', 6, $($G.V), '2026-10-19 13:10:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  ($($H.Id), NULL, $($H.N), $Serie, '$($H.Sis)', '2026-10-19', 7, $($H.V), '2026-10-19 13:20:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  ($($I.Id), NULL, $($I.N), $Serie, '$($I.Sis)', '2026-10-19', 8, $($I.V), '2026-10-19 13:30:00', NULL, 'Lote processado', 'Em contigencia', NULL),
  (500, NULL, $($D.N), $Serie, '$($D.Aut)', '2026-10-19', 3, $($D.V), NULL, NULL, NULL, 'Autorizada', NULL),
  (501, NULL, 1999, $Serie, '$(Chave 1999 '40000001')', '2026-10-19', 9, 10.00, NULL, NULL, NULL, 'Autorizada', NULL),
  (600, NULL, 1990, $Serie, '$(Chave 1990 '40000002')', '2026-10-01', 10, 11.00, '2026-10-01 09:00:00', NULL, 'Lote processado', CONVERT(UNHEX('$(Hex 'Em Contigência 2')') USING utf8), NULL),
  (601, NULL, 1991, $Serie, '$(Chave 1991 '40000003')', '2026-10-19', 11, 12.00, '2026-10-19 09:00:00', NULL, 'Lote processado', CONVERT(UNHEX('$(Hex 'Em Contigência2')') USING utf8), NULL),
  $($sqlNotas -join ",`n  ");
INSERT INTO chefxml.xml (id_xml, XML, id_nota_saida, id_pedido) VALUES
  ($IdXml, CONVERT(UNHEX('$(Hex $aTentativa.Xml)') USING utf8), $IdMetre, $Pedido),
  (400001, CONVERT(UNHEX('$(Hex $bXml.Xml)') USING utf8), $($B.Id), 1),
  (400002, 'H4sIAAAAAAAAA+3BMQEAAADCoPVPbQwfoAAAAAAAAAAAAAAAAAAAAIC3AYbSVKsAQAAA', $($C.Id), 2),
  (400003, CONVERT(UNHEX('$(Hex $eXml.Xml)') USING utf8), $($E.Id), 4),
  $($sqlXmls -join ",`n  ");
CREATE USER 'metre'@'%' IDENTIFIED BY '$senhaMetre'; GRANT SELECT, UPDATE ON chef.* TO 'metre'@'%'; GRANT SELECT ON chefxml.* TO 'metre'@'%';
CREATE USER 'leitor'@'%' IDENTIFIED BY '$senhaMetre'; GRANT SELECT ON chef.* TO 'leitor'@'%'; GRANT SELECT ON chefxml.* TO 'leitor'@'%';
FLUSH PRIVILEGES;
"@
    [IO.File]::WriteAllText((Join-Path $tmp 'banco.sql'), $sql, $utf8)
    Get-Content (Join-Path $tmp 'banco.sql') -Raw | & "$bin\mysql.exe" --user=root --host=127.0.0.1 "--port=$porta" --default-character-set=utf8
    if ($LASTEXITCODE -ne 0) { throw "falha ao criar o banco de teste" }

    # ---------- SEFAZ simulada e certificado ----------
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $portaSefaz = $l.LocalEndpoint.Port; $l.Stop()
    $chamadas = Join-Path $tmp 'chamadas.txt'; $pronto = Join-Path $tmp 'pronto'; $parar = Join-Path $tmp 'parar'
    $servidor = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $PSScriptRoot 'sefaz_respostas.ps1'), '-Porta', $portaSefaz, '-Respostas', $respostas,
        '-ArqChamadas', $chamadas, '-ArqPronto', $pronto, '-ArqParar', $parar)
    for ($i = 0; $i -lt 200 -and -not (Test-Path $pronto); $i++) { Start-Sleep -Milliseconds 50 }
    $senhaCert = Senha
    NovoPfx "$tmp\emp\1\Certificado\cert.pfx" $senhaCert

    # Host e porta vêm do Conexao.ini de teste (como no Metre); usuário e senha, do ini do NotaCerta.
    "[Configuracao]`r`nip = 127.0.0.1`r`nporta = $porta`r`n" | Set-Content -Path "$tmp\Conexao.ini" -Encoding ASCII
    $ini = Join-Path $tmp 'teste.ini'
    @"
PastaRaiz=$xmls
PastaDestino=$tmp\dup
PastaRelatorios=$tmp\rel
PastaNotasFaltantes=$tmp\NotasFaltantes
IniMetre=$tmp\nenhum.ini
UrlConsulta=http://127.0.0.1:$portaSefaz/nfce/services/NFeConsultaProtocolo4
CertificadoPfx=$tmp\emp\1\Certificado
PastaEmpresas=$tmp\emp
CertificadoSenha=$senhaCert
FonteContingencia=banco
DbHost=
DbPorta=
IniConexaoMetre=$tmp\Conexao.ini
DbUsuario=metre
DbSenha=$senhaMetre
DbBanco=chef
DbBancoXml=chefxml
MariaDbDll=$PastaMariaDb\lib\libmariadb.dll
PastasXmlExtras=$caixa;$pendrive;$tmp\caixa_desligado
"@ | Set-Content -Path $ini -Encoding UTF8

    # ---------- Programa de teste (mesmas classes do NotaCerta) ----------
    $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
    $exe = Join-Path $tmp 'teste_ajuste.exe'
    $fontes = @(Get-ChildItem "$raiz\src\*.cs", "$raiz\src\Gui\*.cs" | ForEach-Object { $_.FullName }) + "$PSScriptRoot\TesteAjuste.cs"
    & "$fw\csc.exe" /nologo /codepage:65001 /target:exe /platform:anycpu /main:DuplicidadeNfce.TesteAjuste "/out:$exe" "/lib:$fw\WPF" `
        /r:System.Security.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Core.dll /r:System.Xaml.dll `
        /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll "/resource:$raiz\src\Gui\Principal.xaml,NotaCerta.Principal.xaml" @fontes
    if ($LASTEXITCODE -ne 0) { throw "falha ao compilar o teste" }

    Remove-Item Env:MYSQL_PWD
    $env:TESTE_PORTA = "$porta"
    & $exe $ini $tmp $senhaRoot $aSis $aAut
    $codigo = $LASTEXITCODE
    Write-Host "Log do NotaCerta: $(Get-ChildItem "$tmp\rel\duplicidade_*.log" | Select-Object -Last 1)"
    Write-Host "Backups: $tmp\backups"
    exit $codigo
}
finally {
    if ($Manter) {
        Write-Host "Mantidos no ar: MariaDB pid $($mysqld.Id) porta $porta, SEFAZ simulada pid $($servidor.Id). Config: $(Join-Path $tmp 'teste.ini')"
        Set-Content -Path (Join-Path $tmp 'root.txt') -Value $senhaRoot -Encoding ASCII
        $servidor = $null; $mysqld = $null
    }
    if ($servidor) { New-Item -ItemType File -Path $parar -Force | Out-Null; $servidor.WaitForExit(5000) | Out-Null }
    if ($mysqld) {
        $env:MYSQL_PWD = $senhaRoot
        & { $ErrorActionPreference = 'Continue'; & "$bin\mysqladmin.exe" --user=root --host=127.0.0.1 "--port=$porta" shutdown 2>&1 | Out-Null }
        Remove-Item Env:MYSQL_PWD -ErrorAction SilentlyContinue
        if (-not $mysqld.WaitForExit(20000)) { Stop-Process -Id $mysqld.Id -Force }
    }
}
