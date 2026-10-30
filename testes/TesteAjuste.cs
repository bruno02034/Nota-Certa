using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DuplicidadeNfce
{
    // Testes do "Ajustar chave no banco" contra um MariaDB DE TESTE (montado por testes\testar_ajuste.ps1).
    // Usa as mesmas classes da interface (Motor, Analisador, AjusteChave). Nunca apontar para o banco do Metre.
    // Uso: teste_ajuste.exe <config.ini> <pasta de trabalho> <senha root do banco de teste> <chave A> <chave autorizada A>
    static class TesteAjuste
    {
        static int falhas;
        static Config raiz;

        static void Verdade(bool c, string d)
        {
            Console.WriteLine((c ? "OK    " : "FALHA ") + d);
            if (!c) falhas++;
        }

        static int Main(string[] a)
        {
            try { return Rodar(a); }
            catch (Exception ex) { Console.WriteLine("FALHA exceção: " + ex); return 1; }
        }

        static Motor NovoMotor(string ini, string trabalho)
        {
            var cfg = Config.Carregar(ini);
            cfg.DataInicial = DateTime.Today;
            var m = new Motor(cfg, trabalho, false);
            m.Log.AoMostrar = s => { };
            m.Varrer();
            return m;
        }

        static List<LinhaRelatorio> Processar(Motor m) { return m.Processar(t => true, new string[0], false, null); }

        static List<string[]> Sql(string sql)
        {
            using (var s = Banco.Sessao.Abrir(raiz, null, false)) return s.Consultar(sql);
        }

        static void Exec(string sql)
        {
            using (var s = Banco.Sessao.Abrir(raiz, null, false)) s.Comando(sql);
        }

        static string Linha(string id) { return string.Join("|", Sql("SELECT * FROM chef.nota_saida WHERE id_nota_saida = " + id)[0].Select(v => v ?? "NULL")); }
        static string Tabela() { return string.Join("\n", Sql("SELECT * FROM chef.nota_saida ORDER BY id_nota_saida").Select(v => string.Join("|", v.Select(x => x ?? "NULL")))); }
        static string ChaveNoBanco(string id) { return Sql("SELECT chave_acesso FROM chef.nota_saida WHERE id_nota_saida = " + id)[0][0]; }

        static int Rodar(string[] a)
        {
            string ini = a[0], trabalho = a[1], senhaRoot = a[2], chaveA = a[3], autA = a[4];
            raiz = Config.Carregar(ini);
            raiz.DbUsuario = "root";
            raiz.DbSenha = senhaRoot;
            string idA = Sql("SELECT id_nota_saida FROM chef.nota_saida WHERE chave_acesso = '" + chaveA + "'")[0][0];
            Func<string> xmlsBanco = () => string.Join("|", Sql("SELECT COUNT(*), SUM(CRC32(XML)), SUM(IFNULL(id_nota_saida, 0)), SUM(IFNULL(id_pedido, 0)) FROM chefxml.xml")[0]);
            string xmlsAntes = xmlsBanco();

            // ---------- Regras da chave ----------
            Verdade(AjusteChave.DvValido(autA) && AjusteChave.DvValido(chaveA), "DV (módulo 11) das duas chaves do caso A válido");
            Verdade(!AjusteChave.DvValido(autA.Substring(0, 43) + ((autA[43] - '0' + 1) % 10)), "DV errado é recusado");
            Verdade(AjusteChave.MesmaNumeracao(chaveA, autA), "caso A: mesmos UF, AAMM, CNPJ, modelo, série e número");
            Verdade(!AjusteChave.MesmaNumeracao(chaveA, autA.Substring(0, 33) + "9" + autA.Substring(34)), "número diferente é recusado");

            // ---------- Senha do banco criptografada no ini ----------
            {
                string iniSenha = Path.Combine(trabalho, "senha_teste.ini");
                File.Copy(ini, iniSenha, true);
                string senha = Config.Carregar(ini).DbSenha, antesIni = File.ReadAllText(iniSenha);
                Verdade(Config.Carregar(iniSenha).SenhaEmTextoNoIni, "senha em texto no ini é detectada");
                SenhaBanco.GravarNoIni(iniSenha, senha);
                string texto = File.ReadAllText(iniSenha);
                var c2 = Config.Carregar(iniSenha);
                Verdade(!texto.Contains(senha) && texto.Contains("DbSenha=\r\n") && texto.Contains(SenhaBanco.Chave + "="), "gravada criptografada: DbSenha vazio e a senha não aparece no ini");
                Verdade(c2.DbSenha == senha && !c2.SenhaEmTextoNoIni && c2.ErroSenhaProtegida == null, "ini criptografado abre a mesma senha");
                Verdade(texto.Replace("\r", "").Split('\n').Length == antesIni.Replace("\r", "").Split('\n').Length + 1 &&
                    texto.Contains("PastaRaiz=" + c2.PastaRaiz), "demais linhas do ini preservadas");
                SenhaBanco.GravarNoIni(iniSenha, "outra");
                Verdade(Config.Carregar(iniSenha).DbSenha == "outra" && File.ReadAllText(iniSenha).Split(new[] { SenhaBanco.Chave }, StringSplitOptions.None).Length == 2,
                    "trocar a senha substitui a linha (não duplica)");
                File.WriteAllText(iniSenha, File.ReadAllText(iniSenha).Replace(SenhaBanco.Chave + "=", SenhaBanco.Chave + "=QUJD"));
                var c3 = Config.Carregar(iniSenha);
                Verdade(c3.DbSenha == "" && c3.ErroSenhaProtegida != null, "senha protegida inválida (outro computador) -> pede de novo (" + c3.ErroSenhaProtegida + ")");
                // O resto do teste roda com a senha criptografada no ini.
                File.Copy(ini, iniSenha, true);
                SenhaBanco.GravarNoIni(iniSenha, senha);
                ini = iniSenha;
            }

            // ---------- Ajustes salvos pela tela de Configurações ----------
            {
                string iniTela = Path.Combine(trabalho, "tela.ini");
                File.WriteAllText(iniTela, "# comentario\r\nPastaRaiz=C:\\Chef\\NFCe\r\nDbSenhaProtegida=QUJD\r\nDbUsuario=metre\r\n");
                var chefRede = Config.OpcoesDaPastaChef(@"\\SERVIDOR\Chef");
                chefRede["PastasXmlExtras"] = @"\\CAIXA1\Chef\NFCe;D:\Xmls";
                chefRede["FonteContingencia"] = "arquivos";
                Config.GravarOpcoes(iniTela, chefRede);
                string textoTela = File.ReadAllText(iniTela);
                var cTela = Config.Carregar(iniTela);
                Verdade(textoTela.Contains("# comentario") && textoTela.Contains("DbSenhaProtegida=QUJD") && textoTela.Contains("DbUsuario=metre") &&
                    textoTela.Split(new[] { "PastaRaiz=" }, StringSplitOptions.None).Length == 2,
                    "Salvar ajustes: comentários e senha preservados, opção trocada sem duplicar");
                Verdade(cTela.PastaRaiz == @"\\SERVIDOR\Chef\NFCe" && cTela.PastaChef == @"\\SERVIDOR\Chef" && cTela.IniMetre == @"\\SERVIDOR\Chef\NFCe.ini;\\SERVIDOR\Chef\Config.ini" &&
                    cTela.PastaBancoMetre == @"\\SERVIDOR\Chef\Banco de Dados" && cTela.FonteContingencia == "arquivos" && cTela.PastasXmlExtras == @"\\CAIXA1\Chef\NFCe;D:\Xmls",
                    "Salvar ajustes: o ini gravado é lido de volta com a pasta de rede");
                string conexaoRede = Path.Combine(trabalho, "conexao_localhost.ini");
                File.WriteAllText(conexaoRede, "[Configuracao]\r\nip = localhost\r\nporta = 3307\r\n");
                // Caminho de rede de verdade: a pasta administrativa da própria máquina, por IP.
                var cRede = new Config { IniConexaoMetre = @"\\127.0.0.1\" + conexaoRede.Replace(":", "$") };
                if (File.Exists(cRede.IniConexaoMetre))
                {
                    cRede.ResolverConexaoBanco();
                    Verdade(cRede.DbHost == "127.0.0.1" && cRede.DbPorta == 3307, "Conexao.ini pela rede dizendo localhost -> usa o computador do compartilhamento (" + cRede.DbHost + ":" + cRede.DbPorta + ")");
                }
                else Console.WriteLine("AVISO pasta administrativa inacessível: Conexao.ini pela rede não testado");
            }

            // ---------- Processamento ----------
            var m = NovoMotor(ini, trabalho);
            Verdade(m.Cfg.OrigemConexaoBanco != null && m.Cfg.DbPorta.ToString() == Environment.GetEnvironmentVariable("TESTE_PORTA"), "host/porta lidos do Conexao.ini (" + m.Cfg.DbHost + ":" + m.Cfg.DbPorta + ")");
            var linhas = Processar(m);
            Func<string, LinhaRelatorio> nota = n => linhas.First(l => l.Numero == n);

            // ---------- Contingência 2 (só consulta) ----------
            Verdade(!linhas.Any(l => l.Numero == "1990" || l.Numero == "1991") && !Banco.LerBanco(m.Cfg, m.Log).Any(c => c.Id == "600" || c.Id == "601"),
                "Contingência 2: 'Em Contigência 2' e 'Em Contigência2' ficam fora da contingência processada");
            var cont2 = Banco.LerContingencia2(m.Cfg);
            Verdade(string.Join(",", cont2.Select(c => c.Id)) == "600,601" && cont2[0].Situacao == "Em Contigência 2" && cont2[1].Situacao == "Em Contigência2" &&
                cont2[0].Valor == 11.00m && cont2[1].DtContingencia.HasValue,
                "Contingência 2: lista só as duas linhas escondidas, com situação, valor e data (" + string.Join("; ", cont2.Select(c => c.Id + "=" + c.Situacao)) + ")");
            var A = linhas.First(l => l.ChaveSistema == chaveA);
            Verdade(A.Status == Status.Pendente && A.Ajuste != null && A.Ajuste.PodeConferirPortal, "A sem XML da chave autorizada: PENDENTE com conferência no portal (" + A.Motivo + ")");
            Verdade(A.ValorSefaz == null && AjusteChave.Trava(A) != null, "A: sem valor da SEFAZ e ajuste travado");
            Verdade(A.Sel.Sefaz.Prot.CStat == "150", "A: protocolo cStat 150 (fora de prazo) aceito como autorizado");

            var B = nota("2002"); var C = nota("2003"); var D = nota("2004"); var E = nota("2005");
            Verdade(B.Status == Status.AjusteChave && B.Ajuste.OrigemValor.Contains("chefxml.xml"), "B: XML autorizado no banco chefxml confere com digVal -> AJUSTE DE CHAVE (" + B.Motivo + ")");
            Verdade(C.Status == Status.Pendente && C.Ajuste.PodeConferirPortal, "C: XML autorizado só fora das pastas do período não é procurado (rápido) -> conferir no portal");
            Verdade(D.Status == Status.Pendente && D.Motivo.Contains("já está em outra linha"), "D: chave autorizada já em outra linha -> bloqueado (" + D.Motivo + ")");
            Verdade(E.Status == Status.ACopiar && E.Ajuste == null && E.Fonte.Conteudo != null && E.Motivo.Contains("≠ SEFAZ"),
                "E: XML no banco com valor diferente do sistema -> não ajusta, grava para importar (" + E.Motivo + ")");
            var F = nota("2006"); var G = nota("2007");
            Verdade(F.Status == Status.AjusteChave && F.Fonte == null && F.CaminhoDestino == null && AjusteChave.Trava(F) == null,
                "F: ASSINADA autorizado na pasta e MESMO valor -> AJUSTE DE CHAVE, nada para importar (" + F.Motivo + ")");
            Verdade(G.Status == Status.ACopiar && G.Fonte != null && G.CaminhoDestino.Contains("importar-1") && G.Ajuste == null,
                "G: ASSINADA autorizado na pasta e valor DIFERENTE -> fluxo de cópia para importação, como antes (" + G.Motivo + ")");
            var H = nota("2008"); var I = nota("2009");
            Verdade(H.Status == Status.AjusteChave && H.ValorSefaz == 35.90m && H.Fonte == null && H.CaminhoDestino == null && AjusteChave.Trava(H) == null &&
                H.Ajuste.OrigemValor.Contains("DigestValue = digVal"),
                "H: só o Temp confere com o digVal, sem ASSINADA, MESMO valor -> AJUSTE DE CHAVE (" + H.Motivo + ")");
            Verdade(I.Status == Status.ACopiar && I.ValorSefaz == 15.00m && I.CaminhoDestino.Contains("importar-1") && I.CaminhoDestino.Contains("Temp_ANTES_ENVIO") &&
                I.Fonte != null && I.Ajuste == null,
                "I: só o Temp confere, sem ASSINADA, valor DIFERENTE -> copia o Temp_ANTES_ENVIO para importar (" + I.Motivo + ")");
            // ---------- Número reaproveitado: XML autorizado na pasta do dia da autorização, fora do período ----------
            var W2 = nota("2022"); var X2 = nota("2023"); var Y2 = nota("2024"); var Z2 = nota("2025");
            Verdade(W2.Status == Status.ACopiar && W2.ValorSefaz == 77.00m && W2.CaminhoDestino.Contains("Temp_ANTES_ENVIO") &&
                W2.Motivo.Contains("NÚMERO REAPROVEITADO") && W2.Motivo.Contains("pedido 6122") && W2.Motivo.Contains("pedido 6022"),
                "Reaproveitado, valor diferente: acha pela data de autorização e separa o Temp, mostrando as duas vendas (" + W2.Motivo + ")");
            Verdade(X2.Status == Status.ACopiar && X2.Ajuste == null && X2.Fonte != null && X2.Motivo.Contains("NÚMERO REAPROVEITADO"),
                "Reaproveitado, valor igual por coincidência: vai para importação, nunca para o ajuste (" + X2.Motivo + ")");
            string origemW2 = W2.Fonte.Caminho;
            Copiador.Executar(new List<LinhaRelatorio> { W2 }, m.Historico, m.Log);
            Verdade(W2.Status == Status.Copiado && File.Exists(W2.CaminhoDestino) && File.Exists(origemW2) &&
                NotaXml.HashSha256(File.ReadAllBytes(W2.CaminhoDestino)) == NotaXml.HashSha256(File.ReadAllBytes(origemW2)),
                "Reaproveitado: Temp copiado igual ao original, que continua na pasta (" + W2.CaminhoDestino + ")");
            Verdade(Y2.Status == Status.AjusteChave && !Y2.Motivo.Contains("REAPROVEITADO"),
                "Mesmo pedido, outro dia e valor igual: é a mesma venda -> AJUSTE DE CHAVE (" + Y2.Motivo + ")");
            Verdade(Z2.Status != Status.Copiado && Z2.Fonte == null && Z2.ValorSefaz == null,
                "Temp do dia da autorização sem DigestValue = digVal: nada é copiado (" + Z2.Motivo + ")");
            Verdade(linhas.Where(l => l.Status == Status.AjusteChave).All(l => AjusteChave.Trava(l) == null), "linhas AJUSTE DE CHAVE sem travas");

            // ---------- XML autorizado só no chefxml, pelo pedido (REJEICAO no servidor, sem ASSINADA) ----------
            var J = nota("2010"); var K = nota("2011"); var L = nota("2012"); var M = nota("2013"); var N = nota("2014");
            var O = nota("2015"); var P = nota("2016"); var Q = nota("2017"); var R = nota("2018"); var T = nota("2019");
            Verdade(J.Status == Status.AjusteChave && !J.Ajuste.PodeConferirPortal && J.Ajuste.OrigemValor.Contains("id_xml 700007 (mesmo pedido 5010)") &&
                J.Fonte == null && AjusteChave.Trava(J) == null,
                "(a) XML autorizado só no chefxml, id_nota_saida NULL e mesmo pedido, valor igual -> AJUSTE DE CHAVE sem portal (" + J.Motivo + ")");
            Verdade(K.Status == Status.ACopiar && K.ValorSefaz == 27.50m && K.Ajuste == null && K.Fonte.Conteudo != null && K.CaminhoOrigem.Contains("id_xml 700038") &&
                K.CaminhoDestino.Contains("importar-1") && K.ArquivoEscolhido.Contains("_CHAVE_" + K.ChaveAutorizada + "_Temp_ANTES_ENVIO_19_10_2026_13_47_50.xml"),
                "(b) idem com valor diferente -> gravado do banco para importar (" + K.Motivo + ")");
            byte[] conteudoK = K.Fonte.Conteudo;
            Copiador.Executar(new List<LinhaRelatorio> { K }, m.Historico, m.Log);
            var copiaK = K.Status == Status.Copiado ? NotaXml.Ler(K.CaminhoDestino) : null;
            Verdade(copiaK != null && File.ReadAllBytes(K.CaminhoDestino).SequenceEqual(conteudoK) && copiaK.Chave == K.ChaveAutorizada &&
                copiaK.DigestCalculado == K.DigValSefaz && copiaK.VNF == 27.50m && copiaK.Tipo == TipoArquivo.Temp,
                "(b) arquivo gravado é o XML do banco, com DigestValue = digVal (" + K.Status + ": " + K.Motivo + ")");

            // ---------- Nota emitida em outro computador: pastas extras (caixa, pen drive) ----------
            var Caixa = nota("2026"); var Pen = nota("2027"); var SoNfce = nota("2028"); var NfceBanco = nota("2029");
            Verdade(Caixa.Status == Status.ACopiar && Caixa.ValorSefaz == 55.00m && Caixa.CaminhoOrigem.Contains(@"\caixa\10_2026\12\"),
                "Pasta extra: acha o Temp no dia da autorização dentro da pasta do caixa (" + Caixa.CaminhoOrigem + ")");
            Verdade(Pen.Status == Status.ACopiar && Pen.ValorSefaz == 66.00m && Pen.CaminhoOrigem.Contains(@"\pendrive\NOTA_2027"),
                "Pasta extra: acha o Temp solto na pasta do pen drive (" + Pen.CaminhoOrigem + ")");
            Verdade(SoNfce.Status == Status.ACopiar && SoNfce.CaminhoOrigem.Contains(@"\caixa\") && SoNfce.ArquivoEscolhido.Contains("Temp_ANTES_ENVIO"),
                "Só o _NFCE nas pastas lidas: busca o Temp no caixa, o _NFCE não é copiado (" + SoNfce.ArquivoEscolhido + ")");
            Verdade(NfceBanco.Status == Status.ACopiar && NfceBanco.Fonte.Conteudo != null && NfceBanco.CaminhoOrigem.Contains("id_xml 700300"),
                "Só o _NFCE nas pastas lidas e XML no chefxml: gravado do banco para importar (" + NfceBanco.CaminhoOrigem + ")");
            string logExtras = System.Text.Encoding.UTF8.GetString(NotaXml.LerSomenteLeitura(m.ArqLog));
            Verdade(logExtras.Split(new[] { "caixa_desligado" }, StringSplitOptions.None).Length == 2,
                "Pasta extra inacessível: avisa uma vez só e segue");

            // ---------- Sem conexão com o banco (FonteContingencia=arquivos) ----------
            {
                string iniArq = Path.Combine(trabalho, "sem_banco.ini");
                File.WriteAllText(iniArq, File.ReadAllText(ini).Replace("FonteContingencia=banco", "FonteContingencia=arquivos")
                    .Replace("PastaDestino=" + trabalho + @"\dup", "PastaDestino=" + trabalho + @"\dup_sem_banco"));
                var cfgArq = Config.Carregar(iniArq);
                cfgArq.DataInicial = new DateTime(2026, 10, 19);
                cfgArq.DataFinal = new DateTime(2026, 10, 19);
                var mArq = new Motor(cfgArq, trabalho, false);
                mArq.Log.AoMostrar = s => { };
                mArq.Varrer();
                var semBanco = Processar(mArq);
                var R22 = semBanco.First(l => l.Numero == "2022"); var R26 = semBanco.First(l => l.Numero == "2026");
                Verdade(mArq.Contingencia == null && R22.Status == Status.ACopiar && R22.ValorSefaz == 77.00m && R22.CaminhoOrigem.Contains(@"\10_2026\12\"),
                    "Sem banco: acha o XML pelo dia da autorização (" + R22.Motivo + ")");
                Verdade(R26.Status == Status.ACopiar && R26.CaminhoOrigem.Contains(@"\caixa\"), "Sem banco: acha o XML na pasta do caixa");
                Verdade(semBanco.All(l => l.Status != Status.AjusteChave), "Sem banco: nenhuma linha oferece ajuste de chave");
                mArq.Dispose();
            }
            Verdade(L.Status == Status.AjusteChave && L.Ajuste.OrigemValor.Contains("id_xml 700073"), "(c) XML do pedido depois do ponteiro também vale (" + L.Ajuste.OrigemValor + ")");
            Verdade(M.Status == Status.Pendente && M.Ajuste != null && M.Ajuste.PodeConferirPortal &&
                System.Text.Encoding.UTF8.GetString(NotaXml.LerSomenteLeitura(m.ArqLog)).Contains("id_xml 700100 sem id_pedido"),
                "(d) ponteiro sem id_pedido -> janela do pedido não é usada, conferir no portal (" + M.Motivo + ")");
            Verdade(N.Status == Status.AjusteChave && N.ValorSefaz == 19.00m && N.Sel.Candidatos.Count(c => c.Aceito) == 1 &&
                N.Sel.Candidatos.Any(c => !c.Aceito && c.Caminho.Contains("id_xml 700126") && c.Motivo.Contains("≠ digVal")),
                "(e) dois XMLs da chave no pedido: só o de DigestValue = digVal vale (" + N.Ajuste.OrigemValor + ")");
            Verdade(m.DiasTentativa.Contains(new DateTime(2026, 10, 28)) && !m.DiasTentativa.Contains(new DateTime(2026, 10, 27)) &&
                O.Status == Status.AjusteChave && O.Classificacao.Contains("539"),
                "(f) REJEICAO na pasta do dia da tentativa (fora do período de emissão) é achado -> AJUSTE DE CHAVE (" + O.Classificacao + ")");
            Verdade(P.ChaveAutorizada == null && P.Status == Status.EmContingencia, "(f) REJEICAO num dia fora do período e sem tentativa nesse dia não é lido (só as pastas dos dias)");
            var U = nota("2020"); var V = nota("2021");
            Verdade(m.DiasTentativa.Contains(new DateTime(2026, 10, 21)) && U.Status == Status.AjusteChave && U.Classificacao.Contains("539"),
                "(f) REJEICAO num dia de tentativa dentro dos dias extras de candidatos também vale (" + U.Motivo + ")");
            Verdade(V.ChaveAutorizada == null && V.Status == Status.EmContingencia, "(f) REJEICAO num dia extra sem tentativa continua só candidato, como antes");
            Verdade(Q.Status == Status.EmContingencia && Q.Motivo == Analisador.TransmitaPrimeiro && P.Motivo == Analisador.TransmitaPrimeiro,
                "(g) nota sem REJEICAO -> \"" + Q.Motivo + "\"");
            Verdade(J.Sel.Sefaz.Prot.CStat == "120" && J.Sel.Sefaz.Autorizada, "(h) protocolo cStat 120 (autorizado com alerta) aceito");
            Verdade(R.Status == Status.Pendente && R.Motivo.Contains("não é autorizada") && !R.Sel.Sefaz.Autorizada,
                "(h) protocolo com cStat inválido (999) e topo 100 recusado (" + R.Motivo + ")");
            Verdade(T.Status == Status.Pendente && T.Ajuste != null && T.Ajuste.PodeConferirPortal,
                "XML da chave perto do ponteiro, mas de outro pedido -> não usado, conferir no portal");

            // (h) Resposta real da SEFAZ-MG de 29/10 (topo 100, protNFe 120 com alerta), com chave/protocolo/digVal fictícios.
            {
                var interpretar = typeof(ClienteSefaz).GetMethod("Interpretar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Func<string, string, string, bool> autorizada = (topo, prot, nProt) =>
                {
                    var r0 = new RespostaSefaz { XmlBruto = "<?xml version='1.0' encoding='UTF-8'?><S:Envelope xmlns:S=\"http://www.w3.org/2003/05/soap-envelope\"><S:Body>" +
                        "<nfeResultMsg xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeConsultaProtocolo4\"><retConsSitNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"4.00\">" +
                        "<tpAmb>1</tpAmb><verAplic>W-1.6.27</verAplic><cStat>" + topo + "</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo><cUF>31</cUF>" +
                        "<dhRecbto>2026-10-29T16:20:13-03:00</dhRecbto><chNFe>" + autA + "</chNFe><protNFe versao=\"4.00\"><infProt Id=\"ID131260000009999\"><tpAmb>1</tpAmb>" +
                        "<verAplic>W-1.6.27</verAplic><chNFe>" + autA + "</chNFe><dhRecbto>2026-10-28T17:07:52-03:00</dhRecbto>" + (nProt == null ? "" : "<nProt>" + nProt + "</nProt>") +
                        "<digVal>AAAAAAAAAAAAAAAAAAAAAAAAAAA=</digVal><cStat>" + prot + "</cStat><xMotivo>Autorizado o uso da NF-e, com alerta</xMotivo><cMsg>172</cMsg>" +
                        "<xMsg>Alerta: Situacao do CNPJ destinatario inabilitado no momento da autorizacao</xMsg></infProt></protNFe></retConsSitNFe></nfeResultMsg></S:Body></S:Envelope>" };
                    interpretar.Invoke(null, new object[] { r0, autA });
                    return r0.Autorizada;
                };
                Verdade(autorizada("100", "120", "131260000009999") && autorizada("100", "100", "131260000009999") && autorizada("150", "150", "131260000009999"),
                    "(h) resposta no formato real: topo 100/150 com protocolo 100, 150 ou 120 -> autorizada");
                Verdade(!autorizada("100", "999", "131260000009999") && !autorizada("100", "110", "131260000009999") && !autorizada("100", "539", "131260000009999") &&
                    !autorizada("101", "120", "131260000009999") && !autorizada("110", "100", "131260000009999") && !autorizada("100", "120", null),
                    "(h) protocolo 999/110/539, topo 101/110 ou sem nProt -> não autorizada");
            }

            // ---------- Conferência manual ----------
            AjusteChave.RegistrarConferencia(A, false, m.Historico, m.Log);
            m.Analisador.ReavaliarAjuste(A, m.Historico);
            Verdade(A.Status == Status.Pendente && A.Motivo.Contains("valor diferente no portal"), "A: 'Valor diferente' mantém PENDENTE");
            AjusteChave.RegistrarConferencia(A, true, m.Historico, m.Log);
            m.Analisador.ReavaliarAjuste(A, m.Historico);
            Verdade(A.Status == Status.AjusteChave && A.Ajuste.ValorManual && AjusteChave.Trava(A) == null, "A: 'Valor confere' -> AJUSTE DE CHAVE (" + A.Ajuste.OrigemValor + ")");
            Verdade(m.Historico.Todos.Any(h => h.Status == Status.ValorConfereManual && h.Arquivo.Contains("MANUAL") && h.DataHora.Length == 19), "conferência manual registrada no histórico com data/hora");

            string antes = Linha(idA), tabelaAntes = Tabela();

            // ---------- Travas no banco (nada pode mudar) ----------
            Func<string, bool> falhaSemMudar = desc =>
            {
                string erro = null;
                try { AjusteChave.Ajustar(m.Cfg, A, m.PastaBackups, m.Historico, m.Log); }
                catch (Exception ex) { erro = ex.Message; }
                Console.WriteLine("      erro: " + erro);
                return erro != null && erro.Contains("rollback");
            };
            Exec("UPDATE chef.nota_saida SET chave_acesso = '" + chaveA.Substring(0, 35) + "88888888" + chaveA[43] + "' WHERE id_nota_saida = " + idA);
            Verdade(falhaSemMudar("chave mudou"), "chave_acesso mudou no banco depois da análise -> erro com rollback");
            Exec("UPDATE chef.nota_saida SET chave_acesso = '" + chaveA + "' WHERE id_nota_saida = " + idA);
            Exec("INSERT INTO chef.nota_saida (id_nota_saida, numero, serie, chave_acesso, valor_total_nota, situacao) VALUES (999999, 1, 1, '" + autA + "', 1, 'Autorizada')");
            Verdade(falhaSemMudar("chave em outra linha"), "chave autorizada apareceu em outra linha -> erro com rollback");
            Exec("DELETE FROM chef.nota_saida WHERE id_nota_saida = 999999");
            Exec("UPDATE chef.nota_saida SET valor_total_nota = valor_total_nota + 1 WHERE id_nota_saida = " + idA);
            Verdade(falhaSemMudar("valor mudou"), "valor_total_nota mudou no banco -> erro com rollback");
            Exec("UPDATE chef.nota_saida SET valor_total_nota = valor_total_nota - 1 WHERE id_nota_saida = " + idA);
            var cfgLeitor = Config.Carregar(ini);
            cfgLeitor.DbUsuario = "leitor";
            {
                string erro = null;
                try { AjusteChave.Ajustar(cfgLeitor, A, m.PastaBackups, m.Historico, m.Log); } catch (Exception ex) { erro = ex.Message; }
                Console.WriteLine("      erro: " + erro);
                Verdade(erro != null && erro.Contains("rollback"), "usuário sem permissão de UPDATE -> erro com rollback");
            }
            Verdade(Tabela() == tabelaAntes, "depois das 4 recusas a tabela está idêntica");

            // ---------- Ajuste ----------
            var r = AjusteChave.Ajustar(m.Cfg, A, m.PastaBackups, m.Historico, m.Log);
            string depois = Linha(idA);
            Verdade(ChaveNoBanco(idA) == autA, "ajuste gravado: chave_acesso = chave autorizada");
            Verdade(depois == antes.Replace(chaveA, autA), "na linha só mudou chave_acesso (demais " + (antes.Split('|').Length - 1) + " colunas iguais)");
            Verdade(Tabela() == tabelaAntes.Replace(chaveA, autA), "nenhuma outra linha mudou");
            Verdade(xmlsBanco() == xmlsAntes, "chefxml.xml intocado");
            string bak = File.ReadAllText(r.CaminhoDestino);
            Verdade(File.Exists(r.CaminhoDestino) && Path.GetDirectoryName(r.CaminhoDestino) == m.PastaBackups && bak.Contains("chave_antiga=" + chaveA) &&
                bak.Contains("chave_acesso=" + chaveA) && bak.Contains("observacao=0x"), "backup com a linha inteira (texto com quebra de linha em hex) em backups\\");
            Verdade(r.Status == Status.ChaveAjustada && m.Historico.BuscarAjuste(autA) != null, "histórico: CHAVE AJUSTADA");
            Verdade(!System.Text.Encoding.UTF8.GetString(NotaXml.LerSomenteLeitura(m.ArqLog)).Contains(m.Cfg.DbSenha) && !bak.Contains(m.Cfg.DbSenha), "senha do banco fora do log e do backup");
            {
                string erro = null;
                try { AjusteChave.Ajustar(m.Cfg, A, m.PastaBackups, m.Historico, m.Log); } catch (Exception ex) { erro = ex.Message; }
                Verdade(erro != null && ChaveNoBanco(idA) == autA, "repetir o ajuste é recusado (" + erro + ")");
            }

            // Nova listagem: a linha já aparece como CHAVE AJUSTADA.
            m.Dispose();
            m = NovoMotor(ini, trabalho);
            var lista = m.Listar();
            var A2 = lista.FirstOrDefault(l => l.ChaveSistema == autA);
            Verdade(A2 != null && A2.Status == Status.ChaveAjustada && A2.Ajuste.Backup == r.CaminhoDestino, "nova listagem: CHAVE AJUSTADA com o backup para desfazer");

            // ---------- Desfazer ----------
            var d = AjusteChave.Desfazer(m.Cfg, A2.Ajuste.Backup, m.PastaBackups, m.Historico, m.Log);
            Verdade(ChaveNoBanco(idA) == chaveA && Linha(idA) == antes && Tabela() == tabelaAntes, "desfazer: chave antiga de volta e tabela idêntica à original");
            Verdade(d.Status == Status.AjusteDesfeito && m.Historico.BuscarAjuste(autA) == null, "histórico: AJUSTE DESFEITO");
            {
                string erro = null;
                try { AjusteChave.Desfazer(m.Cfg, A2.Ajuste.Backup, m.PastaBackups, m.Historico, m.Log); } catch (Exception ex) { erro = ex.Message; }
                Verdade(erro != null && erro.Contains("rollback") && ChaveNoBanco(idA) == chaveA, "desfazer de novo é recusado (" + erro + ")");
            }
            Verdade(Directory.GetFiles(m.PastaBackups, "desfazer_ajuste_*").Length >= 1, "backup também antes de desfazer");

            // Processando de novo: a conferência manual continua valendo.
            m.Dispose();
            m = NovoMotor(ini, trabalho);
            var A3 = Processar(m).First(l => l.ChaveSistema == chaveA);
            Verdade(A3.Status == Status.AjusteChave && A3.Ajuste.ValorManual, "reprocessada: AJUSTE DE CHAVE pela conferência manual registrada");

            // ---------- Backup adulterado não pode ser desfeito ----------
            var r2 = AjusteChave.Ajustar(m.Cfg, A3, m.PastaBackups, m.Historico, m.Log);
            string tabelaAjustada = Tabela();
            Func<string, string, bool> recusado = (conteudo, desc) =>
            {
                string arq = Path.Combine(m.PastaBackups, "adulterado_" + desc + ".txt");
                File.WriteAllText(arq, conteudo, new System.Text.UTF8Encoding(true));
                string erro = null;
                try { AjusteChave.Desfazer(m.Cfg, arq, m.PastaBackups, m.Historico, m.Log); } catch (Exception ex) { erro = ex.Message; }
                File.Delete(arq);
                Console.WriteLine("      erro: " + erro);
                return erro != null && Tabela() == tabelaAjustada;
            };
            string original = File.ReadAllText(r2.CaminhoDestino);
            Verdade(original.Contains("\nassinatura="), "backup do ajuste sai assinado");
            Verdade(recusado(original.Replace("id_nota_saida=" + idA, "id_nota_saida=500"), "id"), "backup com id_nota_saida trocado -> desfazer recusado, banco intocado");
            Verdade(recusado(original.Substring(0, original.LastIndexOf("assinatura=")), "semassinatura"), "backup sem assinatura -> desfazer recusado");
            Verdade(recusado(original.Replace("chave_nova=", "chave_nova= "), "espaco"), "backup com um espaço a mais -> desfazer recusado");
            AjusteChave.Desfazer(m.Cfg, r2.CaminhoDestino, m.PastaBackups, m.Historico, m.Log);
            Verdade(ChaveNoBanco(idA) == chaveA && Tabela() == tabelaAntes, "backup íntegro desfaz normalmente");
            m.Dispose();

            // ---------- CSV: texto com cara de fórmula não vira fórmula no Excel ----------
            {
                string csv = Path.Combine(trabalho, "formula.csv");
                Relatorio.GravarCsv(csv, new List<LinhaRelatorio> { new LinhaRelatorio
                {
                    Numero = "1", Serie = "1", Pedido = "=2+5+cmd|'/C calc'!A1", Origem = "@SUM(1)", Motivo = "+HYPERLINK(\"x\")",
                    ArquivoEscolhido = "-2+3", ChaveSistema = chaveA, ChaveAutorizada = "=1+1", Status = "-", ValorSistema = 10.5m
                } });
                var campos = File.ReadAllLines(csv)[1].Split(';')
                    .Select(c => c.StartsWith("\"") && c.EndsWith("\"") ? c.Substring(1, c.Length - 2).Replace("\"\"", "\"") : c).ToList();
                Verdade(campos.All(t => !(t.StartsWith("=") || t.StartsWith("+") || t.StartsWith("@") || (t.StartsWith("-") && t.Length > 1)) || t == "=\"" + chaveA + "\"") &&
                    campos[2].StartsWith("'=") && campos[6].StartsWith("'@") && campos[7] == "'=1+1" && campos[18].StartsWith("'+") && campos[13] == "'-2+3",
                    "CSV: campos com = + - @ saem com apóstrofo; só a chave numérica fica como fórmula de texto");
                var lido = Csv.Dividir(File.ReadAllLines(csv)[1]);
                Verdade(lido[2] == "=2+5+cmd|'/C calc'!A1" && lido[6] == "@SUM(1)" && lido[7] == "=1+1" && Csv.SemFormula(lido[5]) == chaveA && lido[16] == "-",
                    "CSV: ao reler, o apóstrofo some e o texto volta igual");
            }

            // ---------- ini: UrlConsulta, validação TLS e nome do banco ----------
            {
                Func<string, string> url = u => { var c = Config.Carregar(ini); c.UrlConsulta = u; try { return ClienteSefaz.UrlConsulta(c); } catch (Exception ex) { return "ERRO " + ex.Message; } };
                Verdade(url("http://servidor-falso.exemplo/nfce").StartsWith("ERRO") && url("https://sefaz.falsa.com.br/x").StartsWith("ERRO") &&
                    url("http://nfce.fazenda.mg.gov.br/x").StartsWith("ERRO") && url("https://evil.gov.br.exemplo.com/x").StartsWith("ERRO"),
                    "UrlConsulta fora de https *.gov.br / 127.0.0.1 é recusada");
                Verdade(url("https://nfce.fazenda.mg.gov.br/nfce/services/NFeConsultaProtocolo4").StartsWith("https") && url("http://127.0.0.1:9/x").StartsWith("http"),
                    "UrlConsulta oficial e simulador local aceitos");
                string erroBanco = null;
                try { Config.Carregar(ini).Definir("DbBanco", "chef.nota_saida; DROP TABLE x; --", "teste"); } catch (Exception ex) { erroBanco = ex.Message; }
                Verdade(erroBanco != null && erroBanco.Contains("nome de banco inválido"), "DbBanco com SQL é recusado ao ler o ini");
            }

            // ---------- Resposta da SEFAZ com DTD/entidades é recusada ----------
            {
                var interpretar = typeof(ClienteSefaz).GetMethod("Interpretar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var resp = new RespostaSefaz { XmlBruto = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaa\"><!ENTITY b \"&a;&a;&a;\">]><retConsSitNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"><cStat>&b;</cStat></retConsSitNFe>" };
                string erro = null;
                try { interpretar.Invoke(null, new object[] { resp, autA }); } catch (Exception ex) { erro = (ex.InnerException ?? ex).Message; }
                Verdade(erro != null && erro.Contains("DTD"), "resposta da SEFAZ com DTD é recusada (" + erro + ")");
            }

            Console.WriteLine(falhas == 0 ? "TODOS OS TESTES PASSARAM" : falhas + " FALHA(S)");
            return falhas == 0 ? 0 : 1;
        }
    }
}
