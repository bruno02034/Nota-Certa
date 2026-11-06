using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DuplicidadeNfce
{
    // Dados do ajuste de chave de uma linha (duplicidade sem nenhum XML da chave autorizada para copiar).
    class InfoAjuste
    {
        public string IdMetre, Numero, Serie, ChaveAtual, ChaveNova;
        public decimal? ValorSistema, ValorConferido;
        public string OrigemValor;        // de onde veio o valor autorizado (XML conferido ou conferência manual)
        public bool ValorManual;          // confirmado pelo usuário no portal da SEFAZ
        public bool PodeConferirPortal;   // nenhum XML com a chave autorizada: só o portal mostra o valor
        public string ChaveAntiga, Backup; // depois do ajuste (CHAVE AJUSTADA): para desfazer
        public string IdOutraLinha, Pedido; // LINHA DUPLICADA: a linha que já tem a chave autorizada e o pedido das duas
    }

    // "Ajustar chave no banco": troca SÓ o campo chef.nota_saida.chave_acesso de UMA linha pela chave autorizada na
    // SEFAZ (mesma nota: só mudam cNF e DV). Nada mais é alterado: nem status, nem a coluna XML, nem outras tabelas.
    // Antes de gravar, a linha inteira vai para um backup em arquivo; a gravação é numa transação que só confirma
    // se o UPDATE afetar exatamente 1 linha e a releitura mostrar a chave nova.
    static class AjusteChave
    {
        public const string UrlPortalMG = "https://portalsped.fazenda.mg.gov.br/portalnfce/sistema/consultaarg.xhtml";
        public const string AvisoMetre = "Chave(s) ajustada(s). Feche e abra o Metre para atualizar.";

        public static string SoDigitos(string s, int tamanho)
        {
            if (s == null || s.Length != tamanho || !s.All(c => c >= '0' && c <= '9'))
                throw new Exception("valor inválido (esperados " + tamanho + " dígitos): " + s);
            return s;
        }

        static string Inteiro(string s)
        {
            long n;
            if (s == null || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 0)
                throw new Exception("número inválido: " + s);
            return n.ToString(CultureInfo.InvariantCulture);
        }

        // Dígito verificador da chave (módulo 11, pesos 2 a 9 da direita para a esquerda).
        public static bool DvValido(string chave)
        {
            if (chave == null || chave.Length != 44 || !chave.All(char.IsDigit)) return false;
            int soma = 0, peso = 2;
            for (int i = 42; i >= 0; i--)
            {
                soma += (chave[i] - '0') * peso;
                peso = peso == 9 ? 2 : peso + 1;
            }
            int dv = 11 - soma % 11;
            if (dv >= 10) dv = 0;
            return chave[43] - '0' == dv;
        }

        // UF(2) + AAMM(4) + CNPJ(14) + modelo(2) + série(3) + número(9): a mesma nota.
        public static bool MesmaNumeracao(string a, string b)
        {
            return a != null && b != null && a.Length == 44 && b.Length == 44 && a.Substring(0, 34) == b.Substring(0, 34);
        }

        // Motivo que impede o ajuste da linha; null = pode ajustar (falta só a conferência no banco, feita ao gravar).
        public static string Trava(LinhaRelatorio l)
        {
            var a = l.Ajuste;
            if (a == null) return "linha sem dados de ajuste";
            if (l.Status != Status.AjusteChave) return "status não é " + Status.AjusteChave;
            var s = l.Sel != null ? l.Sel.Sefaz : null;
            if (s == null || !s.Autorizada || s.Prot == null || s.Prot.ChNFe != a.ChaveNova)
                return "a SEFAZ não confirmou a chave autorizada (cStat 100 ou 150; no protocolo, 100, 150 ou 120)";
            if (a.ChaveAtual == null || a.ChaveAtual.Length != 44 || !a.ChaveAtual.All(char.IsDigit)) return "chave do sistema inválida";
            if (!DvValido(a.ChaveNova)) return "chave autorizada sem 44 dígitos ou com DV inválido";
            if (!MesmaNumeracao(a.ChaveAtual, a.ChaveNova)) return "as chaves não têm os mesmos UF, AAMM, CNPJ, modelo, série e número";
            if (!a.ValorSistema.HasValue || !a.ValorConferido.HasValue || a.ValorSistema.Value != a.ValorConferido.Value)
                return "valor do sistema (" + Analisador.Moeda(a.ValorSistema) + ") diferente do autorizado (" + Analisador.Moeda(a.ValorConferido) + ")";
            if (string.IsNullOrEmpty(a.IdMetre)) return "linha sem id_nota_saida do Metre";
            return null;
        }

        public static string Usuario { get { return Environment.UserDomainName + "\\" + Environment.UserName + " em " + Environment.MachineName; } }

        // Grava a chave autorizada. Exceção = nada foi alterado no banco (rollback).
        public static RegistroHistorico Ajustar(Config cfg, LinhaRelatorio l, string pastaBackups, Historico historico, Log log)
        {
            string trava = Trava(l);
            if (trava != null) throw new Exception("ajuste bloqueado: " + trava);
            var a = l.Ajuste;
            var r = Trocar(cfg, "ajuste", Inteiro(a.IdMetre), Inteiro(l.Numero), Inteiro(l.Serie), a.ChaveAtual, a.ChaveNova, a.ValorSistema,
                pastaBackups, log, a.OrigemValor);
            r.Status = Status.ChaveAjustada;
            historico.Registrar(r);
            return r;
        }

        // Ajuste em massa: cada linha passa pelo Ajustar de sempre (travas, backup e transação próprios); a falha de uma
        // não desfaz nem interrompe as outras. Resultado na ordem das linhas: (registro, null) ajustada ou (null, erro).
        public static List<Tuple<RegistroHistorico, string>> AjustarLote(Config cfg, IList<LinhaRelatorio> linhas, string pastaBackups,
            Historico historico, Log log, Action<int, int> progresso)
        {
            var res = new List<Tuple<RegistroHistorico, string>>();
            log.Info("");
            log.Info("Ajuste de chave em massa: " + linhas.Count + " nota(s)");
            for (int i = 0; i < linhas.Count; i++)
            {
                if (progresso != null) progresso(i, linhas.Count);
                try { res.Add(Tuple.Create(Ajustar(cfg, linhas[i], pastaBackups, historico, log), (string)null)); }
                catch (Exception ex) { res.Add(Tuple.Create((RegistroHistorico)null, ex.Message)); }
            }
            log.Info("Ajuste em massa: " + res.Count(r => r.Item2 == null) + " ajustada(s), " + res.Count(r => r.Item2 != null) + " com erro");
            return res;
        }

        // Volta a chave antiga gravada no backup do ajuste, com as mesmas travas.
        public static RegistroHistorico Desfazer(Config cfg, string arqBackup, string pastaBackups, Historico historico, Log log)
        {
            var b = LerBackup(arqBackup);
            string id = Inteiro(Campo(b, "id_nota_saida")), numero = Inteiro(Campo(b, "numero")), serie = Inteiro(Campo(b, "serie"));
            string antiga = SoDigitos(Campo(b, "chave_antiga"), 44), nova = SoDigitos(Campo(b, "chave_nova"), 44);
            if (Campo(b, "operacao") != "ajuste") throw new Exception("o arquivo não é backup de um ajuste: " + arqBackup);
            if (!MesmaNumeracao(antiga, nova)) throw new Exception("as chaves do backup não são da mesma nota");
            var r = Trocar(cfg, "desfazer", id, numero, serie, nova, antiga, null, pastaBackups, log, "desfazendo o ajuste do backup " + arqBackup);
            r.Status = Status.AjusteDesfeito;
            // No histórico a chave autorizada fica na mesma coluna do ajuste, para achar o par.
            r.ChaveAutorizada = nova;
            r.CaminhoOrigem = antiga;
            historico.Registrar(r);
            return r;
        }

        static RegistroHistorico Trocar(Config cfg, string operacao, string id, string numero, string serie, string atual, string nova,
            decimal? valorEsperado, string pastaBackups, Log log, string observacao)
        {
            SoDigitos(atual, 44);
            SoDigitos(nova, 44);
            if (!DvValido(nova)) throw new Exception("chave " + nova + " com DV inválido");
            if (!MesmaNumeracao(atual, nova)) throw new Exception("as chaves não são da mesma nota");
            string tabela = cfg.DbBanco + ".nota_saida";
            string onde = "id_nota_saida = " + id + " AND numero = " + numero + " AND serie = " + serie;
            log.Info("");
            log.Info("Ajuste de chave (" + operacao + "): " + tabela + " id_nota_saida " + id + ", nota " + serie + "/" + numero + ": " + atual + " -> " + nova);
            using (var s = Banco.Sessao.Abrir(cfg, x => log.Info("Banco: " + x), false))
            {
                string backup = null;
                EmTransacao(s, log, () =>
                {
                    var linha = LinhaTravada(s, tabela, id, numero, serie);
                    Func<string, string> col = nome => Coluna(linha, tabela, nome);
                    if (col("chave_acesso") != atual)
                        throw new Exception("a chave_acesso no banco mudou: está " + (col("chave_acesso") ?? "NULL") + ", esperada " + atual);
                    // Em 03/11 um desfazer depois que o Metre autorizou deixou a nota autorizada com a chave errada.
                    if (SemAcento(col("situacao")) != SemAcento(cfg.DbSituacao))
                        throw new Exception("a nota não está mais em contingência (situação: " + (col("situacao") ?? "NULL") + "); trocar a chave agora " +
                            (operacao == "desfazer" ? "deixaria a nota autorizada com a chave errada" : "não é necessário"));
                    if (valorEsperado.HasValue)
                    {
                        decimal? v = NotaXml.Decimal(col("valor_total_nota"));
                        if (v != valorEsperado) throw new Exception("valor_total_nota no banco (" + Analisador.Moeda(v) + ") diferente do conferido (" + Analisador.Moeda(valorEsperado) + ")");
                    }
                    var outras = Banco.IdsComChave(s, cfg, nova);
                    if (outras.Count > 0) throw new Exception("a chave " + nova + " já está em outra linha de " + tabela + " (id_nota_saida " + string.Join(", ", outras) + ")");

                    backup = GravarBackup(pastaBackups, operacao, tabela, id, numero, serie, atual, nova, linha, observacao);
                    log.Info("  backup da linha: " + backup);

                    long n = s.Comando("UPDATE " + tabela + " SET chave_acesso = '" + nova + "' WHERE " + onde + " AND chave_acesso = '" + atual + "'");
                    if (n != 1) throw new Exception("o UPDATE afetou " + n + " linha(s), esperada exatamente 1");
                    var relida = s.Consultar("SELECT chave_acesso FROM " + tabela + " WHERE id_nota_saida = " + id);
                    if (relida.Count != 1 || relida[0][0] != nova) throw new Exception("a releitura não mostrou a chave nova");
                });
                // Confirmação fora da transação: o que ficou gravado.
                var gravada = s.Consultar("SELECT chave_acesso FROM " + tabela + " WHERE id_nota_saida = " + id);
                if (gravada.Count != 1 || gravada[0][0] != nova)
                    throw new Exception("COMMIT feito, mas a releitura mostra " + (gravada.Count == 1 ? gravada[0][0] : "nenhuma linha") + "; conferir no banco (backup " + backup + ")");
                log.Info("  gravado e relido: chave_acesso = " + nova);
                return new RegistroHistorico
                {
                    DataHora = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"), Serie = serie, Numero = numero, ChaveAutorizada = nova,
                    Arquivo = tabela + " id_nota_saida=" + id, CaminhoOrigem = atual, CaminhoDestino = backup,
                    Tamanho = valorEsperado.HasValue ? valorEsperado.Value.ToString("0.00", CultureInfo.InvariantCulture) : "", Sha256 = ""
                };
            }
        }

        // ---------- Contingência 2 → contingência normal ----------
        // Troca SÓ o campo situacao de UMA linha de "Em Contigência 2" (e variações) para o texto que o Metre usa na
        // contingência normal; a nota volta à aba Contingência do Metre. Mesmas proteções do ajuste: linha travada,
        // conferência do que foi listado, backup, UPDATE exigindo 1 linha e releitura. Exceção = nada alterado.
        public const string SituacaoMetre = "Em Contigência";

        public static string VoltarParaContingencia(Config cfg, LinhaContingencia c, string pastaBackups, Log log)
        {
            string id = Inteiro(c.Id), numero = Inteiro(c.Numero), serie = Inteiro(c.Serie);
            if (!Banco.EhContingencia2(c.Situacao)) throw new Exception("a situação \"" + c.Situacao + "\" não é de Contingência 2");
            string tabela = cfg.DbBanco + ".nota_saida";
            string onde = "id_nota_saida = " + id + " AND numero = " + numero + " AND serie = " + serie;
            log.Info("");
            log.Info("Voltar para a contingência: " + tabela + " id_nota_saida " + id + ", nota " + serie + "/" + numero + " (" + c.Situacao + ")");
            using (var s = Banco.Sessao.Abrir(cfg, x => log.Info("Banco: " + x), false))
            {
                // O texto exato que o Metre grava hoje na contingência normal; sem nenhuma nota lá, o visto no Metre.
                var usada = s.Consultar("SELECT situacao FROM " + tabela + " WHERE situacao = '" + Banco.Sql(cfg.DbSituacao) + "' LIMIT 1");
                string nova = usada.Count == 1 && usada[0][0] != null ? usada[0][0] : SituacaoMetre;
                if (SemAcento(nova) != SemAcento(cfg.DbSituacao)) throw new Exception("situação de destino \"" + nova + "\" não confere com DbSituacao");
                string backup = null;
                EmTransacao(s, log, () =>
                {
                    var linha = LinhaTravada(s, tabela, id, numero, serie);
                    Func<string, string> col = nome => Coluna(linha, tabela, nome);
                    if (col("situacao") != c.Situacao)
                        throw new Exception("a situação no banco mudou desde a listagem: está \"" + (col("situacao") ?? "NULL") + "\", listada \"" + c.Situacao + "\"");
                    if (col("chave_acesso") != c.Chave)
                        throw new Exception("a chave_acesso no banco mudou desde a listagem: está " + (col("chave_acesso") ?? "NULL"));

                    backup = GravarBackup(pastaBackups, "voltar_contingencia", tabela, id, numero, serie, c.Chave ?? "", c.Chave ?? "", linha,
                        "situacao: " + c.Situacao + " -> " + nova);
                    log.Info("  backup da linha: " + backup);

                    long n = s.Comando("UPDATE " + tabela + " SET situacao = '" + Banco.Sql(nova) + "' WHERE " + onde + " AND situacao = '" + Banco.Sql(c.Situacao) + "'" +
                        " AND chave_acesso " + (c.Chave == null ? "IS NULL" : "= '" + Banco.Sql(c.Chave) + "'"));
                    if (n != 1) throw new Exception("o UPDATE afetou " + n + " linha(s), esperada exatamente 1");
                    var relida = s.Consultar("SELECT situacao FROM " + tabela + " WHERE id_nota_saida = " + id);
                    if (relida.Count != 1 || relida[0][0] != nova) throw new Exception("a releitura não mostrou a situação nova");
                });
                log.Info("  gravado e relido: situacao = " + nova);
                return backup;
            }
        }

        // ---------- Linha duplicada: a mesma venda já está autorizada em outra linha ----------
        // Faz o que o suporte faz à mão (06/11): num_pedido = NULL na linha da contingência (desvincula do pedido), apaga os
        // itens dela em nota_saida_item e depois a linha. Só com a outra linha tendo a chave autorizada e a mesma série,
        // número, pedido e valor, conferidos de novo dentro da transação; backup da linha e dos itens (com os INSERT para
        // restaurar) antes de apagar. Qualquer outra tabela que aponte para a linha bloqueia. Exceção = nada alterado.
        public static string TravaExclusao(LinhaRelatorio l)
        {
            var a = l.Ajuste;
            if (a == null || l.Status != Status.LinhaDuplicada) return "status não é " + Status.LinhaDuplicada;
            var s = l.Sel != null ? l.Sel.Sefaz : null;
            if (s == null || !s.Autorizada || s.Prot == null || s.Prot.ChNFe != a.ChaveNova) return "a SEFAZ não confirmou a chave autorizada";
            if (string.IsNullOrEmpty(a.IdMetre) || string.IsNullOrEmpty(a.IdOutraLinha) || a.IdMetre == a.IdOutraLinha) return "linhas do banco não identificadas";
            if (string.IsNullOrEmpty(a.Pedido) || !a.ValorSistema.HasValue) return "sem pedido ou valor para conferir";
            if (a.ChaveAtual == a.ChaveNova) return "esta linha já tem a chave autorizada";
            return null;
        }

        public static RegistroHistorico ExcluirDuplicada(Config cfg, LinhaRelatorio l, string pastaBackups, Historico historico, Log log)
        {
            string trava = TravaExclusao(l);
            if (trava != null) throw new Exception("exclusão bloqueada: " + trava);
            var a = l.Ajuste;
            string id = Inteiro(a.IdMetre), outra = Inteiro(a.IdOutraLinha), numero = Inteiro(l.Numero), serie = Inteiro(l.Serie), pedido = Inteiro(a.Pedido);
            string atual = SoDigitos(a.ChaveAtual, 44), autorizada = SoDigitos(a.ChaveNova, 44);
            string tabela = cfg.DbBanco + ".nota_saida", itens = cfg.DbBanco + ".nota_saida_item";
            log.Info("");
            log.Info("Excluir linha duplicada: " + tabela + " id_nota_saida " + id + ", nota " + serie + "/" + numero + " (chave " + atual +
                "); a chave autorizada " + autorizada + " está em id_nota_saida " + outra);
            using (var s = Banco.Sessao.Abrir(cfg, x => log.Info("Banco: " + x), false))
            {
                string backup = null;
                EmTransacao(s, log, () =>
                {
                    var linha = s.LinhaCompleta("SELECT * FROM " + tabela + " WHERE id_nota_saida = " + id + " FOR UPDATE");
                    if (linha == null) throw new Exception("a linha id_nota_saida " + id + " não existe mais em " + tabela);
                    var lOutra = s.LinhaCompleta("SELECT * FROM " + tabela + " WHERE id_nota_saida = " + outra + " FOR UPDATE");
                    if (lOutra == null) throw new Exception("a linha com a chave autorizada (id_nota_saida " + outra + ") não existe mais");
                    Func<List<KeyValuePair<string, byte[]>>, string, string> col = (ln, nome) => Coluna(ln, tabela, nome);
                    foreach (var par in new[] { Tuple.Create(linha, id, atual), Tuple.Create(lOutra, outra, autorizada) })
                    {
                        var ln = par.Item1;
                        ConferirNota(ln, tabela, par.Item2, numero, serie);
                        if (col(ln, "chave_acesso") != par.Item3)
                            throw new Exception("a chave_acesso da linha id_nota_saida " + par.Item2 + " mudou: está " + (col(ln, "chave_acesso") ?? "NULL") + ", esperada " + par.Item3);
                        if (col(ln, "num_pedido") != pedido)
                            throw new Exception("o pedido da linha id_nota_saida " + par.Item2 + " é " + (col(ln, "num_pedido") ?? "vazio") + ", esperado " + pedido);
                        decimal? valor = NotaXml.Decimal(col(ln, "valor_total_nota"));
                        if (valor != a.ValorSistema)
                            throw new Exception("o valor da linha id_nota_saida " + par.Item2 + " é " + Analisador.Moeda(valor) + ", esperado " + Analisador.Moeda(a.ValorSistema));
                    }
                    if (SemAcento(col(linha, "situacao")) != SemAcento(cfg.DbSituacao))
                        throw new Exception("a linha id_nota_saida " + id + " não está mais em contingência (situação: " + (col(linha, "situacao") ?? "NULL") + ")");

                    // Só nota_saida_item pode apontar para a linha; outra tabela com referência a ela bloqueia.
                    foreach (var fk in s.Consultar("SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE WHERE REFERENCED_TABLE_SCHEMA = '" +
                        Banco.Sql(cfg.DbBanco) + "' AND REFERENCED_TABLE_NAME = 'nota_saida'"))
                    {
                        if (fk[0].Equals(cfg.DbBanco, StringComparison.OrdinalIgnoreCase) && fk[1].Equals("nota_saida_item", StringComparison.OrdinalIgnoreCase)) continue;
                        string n = s.Consultar("SELECT COUNT(*) FROM `" + fk[0] + "`.`" + fk[1] + "` WHERE `" + fk[2] + "` = " + id)[0][0];
                        if (n != "0") throw new Exception("a linha é usada em " + fk[0] + "." + fk[1] + " (" + n + " linha(s)); exclusão bloqueada");
                    }
                    var linhasItens = s.LinhasCompletas("SELECT * FROM " + itens + " WHERE id_nota_saida = " + id + " FOR UPDATE");

                    backup = GravarBackup(pastaBackups, "excluir_duplicada", tabela, id, numero, serie, atual, autorizada, linha,
                        "linha duplicada: a chave autorizada está em id_nota_saida " + outra + " (mesmo pedido " + pedido + " e valor R$ " +
                        Analisador.Moeda(a.ValorSistema) + "); num_pedido = NULL, " + linhasItens.Count + " item(ns) e a linha apagados", itens, linhasItens);
                    log.Info("  backup da linha e de " + linhasItens.Count + " item(ns): " + backup);

                    string onde = "id_nota_saida = " + id + " AND numero = " + numero + " AND serie = " + serie + " AND chave_acesso = '" + atual + "'";
                    long n1 = s.Comando("UPDATE " + tabela + " SET num_pedido = NULL WHERE " + onde + " AND num_pedido = " + pedido);
                    if (n1 != 1) throw new Exception("o UPDATE do num_pedido afetou " + n1 + " linha(s), esperada exatamente 1");
                    long n2 = s.Comando("DELETE FROM " + itens + " WHERE id_nota_saida = " + id);
                    if (n2 != linhasItens.Count) throw new Exception("o DELETE dos itens afetou " + n2 + " linha(s), esperadas " + linhasItens.Count);
                    long n3 = s.Comando("DELETE FROM " + tabela + " WHERE " + onde + " AND num_pedido IS NULL");
                    if (n3 != 1) throw new Exception("o DELETE da linha afetou " + n3 + " linha(s), esperada exatamente 1");
                    if (s.Consultar("SELECT COUNT(*) FROM " + tabela + " WHERE id_nota_saida = " + id)[0][0] != "0") throw new Exception("a releitura ainda mostra a linha");
                    var ok = s.Consultar("SELECT chave_acesso FROM " + tabela + " WHERE id_nota_saida = " + outra);
                    if (ok.Count != 1 || ok[0][0] != autorizada) throw new Exception("a linha com a chave autorizada não está mais como antes");
                });
                if (s.Consultar("SELECT COUNT(*) FROM " + tabela + " WHERE id_nota_saida = " + id)[0][0] != "0")
                    throw new Exception("COMMIT feito, mas a linha id_nota_saida " + id + " ainda aparece; conferir no banco (backup " + backup + ")");
                log.Info("  excluída e conferida");
                var r = new RegistroHistorico
                {
                    DataHora = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"), Serie = serie, Numero = numero, ChaveAutorizada = autorizada,
                    Arquivo = tabela + " id_nota_saida=" + id + " excluída (autorizada em id_nota_saida=" + outra + ")", CaminhoOrigem = atual,
                    CaminhoDestino = backup, Tamanho = a.ValorSistema.Value.ToString("0.00", CultureInfo.InvariantCulture), Sha256 = "",
                    Status = Status.LinhaExcluida
                };
                historico.Registrar(r);
                return r;
            }
        }

        // START TRANSACTION, corpo e COMMIT; qualquer erro (inclusive no COMMIT) faz ROLLBACK: nada fica alterado.
        static void EmTransacao(Banco.Sessao s, Log log, Action corpo)
        {
            s.Comando("START TRANSACTION");
            try
            {
                corpo();
                s.Comando("COMMIT");
            }
            catch (Exception ex)
            {
                try { s.Comando("ROLLBACK"); } catch { }
                log.Info("  ERRO, desfeito (rollback), nada alterado: " + ex.Message);
                throw new Exception(ex.Message + " (rollback: nada foi alterado no banco)");
            }
        }

        // A linha inteira (SELECT *), travada até o fim da transação, conferida como da nota esperada.
        static List<KeyValuePair<string, byte[]>> LinhaTravada(Banco.Sessao s, string tabela, string id, string numero, string serie)
        {
            var linha = s.LinhaCompleta("SELECT * FROM " + tabela + " WHERE id_nota_saida = " + id + " FOR UPDATE");
            if (linha == null) throw new Exception("a linha id_nota_saida " + id + " não existe mais em " + tabela);
            ConferirNota(linha, tabela, id, numero, serie);
            return linha;
        }

        static void ConferirNota(List<KeyValuePair<string, byte[]>> linha, string tabela, string id, string numero, string serie)
        {
            string n = Coluna(linha, tabela, "numero"), s = Coluna(linha, tabela, "serie");
            if (n != numero || s != serie)
                throw new Exception("a linha id_nota_saida " + id + " é da nota " + s + "/" + n + ", não " + serie + "/" + numero);
        }

        // Valor de uma coluna da linha lida com SELECT * (texto UTF-8; null = NULL).
        static string Coluna(List<KeyValuePair<string, byte[]>> linha, string tabela, string nome)
        {
            var kv = linha.FirstOrDefault(x => x.Key.Equals(nome, StringComparison.OrdinalIgnoreCase));
            if (kv.Key == null) throw new Exception(tabela + " sem a coluna " + nome);
            return kv.Value == null ? null : Encoding.UTF8.GetString(kv.Value);
        }

        // "Em Contigência" (banco) = "Em contigencia" (ini): o MariaDB compara sem acento e sem maiúsculas.
        internal static string SemAcento(string s)
        {
            if (s == null) return "";
            var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        }

        // ---------- Backup em arquivo (pasta backups ao lado do exe) ----------
        static string GravarBackup(string pasta, string operacao, string tabela, string id, string numero, string serie, string atual, string nova,
            List<KeyValuePair<string, byte[]>> linha, string observacao, string tabelaItens = null, List<List<KeyValuePair<string, byte[]>>> itens = null)
        {
            Directory.CreateDirectory(pasta);
            string baseNome = (operacao == "ajuste" ? "ajuste_chave_" : operacao == "desfazer" ? "desfazer_ajuste_" : operacao + "_") + serie + "-" + numero + "_id" + id + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string arq = Path.Combine(pasta, baseNome + ".txt");
            for (int i = 2; File.Exists(arq); i++) arq = Path.Combine(pasta, baseNome + "_" + i + ".txt");
            var sb = new StringBuilder();
            sb.AppendLine("# " + Program.NomeSistema + " - backup da linha de " + tabela + (operacao == "excluir_duplicada" ? " e dos itens antes de EXCLUIR" :
                " antes de alterar " + (operacao == "voltar_contingencia" ? "situacao" : "chave_acesso")));
            sb.AppendLine("# Valores: texto como está no banco; \\N = NULL; 0x... = conteúdo binário ou com quebra de linha, em hexadecimal.");
            sb.AppendLine("operacao=" + operacao);
            sb.AppendLine("data=" + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
            sb.AppendLine("usuario=" + Usuario);
            sb.AppendLine("tabela=" + tabela);
            sb.AppendLine("id_nota_saida=" + id);
            sb.AppendLine("numero=" + numero);
            sb.AppendLine("serie=" + serie);
            sb.AppendLine("chave_antiga=" + atual);
            sb.AppendLine("chave_nova=" + nova);
            sb.AppendLine("observacao=" + (observacao ?? "").Replace("\r", " ").Replace("\n", " "));
            sb.AppendLine("[linha]");
            var estrito = new UTF8Encoding(false, true);
            foreach (var kv in linha)
            {
                string v;
                if (kv.Value == null) v = "\\N";
                else
                {
                    try
                    {
                        v = estrito.GetString(kv.Value);
                        if (v.Any(c => c < ' ' && c != '\t') || v.StartsWith("0x") || v == "\\N") v = null;
                    }
                    catch (DecoderFallbackException) { v = null; }
                    if (v == null) v = "0x" + BitConverter.ToString(kv.Value).Replace("-", "");
                }
                sb.AppendLine(kv.Key + "=" + v);
            }
            if (itens != null)
            {
                for (int i = 0; i < itens.Count; i++)
                {
                    sb.AppendLine("[item " + (i + 1) + "]");
                    foreach (var kv in itens[i]) sb.AppendLine(kv.Key + "=" + (kv.Value == null ? "\\N" : "0x" + BitConverter.ToString(kv.Value).Replace("-", "")));
                }
                // Para restaurar à mão (HeidiSQL): a linha primeiro, depois os itens.
                sb.AppendLine("[restaurar]");
                sb.AppendLine(Insert(tabela, linha) + ";");
                foreach (var it in itens) sb.AppendLine(Insert(tabelaItens, it) + ";");
            }
            sb.AppendLine(LinhaAssinatura + Assinar(pasta, sb.ToString(), true));
            File.WriteAllText(arq, sb.ToString(), new UTF8Encoding(true));
            return arq;
        }

        // Valores como texto entre aspas (o MariaDB converte para o tipo da coluna); conteúdo que não é texto vai em hexadecimal.
        static string Insert(string tabela, List<KeyValuePair<string, byte[]>> linha)
        {
            var estrito = new UTF8Encoding(false, true);
            return "INSERT INTO " + tabela + " (" + string.Join(", ", linha.Select(kv => "`" + kv.Key + "`")) + ") VALUES (" +
                string.Join(", ", linha.Select(kv =>
                {
                    if (kv.Value == null) return "NULL";
                    try { return "'" + Banco.Sql(estrito.GetString(kv.Value)).Replace("\r", "\\r").Replace("\n", "\\n") + "'"; }
                    catch (DecoderFallbackException) { return "0x" + BitConverter.ToString(kv.Value).Replace("-", ""); }
                })) + ")";
        }

        // O "Desfazer" grava no banco o que o backup diz: o arquivo é assinado (HMAC-SHA256) com uma chave aleatória
        // guardada em backups\, criptografada pelo Windows. Backup editado à mão (ou sem assinatura) é recusado.
        const string LinhaAssinatura = "assinatura=";
        const string ArqChaveBackups = "chave_backups.bin";
        static readonly byte[] EntropiaBackups = Encoding.UTF8.GetBytes("NotaCerta/Backups/v1");

        static string Assinar(string pasta, string texto, bool criarChave)
        {
            string arqChave = Path.Combine(pasta, ArqChaveBackups);
            byte[] chave;
            if (File.Exists(arqChave))
                chave = ProtectedData.Unprotect(File.ReadAllBytes(arqChave), EntropiaBackups, DataProtectionScope.LocalMachine);
            else if (criarChave)
            {
                chave = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(chave);
                File.WriteAllBytes(arqChave, ProtectedData.Protect(chave, EntropiaBackups, DataProtectionScope.LocalMachine));
            }
            else throw new Exception("chave de assinatura dos backups não encontrada (" + arqChave + ")");
            using (var h = new HMACSHA256(chave))
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(texto))).Replace("-", "");
        }

        static void ConferirAssinatura(string arq)
        {
            string texto = File.ReadAllText(arq, Encoding.UTF8);
            int p = texto.LastIndexOf("\n" + LinhaAssinatura, StringComparison.Ordinal);
            if (p < 0)
                throw new Exception("backup sem assinatura (feito por uma versão antiga ou editado): o desfazer automático não é seguro. " +
                    "Se precisar, desfaça à mão no banco com a chave_antiga deste arquivo: " + arq);
            string assinado = texto.Substring(0, p + 1), assinatura = texto.Substring(p + 1 + LinhaAssinatura.Length).Trim();
            string esperada;
            try { esperada = Assinar(Path.GetDirectoryName(Path.GetFullPath(arq)), assinado, false); }
            catch (Exception ex) { throw new Exception("não foi possível conferir a assinatura do backup: " + ex.Message.Trim()); }
            if (!string.Equals(assinatura, esperada, StringComparison.OrdinalIgnoreCase))
                throw new Exception("o backup foi alterado depois do ajuste (assinatura não confere); desfazer recusado: " + arq);
        }

        // Só o cabeçalho (antes de [linha]).
        static Dictionary<string, string> LerBackup(string arq)
        {
            if (!File.Exists(arq)) throw new Exception("backup não encontrado: " + arq);
            ConferirAssinatura(arq);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string l in File.ReadAllLines(arq, Encoding.UTF8))
            {
                if (l.Trim() == "[linha]") break;
                if (l.StartsWith("#")) continue;
                int eq = l.IndexOf('=');
                if (eq > 0) d[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
            }
            return d;
        }

        static string Campo(Dictionary<string, string> d, string k)
        {
            string v;
            if (!d.TryGetValue(k, out v) || v == "") throw new Exception("backup sem o campo " + k);
            return v;
        }

        // ---------- Conferência manual do valor no portal da SEFAZ ----------
        public static RegistroHistorico RegistrarConferencia(LinhaRelatorio l, bool confere, Historico historico, Log log)
        {
            var a = l.Ajuste;
            var r = new RegistroHistorico
            {
                DataHora = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"), Serie = l.Serie, Numero = l.Numero, ChaveAutorizada = a.ChaveNova,
                Arquivo = "conferência MANUAL no portal da SEFAZ por " + Usuario,
                CaminhoOrigem = "valor do sistema R$ " + Analisador.Moeda(a.ValorSistema), CaminhoDestino = "",
                // Valor do sistema que foi conferido (em Tamanho, formato invariável, para reler).
                Tamanho = a.ValorSistema.HasValue ? a.ValorSistema.Value.ToString("0.00", CultureInfo.InvariantCulture) : "", Sha256 = "",
                Status = confere ? Status.ValorConfereManual : Status.ValorDiferenteManual
            };
            historico.Registrar(r);
            log.Info("");
            log.Info("Conferência manual no portal da SEFAZ: " + l.Serie + "/" + l.Numero + " chave " + a.ChaveNova + ": " + r.Status +
                " (valor do sistema R$ " + Analisador.Moeda(a.ValorSistema) + ") - " + r.DataHora + " - " + Usuario);
            return r;
        }

        public static decimal? ValorDoRegistro(RegistroHistorico r) { return NotaXml.Decimal(r.Tamanho); }
    }
}
