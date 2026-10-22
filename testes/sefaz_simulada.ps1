# SEFAZ SIMULADA, só para testes: responde NFeConsultaProtocolo4 em http://127.0.0.1:<Porta>/
# com o protNFe autorizado gravado nos XMLs de <Amostras>. Registra cada chave consultada em <ArqChamadas>.
# Para quando o arquivo <ArqParar> existir.
param([int]$Porta, [string]$Amostras, [string]$ArqChamadas, [string]$ArqPronto, [string]$ArqParar)
$ErrorActionPreference = 'Stop'

$protocolos = @{}
foreach ($f in Get-ChildItem $Amostras -Recurse -Filter *.xml) {
    $t = [IO.File]::ReadAllText($f.FullName)
    foreach ($m in [regex]::Matches($t, '<infProt[^>]*>(?:(?!</infProt>).)*?<cStat>100</cStat>(?:(?!</infProt>).)*?</infProt>', 'Singleline')) {
        $ch = [regex]::Match($m.Value, '<chNFe>(\d{44})</chNFe>').Groups[1].Value
        $protocolos[$ch] = $m.Value
    }
}

$ouvinte = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $Porta)
$ouvinte.Start()
New-Item -ItemType File -Path $ArqPronto -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)

while (-not (Test-Path $ArqParar)) {
    if (-not $ouvinte.Pending()) { Start-Sleep -Milliseconds 30; continue }
    $cliente = $ouvinte.AcceptTcpClient()
    try {
        $s = $cliente.GetStream()
        $s.ReadTimeout = 5000
        $buf = New-Object byte[] 65536
        $ms = New-Object IO.MemoryStream
        $fimCab = -1
        while ($fimCab -lt 0) {
            $n = $s.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            $ms.Write($buf, 0, $n)
            $fimCab = $utf8.GetString($ms.ToArray()).IndexOf("`r`n`r`n")
        }
        $cab = $utf8.GetString($ms.ToArray()).Substring(0, $fimCab)
        $tam = [int]([regex]::Match($cab, '(?im)^Content-Length:\s*(\d+)').Groups[1].Value)
        while ($ms.Length -lt $fimCab + 4 + $tam) {
            $n = $s.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            $ms.Write($buf, 0, $n)
        }
        $corpo = $utf8.GetString($ms.ToArray()).Substring($fimCab + 4)
        $ch = [regex]::Match($corpo, '<chNFe>(\d{44})</chNFe>').Groups[1].Value
        Add-Content -Path $ArqChamadas -Value $ch

        if ($protocolos.ContainsKey($ch)) {
            $ret = '<cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo><cUF>31</cUF><dhRecbto>2026-10-22T12:00:00-03:00</dhRecbto>' +
                "<chNFe>$ch</chNFe><protNFe versao=`"4.00`">" + $protocolos[$ch] + '</protNFe>'
        } else {
            $ret = "<cStat>217</cStat><xMotivo>Rejeicao: NF-e nao consta na base de dados da SEFAZ</xMotivo><cUF>31</cUF><dhRecbto>2026-10-22T12:00:00-03:00</dhRecbto><chNFe>$ch</chNFe>"
        }
        $xml = '<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope"><soap:Body>' +
            '<nfeResultMsg xmlns="http://www.portalfiscal.inf.br/nfe/wsdl/NFeConsultaProtocolo4"><retConsSitNFe versao="4.00" xmlns="http://www.portalfiscal.inf.br/nfe">' +
            '<tpAmb>1</tpAmb><verAplic>SIMULADA-TESTE</verAplic>' + $ret + '</retConsSitNFe></nfeResultMsg></soap:Body></soap:Envelope>'
        $b = $utf8.GetBytes($xml)
        $h = $utf8.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: application/soap+xml; charset=utf-8`r`nContent-Length: $($b.Length)`r`nConnection: close`r`n`r`n")
        $s.Write($h, 0, $h.Length)
        $s.Write($b, 0, $b.Length)
        $s.Flush()
    }
    catch { Add-Content -Path $ArqChamadas -Value ("ERRO " + $_.Exception.Message) }
    finally { $cliente.Close() }
}
$ouvinte.Stop()
