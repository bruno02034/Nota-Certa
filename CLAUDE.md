# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

NotaCerta: ferramenta Windows (WPF + linha de comando, um único `NotaCerta.exe`) para o servidor de um cliente que usa o
emissor/PDV **Metre** (pasta `C:\Chef`, banco MariaDB). Resolve NFC-e rejeitadas por duplicidade (cStat 539) e, na
seção "Notas Faltantes", localiza XMLs de números que faltam no fiscal. O `LEIA-ME.md` é o manual do usuário e
descreve as regras de negócio em detalhe; leia-o antes de mudar comportamento. Código, mensagens e commits em português.

## Build e testes

- `build.cmd` compila com o `csc.exe` do .NET Framework 4 (`%WINDIR%\Microsoft.NET\Framework\v4.0.30319`), sem SDK nem
  projeto .csproj: todos os `src\*.cs` e `src\Gui\*.cs`, com `src\Gui\Principal.xaml` embutido como recurso
  `NotaCerta.Principal.xaml`. Gera `dist\NotaCerta.exe` e copia `duplicidade.ini` (modelo) para `dist\` só se lá
  ainda não houver um (o do `dist\` guarda a senha do banco criptografada; opção nova no modelo precisa ir à mão).
  **O csc é C# 5**: nada de `?.`, `$"..."`, `nameof`, expressões `=>` em membros, etc.
- Testes são scripts PowerShell que rodam o exe (ou compilam um harness com as mesmas fontes); não há framework de teste
  nem como rodar um caso isolado, cada script roda tudo e imprime `OK`/`FALHA` por verificação:
  - `testes\testar_ajuste.ps1`: ajuste de chave e senha do banco. Sobe um **MariaDB de teste** (binários de
    `C:\Chef\Banco de Dados\bin`, pasta temporária, porta livre) e a SEFAZ simulada, compila `testes\TesteAjuste.cs`
    (`/main:DuplicidadeNfce.TesteAjuste`) com as fontes do app. Dados fictícios; um caso real entra só por parâmetro
    (`-Cnpj -Numero -CnfSistema -CnfAutorizada -Valor ...`). `-Manter` deixa banco e SEFAZ no ar e imprime o
    `teste.ini` para abrir a interface com `NotaCerta.exe --gui --config <teste.ini>`.
  - `testes\testar_amostras.ps1`: fluxo de cópia para importação. Precisa de `amostras\` e `testes\casos.local.ps1`
    (dados reais do cliente, fora do git); sem eles sai com aviso.
  - `testes\testar_faltantes.ps1`: Notas Faltantes com XMLs sintéticos.
  - Rode-os direto na sessão PowerShell (`& .\testes\testar_ajuste.ps1`); depois de mudar `src\`, rode `build.cmd`
    antes de `testar_amostras`/`testar_faltantes`, que usam `dist\NotaCerta.exe` (ou `$env:NOTACERTA_EXE`).
- Nenhum teste chama a SEFAZ real: `sefaz_simulada.ps1`/`sefaz_respostas.ps1` servem respostas locais
  (`UrlConsulta=` no ini aponta para elas). Nunca testar contra o banco do Metre do cliente.
- Captura de tela da interface (sem mexer no mouse): `NotaCerta.exe --gui --captura <png>` com as variáveis
  `NOTACERTA_LARGURA`/`NOTACERTA_ALTURA`/`NOTACERTA_PAGINA=faltantes`/`NOTACERTA_DETALHE_FIM`/`NOTACERTA_FALT_SERIE`/
  `NOTACERTA_FALT_NUMEROS`. Sem série/números a captura de Faltantes fica esperando e trava o exe (o build falha).

## Arquitetura

Fluxo da duplicidade (mesmo caminho para interface e linha de comando):

1. `Program.cs` lê argumentos em `Opcoes`; sem argumentos (ou `--gui`) abre `Gui.Executar`, senão roda em console.
   `Opcoes.CarregarConfig()` = `Config.Carregar(duplicidade.ini)` + sobreposições da linha de comando.
2. `Motor` (um por busca) orquestra: `Varrer()` lê os XMLs `NOTA_*` de `C:\Chef\NFCe\MM_AAAA\DD\` do período
   (`NotaXml`, sempre somente leitura, recalcula o DigestValue) e a lista da contingência (`Banco`: `chef.nota_saida`
   com `situacao='Em contigencia'`, ou CSV/arquivos conforme `FonteContingencia`). Depois `Listar()` /
   `Processar()` / `GravarRelatorio()`.
3. `Analisador` monta as `Tentativa`s (tentativas em contingência, com retorno 539 apontando a chave autorizada),
   consulta a SEFAZ (`Sefaz.cs`, NFeConsultaProtocolo4 com o certificado do CNPJ, cache por chave) e escolhe o caminho
   de cada linha (`LinhaRelatorio` + `Status` em `Relatorio.cs`):
   - **valor do sistema ≠ SEFAZ** → copia o **ASSINADA** da chave autorizada com o valor da SEFAZ para
     `Duplicidade\AAAA-MM-DD\<hora>\importar-N` (uma cópia por linha da contingência; `Copia.cs` confere tamanho e
     SHA-256 e registra no `historico.csv`). Sem ASSINADA da chave, vale o `Temp_ANTES_ENVIO` com DigestValue = `digVal`,
     das pastas lidas, das `PastasXmlExtras` (caixa, pen drive) ou gravado do `chefxml.xml` (`NotaXml.Conteudo`).
     O `_NFCE` só confirma o valor, nunca é copiado.
   - **número reaproveitado** (XML autorizado com outro pedido e outro dia): sem arquivo nas pastas lidas, o `dhRecbto`
     da SEFAZ indica a pasta do dia (só os arquivos daquele número). Vai para importação mesmo com valor igual.
   - **valor igual** → AJUSTE DE CHAVE (`AjusteChave.cs`): troca só `chef.nota_saida.chave_acesso` daquela linha,
     botão linha a linha, com backup da linha em `backups\`, transação com `FOR UPDATE` e `UPDATE ... WHERE id AND
     numero AND serie AND chave_acesso=<atual>` exigindo 1 linha afetada; "Desfazer" volta pelo backup.
   - O valor autorizado vale quando algum XML da chave tem DigestValue = `digVal` da SEFAZ (inclusive só o
     `Temp_ANTES_ENVIO`); sem arquivo, busca o XML no banco `chefxml.xml` pela PK; sem nada, conferência manual no
     portal (registrada no histórico).
   - Qualquer dúvida (SEFAZ sem resposta, mais de um candidato, etc.) → PENDENTE com o motivo; nada é escolhido pelo
     nome do arquivo.
4. `Gui\Gui.cs` (classe `Janela`) carrega `Principal.xaml` por `XamlReader` (sem code-behind; elementos achados por
   nome com `G<T>("Nome")`) e roda o `Motor` em `Task`s. `Gui\TelaFaltantes.cs` é a página de Notas Faltantes
   (lógica em `Faltantes.cs`); `Gui\TelaSenha.cs` é o diálogo da senha do banco, montado em código.

Configuração e segredos:
- `Config.cs` lê `duplicidade.ini` (chave=valor; opção desconhecida é erro). Host/porta do banco vêm de
  `C:\Chef\Conexao.ini` quando vazios. A senha do certificado vem dos INIs do Metre (`MetreIni.cs`,
  `C:\Chef\NFCe.ini`). A senha do banco é digitada na interface e gravada em `DbSenhaProtegida` (DPAPI da máquina,
  `SenhaBanco.cs`); `DbSenha` em texto é migrado e apagado ao abrir a interface. Senhas nunca vão para tela, log,
  relatório ou backup (os testes verificam).
- `Banco.cs` usa a `libmariadb.dll` por P/Invoke (do MariaDB do Metre ou do HeidiSQL), com timeouts; sessões de
  leitura são `READ ONLY`; a única escrita do sistema é o ajuste de chave.

## Regras do projeto

- Responder em português.
- Somente leitura nos arquivos do Metre (`C:\Chef`): copiar, nunca mover nem apagar.
- No banco, a única escrita permitida é `chave_acesso` em `chef.nota_saida`, pelo botão "Ajustar chave", com backup
  e transação. Nunca testar escrita no banco de um cliente.
- Alvo: .NET Framework 4.5 no Windows Server 2012 R2, com TLS 1.2 (`Sefaz.cs` liga o protocolo 3072, que o .NET 4
  não tem como constante). O entregável é `dist\NotaCerta.exe` gerado pelo `build.cmd`.
- Rodar os testes de `testes\` antes de dizer que algo está pronto.
- O fluxo de cópia para importação (valor diferente) não deve ser alterado sem pedido explícito; ele funciona no
  cliente. A regra do ASSINADA e do "importar-N" veio de testes reais (importar `_NFCE` aumentou a contingência).
- Não varrer `C:\Chef\NFCe` inteiro na duplicidade: deixou o processamento lento demais no cliente.
- A interface não busca nada ao abrir; só quando o usuário clica em "Listar contingência".
- `amostras\`, `exemplos\`, `testes\casos.local.ps1` e `*.pfx` contêm dados reais do cliente e não vão para o git.
- Para o cliente basta `dist\NotaCerta.exe` + `duplicidade.ini`; nas atualizações, copiar só o exe (o ini do
  servidor tem a senha criptografada daquela máquina).
