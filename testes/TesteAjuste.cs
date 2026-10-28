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

            // ---------- Regras da chave ----------
            Verdade(AjusteChave.DvValido(autA) && AjusteChave.DvValido(chaveA), "DV (módulo 11) das duas chaves do caso A válido");
            Verdade(!AjusteChave.DvValido(autA.Substring(0, 43) + ((autA[43] - '0' + 1) % 10)), "DV errado é recusado");
            Verdade(AjusteChave.MesmaNumeracao(chaveA, autA), "caso A: mesmos UF, AAMM, CNPJ, modelo, série e número");
            Verdade(!AjusteChave.MesmaNumeracao(chaveA, autA.Substring(0, 33) + "9" + autA.Substring(34)), "número diferente é recusado");

            // ---------- Processamento ----------
            var m = NovoMotor(ini, trabalho);
            Verdade(m.Cfg.OrigemConexaoBanco != null && m.Cfg.DbPorta.ToString() == Environment.GetEnvironmentVariable("TESTE_PORTA"), "host/porta lidos do Conexao.ini (" + m.Cfg.DbHost + ":" + m.Cfg.DbPorta + ")");
            var linhas = Processar(m);
            Func<string, LinhaRelatorio> nota = n => linhas.First(l => l.Numero == n);
            var A = linhas.First(l => l.ChaveSistema == chaveA);
            Verdade(A.Status == Status.Pendente && A.Ajuste != null && A.Ajuste.PodeConferirPortal, "A sem XML da chave autorizada: PENDENTE com conferência no portal (" + A.Motivo + ")");
            Verdade(A.ValorSefaz == null && AjusteChave.Trava(A) != null, "A: sem valor da SEFAZ e ajuste travado");
            Verdade(A.Sel.Sefaz.Prot.CStat == "150", "A: protocolo cStat 150 (fora de prazo) aceito como autorizado");

            var B = nota("2002"); var C = nota("2003"); var D = nota("2004"); var E = nota("2005");
            Verdade(B.Status == Status.AjusteChave && B.Ajuste.OrigemValor.Contains("chefxml.xml"), "B: XML autorizado no banco chefxml confere com digVal -> AJUSTE DE CHAVE (" + B.Motivo + ")");
            Verdade(C.Status == Status.Pendente && C.Ajuste.PodeConferirPortal, "C: XML autorizado só fora das pastas do período não é procurado (rápido) -> conferir no portal");
            Verdade(D.Status == Status.Pendente && D.Motivo.Contains("já está em outra linha"), "D: chave autorizada já em outra linha -> bloqueado (" + D.Motivo + ")");
            Verdade(E.Status == Status.Pendente && E.Motivo.Contains("≠ sistema"), "E: valor autorizado diferente do sistema -> não ajusta (" + E.Motivo + ")");
            var F = nota("2006"); var G = nota("2007");
            Verdade(F.Status == Status.AjusteChave && F.Fonte == null && F.CaminhoDestino == null && AjusteChave.Trava(F) == null,
                "F: ASSINADA autorizado na pasta e MESMO valor -> AJUSTE DE CHAVE, nada para importar (" + F.Motivo + ")");
            Verdade(G.Status == Status.ACopiar && G.Fonte != null && G.CaminhoDestino.Contains("importar-1") && G.Ajuste == null,
                "G: ASSINADA autorizado na pasta e valor DIFERENTE -> fluxo de cópia para importação, como antes (" + G.Motivo + ")");
            var H = nota("2008"); var I = nota("2009");
            Verdade(H.Status == Status.AjusteChave && H.ValorSefaz == 35.90m && H.Fonte == null && H.CaminhoDestino == null && AjusteChave.Trava(H) == null &&
                H.Ajuste.OrigemValor.Contains("sem ASSINADA"),
                "H: só o Temp confere com o digVal, sem ASSINADA, MESMO valor -> AJUSTE DE CHAVE (" + H.Motivo + ")");
            Verdade(I.Status == Status.Pendente && I.ValorSefaz == 15.00m && I.Fonte == null && I.CaminhoDestino == null && (I.Ajuste == null || AjusteChave.Trava(I) != null),
                "I: só o Temp confere, sem ASSINADA, valor DIFERENTE -> PENDENTE com o Valor SEFAZ na tela (" + I.Motivo + ")");
            Verdade(linhas.Where(l => l.Status == Status.AjusteChave).All(l => AjusteChave.Trava(l) == null), "linhas AJUSTE DE CHAVE sem travas");

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
            Verdade(Sql("SELECT COUNT(*) FROM chefxml.xml")[0][0] == "4", "chefxml.xml intocado");
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
            m.Dispose();

            Console.WriteLine(falhas == 0 ? "TODOS OS TESTES PASSARAM" : falhas + " FALHA(S)");
            return falhas == 0 ? 0 : 1;
        }
    }
}
