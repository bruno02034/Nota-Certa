# NotaCerta

Conferência de NFC-e rejeitadas por duplicidade (cStat 539): consulta a chave autorizada na SEFAZ-MG e copia
para `C:\Chef\NFCe\Duplicidade\AAAA-MM-DD` o **ASSINADA dessa chave com o mesmo valor da SEFAZ** (é o arquivo que o
Metre importa). O `_NFCE` da pasta Autorizadas só confirma o conteúdo e o valor autorizados; ele não é copiado.

## Instalação no servidor

Copie `dist\NotaCerta.exe` e `dist\duplicidade.ini` para uma pasta qualquer (ex.: `C:\NotaCerta`).
Requer .NET Framework 4.5 (vem com o Windows Server 2012 R2). Nada mais a instalar.

Os XMLs são lidos de `C:\Chef\NFCe\MM_AAAA\DD\` (com subpastas, ex.: `Autorizadas`) e também dos `NOTA_*`
soltos direto em `C:\Chef\NFCe`; para estes, o dia é a data de emissão (`dhEmi`) gravada no próprio XML.

O certificado e a senha são lidos de `C:\Chef\NFCe.ini` (ou `C:\Chef\Config.ini`); o `.pfx` é procurado
também em `C:\Chef\Empresa\*\Certificado`, valendo o do CNPJ emitente. A senha nunca aparece em tela, log ou relatório.

## Uso

**Interface:** dê dois cliques em `NotaCerta.exe` → escolha as datas → *Listar notas com 539* →
marque as notas → *Processar marcadas* (consulta a SEFAZ, escolhe o arquivo e copia).

**Linha de comando** (PowerShell, na pasta do exe):

```
.\NotaCerta.exe --notas 6/88867,6/88868 --de 19/10/2026 --ate 19/10/2026
.\NotaCerta.exe --notas 6/88867,6/88868 --de 19/10/2026 --ate 19/10/2026 --copiar
```

`.\NotaCerta.exe --ajuda` lista todas as opções.

## Status

| Status | Significado |
|---|---|
| A PROCESSAR | duplicidade listada, aguardando consulta à SEFAZ |
| EM CONTINGÊNCIA | tentativa em contingência cujo número ainda não tem autorização; nada a fazer |
| OUTRO ERRO | rejeição com outro código (ex.: 537) |
| JÁ AUTORIZADA | a nota pedida já está autorizada e não tem tentativa pendente |
| A COPIAR | arquivo autorizado encontrado (linha de comando sem `--copiar`) |
| COPIADO | copiado e conferido (tamanho e SHA-256) |
| JÁ PROCESSADA | já consta como copiada no `historico.csv` |
| PENDENTE | dúvida: SEFAZ não consultada, sem retorno 539, nenhum ou mais de um candidato, destino já existe… |
| ERRO_COPIA | a cópia não conferiu com o original |

## Lista da contingência (banco do Metre)

Com `FonteContingencia=banco`, a lista vem de `chef.nota_saida` com `situacao = 'Em contigencia'` (o Metre grava
assim), por uma sessão **somente leitura** (só `SELECT`), usando a `libmariadb.dll` do HeidiSQL. Preencha `DbSenha`
no `duplicidade.ini` do servidor e confira com `.\NotaCerta.exe --testar-banco`. Linhas cuja chave já está autorizada
(criadas ao importar um NFCE) aparecem como JÁ AUTORIZADA e não são tocadas.

## Pastas das cópias

Cada processamento copia em `Duplicidade\AAAA-MM-DD\<hora>\importar-N\`. Quando o mesmo número aparece várias
vezes na contingência, o mesmo ASSINADA autorizado vai em `importar-1`, `importar-2`, `importar-3`… (uma cópia
por linha). Importe no Metre `importar-1`, depois `importar-2` e assim por diante.

## Como as duplicidades são achadas

Toda tentativa em contingência (`tpEmis=9`) ainda não autorizada é listada. Só as que têm **retorno 539**
registrado (`REJEICAO_NOTA`) são processadas e copiadas. Se o número já tem autorização com outra chave mas o
Metre ainda não registrou a 539, a linha fica PENDENTE "sem retorno 539" (informativa). Sem autorização para o
número, fica EM CONTINGÊNCIA.

## Regras de segurança

- Arquivos do emissor só são lidos; a cópia nunca move nem sobrescreve.
- Nada é escolhido pelo nome do arquivo: só pelo conteúdo (chave no `Id` + `DigestValue` = `digVal` da SEFAZ,
  com o digest recalculado).
- Sem resposta da SEFAZ, nada é copiado.

## Notas Faltantes

Para o relatório "Notas não lançadas de Saídas" (números sem XML no fiscal; o cliente costuma mandar uma foto).
Menu **Notas Faltantes**: digite os **Números** e a **Série**. Os números podem ser separados por vírgula, ponto e
vírgula, espaço ou linha; `1201-1205` busca a faixa inteira. A contagem "X número(s) a buscar" é atualizada
enquanto você digita; repetidos e inválidos são avisados e ignorados, sem impedir a busca dos demais.

- O CNPJ não é informado: vem do emitente dos XMLs encontrados e escolhe o certificado, o nome da pasta e o
  histórico. A tela mostra o CNPJ e a empresa usados. Se um número tiver XMLs de mais de um emitente, ele fica
  PENDENTE "mais de um emitente" e os demais seguem.
- Varre todas as subpastas de `C:\Chef\NFCe`. O nome do arquivo só pré-filtra; modelo 65, série e número são
  conferidos no conteúdo. `REJEICAO`, `FALHA_SCHEMA`/`ERRO_SCHEMA` e `Temp` são descartados (ficam no log).
- Não há mês de referência: a grade mostra a emissão (`dhEmi`) de cada nota e, se as notas encontradas forem de meses
  diferentes entre si, a tela destaca os meses e as notas fora do mês da maioria (a pasta do arquivo não conta).
- Cada chave é consultada uma vez na SEFAZ; o arquivo certo é o de DigestValue (recalculado) igual ao `digVal`.

| Status | O que vai para a entrega |
|---|---|
| ENCONTRADA | o XML da nota; se ele não tiver o protocolo, também `<chave>-procNFe.xml` (a nota + o protNFe da SEFAZ) |
| CANCELADA | a nota e o evento de cancelamento: o `procEventoNFe` do disco ou, sem ele, o devolvido pela SEFAZ (`<chave>-procEventoNFe-110111.xml`) |
| CANCELADA – SEM XML DO EVENTO | só a nota (o evento não está no disco nem na resposta da SEFAZ) |
| INUTILIZADA | o XML da inutilização homologada (cStat 102) que cobre o número; uma vez só, mesmo cobrindo vários números |

| NÃO CONSTA NA SEFAZ | nada (217: XML local que a SEFAZ não conhece, provavelmente contingência nunca transmitida) |
| NÃO ENCONTRADA | nada (sem nota e sem inutilização: pode ser número pulado a inutilizar) |
| PENDENTE | nada (SEFAZ não consultada, nenhum candidato com o digest da SEFAZ, mais de um autorizado, já existe no destino…) |

A inutilização não tem chave para consultar: só vale o XML de inutilização salvo no computador.

Saída em `C:\Chef\NFCe\NotasFaltantes\<CNPJ>_<data-hora>\` (`PastaNotasFaltantes` no ini): cópias com o nome
original, conferidas por tamanho e SHA-256, e o relatório CSV. *Gerar zip* cria o `.zip` ao lado da pasta.
O `historico.csv` da pasta base impede copiar o mesmo número de novo (marque *Copiar de novo as já copiadas* para
entregar outra vez). Nada é transmitido, cancelado nem inutilizado.

Linha de comando:

```
.\NotaCerta.exe --faltantes --serie 7 --numeros "501, 503, 510-512" --copiar --zip
```

## Desenvolvimento

- `build.cmd` compila com o `csc` do próprio Windows (sem SDK).
- `testes\testar_amostras.ps1` roda os testes sobre `amostras\` (usa uma SEFAZ **simulada** local; nunca chama a real).
- `testes\testar_faltantes.ps1` testa as Notas Faltantes com XMLs sintéticos (CNPJ fictício) e a SEFAZ simulada
  `testes\sefaz_respostas.ps1`.
