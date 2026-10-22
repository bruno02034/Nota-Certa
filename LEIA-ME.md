# NotaCerta

Conferência de NFC-e rejeitadas por duplicidade (cStat 539): acha o XML **autorizado** de cada nota
(conferindo o `DigestValue` com o `digVal` da SEFAZ-MG) e copia para `C:\Chef\NFCe\Duplicidade\AAAA-MM-DD`.

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
| A PROCESSAR | listada, aguardando consulta à SEFAZ |
| A COPIAR | arquivo autorizado encontrado (linha de comando sem `--copiar`) |
| COPIADO | copiado e conferido (tamanho e SHA-256) |
| JÁ PROCESSADA | já consta como copiada no `historico.csv` |
| AJUSTE_CHAVE_BANCO (fora do escopo) | valor do sistema = valor SEFAZ; nada é feito |
| PENDENTE | dúvida: SEFAZ não consultada, sem retorno 539, nenhum ou mais de um candidato, destino já existe… |
| ERRO_COPIA | a cópia não conferiu com o original |

## Regras de segurança

- Arquivos do emissor só são lidos; a cópia nunca move nem sobrescreve.
- Nada é escolhido pelo nome do arquivo: só pelo conteúdo (chave no `Id` + `DigestValue` = `digVal` da SEFAZ,
  com o digest recalculado).
- Sem resposta da SEFAZ, nada é copiado.

## Desenvolvimento

- `build.cmd` compila com o `csc` do próprio Windows (sem SDK).
- `testes\testar_amostras.ps1` roda os testes sobre `amostras\` (usa uma SEFAZ **simulada** local; nunca chama a real).
