using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DuplicidadeNfce
{
    enum Classe
    {
        Duplicidade,    // número já autorizado com outra chave (por 539 ou deduzido dos arquivos)
        SemRetorno,     // em contingência, número ainda sem autorização
        OutroErro       // rejeição com outro código (ex.: 537)
    }

    // Uma tentativa do sistema (chave própria) emitida em contingência e ainda não autorizada.
    class Tentativa
    {
        public string Chave, Numero, Serie, Pedido, DhEmi, ChaveAutorizada, NRec, Erro;
        public decimal? Valor;
        public bool Rejeicao539;
        public Classe Classe = Classe.Duplicidade;
        public List<string> Origens = new List<string>();
        public List<string> Avisos = new List<string>();
        public string Nota { get { return Serie + "/" + Numero; } }

        public string Classificacao
        {
            get
            {
                switch (Classe)
                {
                    case Classe.SemRetorno: return "Em contingência sem retorno";
                    case Classe.OutroErro: return "Outro erro";
                    default: return Rejeicao539 ? "Duplicidade (retorno 539)" : "Duplicidade (deduzida pelos arquivos)";
                }
            }
        }
    }

    class LinhaRelatorio
    {
        public string Numero, Serie, Pedido, DhEmiSistema, ChaveSistema, Origem;
        public decimal? ValorSistema, ValorSefaz;
        public string ChaveAutorizada, NRec, SituacaoSefaz, NProtSefaz, DigValSefaz;
        public string ArquivoEscolhido, CaminhoOrigem, CaminhoDestino, Status, Motivo, Classificacao;
        public NotaXml Fonte;   // arquivo autorizado a copiar (só nas linhas "A COPIAR")
        public Selecao Sel;     // decisão sobre os candidatos (null antes de processar)
        public List<string> ArquivosChaveAutorizada = new List<string>();
    }

    class Selecao
    {
        public string ChaveAutorizada;
        public RespostaSefaz Sefaz;
        public NotaXml Escolhido;
        public NotaXml Indicado;        // só pelo protNFe local, quando a SEFAZ não respondeu (nunca é copiado)
        public decimal? ValorSefaz;
        public string MotivoPendente;   // null = escolhido
        public string MotivoEscolha;
        public List<Candidato> Candidatos = new List<Candidato>();
    }

    class Candidato
    {
        public string Caminho, Motivo;
        public bool Aceito;
        public long Tamanho;
        public decimal? VNF;
    }

    class Analisador
    {
        static readonly Regex Re539 = new Regex(@"chNFe:(\d{44})\]\s*\[nRec:(\d*)\]");
        static readonly TipoArquivo[] TiposDescartados =
            { TipoArquivo.Rejeicao, TipoArquivo.FalhaSchema, TipoArquivo.Temp, TipoArquivo.ContingenciaEnvia };
        static readonly TipoArquivo[] PrioridadeValor =
            { TipoArquivo.Rejeicao, TipoArquivo.Assinada, TipoArquivo.ContingenciaEnvia, TipoArquivo.FalhaSchema, TipoArquivo.Temp, TipoArquivo.Outro, TipoArquivo.Autorizada };

        readonly List<NotaXml> notas;
        readonly Func<string, List<NotaXml>, RespostaSefaz> consultar;
        readonly Log log;
        readonly Dictionary<string, List<NotaXml>> porChave;
        readonly Dictionary<string, Selecao> selecoes = new Dictionary<string, Selecao>();
        List<Tentativa> tentativas;
        readonly Dictionary<string, string> autorizadaPorNota = new Dictionary<string, string>();

        public Analisador(List<NotaXml> notas, Func<string, List<NotaXml>, RespostaSefaz> consultar, Log log)
        {
            this.notas = notas;
            this.consultar = consultar;
            this.log = log;
            porChave = notas.Where(n => n.Chave != null).GroupBy(n => n.Chave).ToDictionary(g => g.Key, g => g.ToList());
        }

        public List<Tentativa> Tentativas
        {
            get
            {
                if (tentativas == null) tentativas = LocalizarTentativas();
                return tentativas;
            }
        }

        List<Tentativa> LocalizarTentativas()
        {
            var lista = new Dictionary<string, Tentativa>();
            var autorizadasPorNumero = new Dictionary<string, HashSet<string>>();

            // 0) Chaves autorizadas: XML com protNFe 100/150 da própria chave (o NFCE da pasta Autorizadas).
            var autorizadas = new HashSet<string>();
            foreach (var n in notas.Where(x => x.Chave != null && x.Tipo != TipoArquivo.Rejeicao && x.Prot != null
                && (x.Prot.CStat == "100" || x.Prot.CStat == "150") && x.Prot.ChNFe == x.Chave))
            {
                autorizadas.Add(n.Chave);
                AdicionarAutorizada(autorizadasPorNumero, n.Chave);
                autorizadaPorNota[SerieDaChave(n.Chave) + "/" + NumeroDaChave(n.Chave)] = n.Chave + " (cStat " + n.Prot.CStat + ")";
            }

            // 1) Retornos nos REJEICAO_NOTA (documento principal e restos de gravações anteriores).
            //    O arquivo só existe para parte das rejeições: o Metre guarda a maioria no banco.
            foreach (var rej in notas.Where(n => n.Tipo == TipoArquivo.Rejeicao && n.NaFaixaPrincipal && n.Chave != null))
            {
                if (rej.Prot != null && rej.Prot.CStat == "539")
                    Registrar539(lista, autorizadasPorNumero, rej.Prot, rej, "539 em " + rej.Nome);
                else if (rej.Prot != null && rej.Prot.CStat != null && !autorizadas.Contains(rej.Chave) && !lista.ContainsKey(rej.Chave))
                {
                    var t = new Tentativa { Chave = rej.Chave, Classe = Classe.OutroErro, Erro = rej.Prot.CStat + " - " + rej.Prot.XMotivo };
                    t.Origens.Add("rejeição " + rej.Prot.CStat + " em " + rej.Nome);
                    lista[rej.Chave] = t;
                }
                foreach (var p in rej.ProtsNaSobra.Where(p => p.CStat == "539" && p.ChNFe != null && p.ChNFe != rej.Chave))
                    Registrar539(lista, autorizadasPorNumero, p, rej, "539 em resto de gravação anterior de " + rej.Nome);
            }

            // 2) Tentativas em contingência (tpEmis 9) ainda não autorizadas. Se o número já tem uma
            //    autorização com outra chave, é duplicidade mesmo sem o arquivo de retorno 539.
            foreach (var n in notas.Where(x => x.Chave != null && x.NaFaixaPrincipal && x.TpEmis == "9"
                && !autorizadas.Contains(x.Chave) && !lista.ContainsKey(x.Chave)).OrderBy(x => x.Caminho))
            {
                var t = new Tentativa { Chave = n.Chave };
                HashSet<string> ks;
                if (autorizadasPorNumero.TryGetValue(ChaveNumero(n.Chave), out ks) && !ks.Contains(n.Chave))
                {
                    t.Origens.Add("número já autorizado com outra chave, sem arquivo de retorno 539 (tentativa vista em " + n.Nome + ")");
                    if (ks.Count == 1) t.ChaveAutorizada = ks.First();
                    else t.Avisos.Add("mais de uma chave autorizada para o mesmo número: " + string.Join(", ", ks));
                }
                else
                {
                    t.Classe = Classe.SemRetorno;
                    t.Origens.Add("em contingência, número ainda sem autorização (vista em " + n.Nome + ")");
                }
                lista[n.Chave] = t;
            }
            log.Info("Chaves autorizadas nos arquivos: " + autorizadas.Count + "; tentativas em contingência não autorizadas: " + lista.Count +
                " (" + string.Join(", ", lista.Values.GroupBy(t => t.Classificacao).Select(g => g.Count() + " " + g.Key)) + ")");

            // FALHA_SCHEMA não traz código nem motivo; só registra que o envio falhou na validação.
            foreach (var f in notas.Where(x => x.Tipo == TipoArquivo.FalhaSchema && x.Chave != null))
            {
                Tentativa t;
                if (lista.TryGetValue(f.Chave, out t)) t.Origens.Add("envio com FALHA_SCHEMA (" + f.Nome + ")");
            }

            // 3) Dados de cada tentativa vindos dos arquivos com a mesma chave.
            foreach (var t in lista.Values)
            {
                List<NotaXml> arqs;
                if (!porChave.TryGetValue(t.Chave, out arqs)) arqs = new List<NotaXml>();
                var ordenados = arqs.OrderBy(a => Array.IndexOf(PrioridadeValor, a.Tipo)).ThenBy(a => a.Caminho).ToList();
                var comValor = ordenados.Where(a => a.VNF.HasValue).ToList();
                if (comValor.Count > 0)
                {
                    t.Valor = comValor[0].VNF;
                    var distintos = comValor.Select(a => a.VNF.Value).Distinct().ToList();
                    if (distintos.Count > 1)
                        t.Avisos.Add("valores diferentes entre arquivos da mesma chave: " +
                            string.Join(", ", comValor.Select(a => a.Nome + "=" + Moeda(a.VNF))));
                }
                var comPedido = ordenados.FirstOrDefault(a => !string.IsNullOrEmpty(a.Pedido));
                if (comPedido != null) t.Pedido = comPedido.Pedido;
                var comData = ordenados.FirstOrDefault(a => a.DhEmi != null);
                if (comData != null) t.DhEmi = comData.DhEmi;
                t.Numero = NumeroDaChave(t.Chave);
                t.Serie = SerieDaChave(t.Chave);
            }

            return lista.Values.OrderBy(t => int.Parse(t.Serie)).ThenBy(t => long.Parse(t.Numero)).ThenBy(t => t.DhEmi).ToList();
        }

        void Registrar539(Dictionary<string, Tentativa> lista, Dictionary<string, HashSet<string>> autPorNum,
            ProtocoloXml p, NotaXml arq, string origem)
        {
            var m = Re539.Match(p.XMotivo ?? "");
            string chaveSistema = p.ChNFe ?? arq.Chave;
            Tentativa t;
            if (!lista.TryGetValue(chaveSistema, out t))
            {
                t = new Tentativa { Chave = chaveSistema };
                lista[chaveSistema] = t;
            }
            t.Rejeicao539 = true;
            t.Origens.Add(origem);
            if (!m.Success)
            {
                t.Avisos.Add("cStat 539 sem chave autorizada legível no xMotivo: " + p.XMotivo);
                return;
            }
            string k = m.Groups[1].Value;
            if (t.ChaveAutorizada != null && t.ChaveAutorizada != k)
                t.Avisos.Add("retornos 539 com chaves autorizadas diferentes: " + t.ChaveAutorizada + " / " + k);
            t.ChaveAutorizada = t.ChaveAutorizada ?? k;
            t.NRec = t.NRec ?? m.Groups[2].Value;

            AdicionarAutorizada(autPorNum, k);
        }

        static void AdicionarAutorizada(Dictionary<string, HashSet<string>> autPorNum, string chave)
        {
            string num = ChaveNumero(chave);
            HashSet<string> ks;
            if (!autPorNum.TryGetValue(num, out ks)) autPorNum[num] = ks = new HashSet<string>();
            ks.Add(chave);
        }

        // Escolhe o arquivo da nota autorizada. Qualquer dúvida → MotivoPendente preenchido.
        public Selecao Selecionar(string k)
        {
            Selecao sel;
            if (selecoes.TryGetValue(k, out sel)) return sel;
            sel = new Selecao { ChaveAutorizada = k };
            selecoes[k] = sel;

            List<NotaXml> arqs;
            if (!porChave.TryGetValue(k, out arqs)) arqs = new List<NotaXml>();
            log.Info("");
            log.Info("Chave autorizada " + k + ": " + arqs.Count + " arquivo(s) com essa chave no Id do infNFe");

            sel.Sefaz = consultar(k, arqs);
            string motivo;
            int copias;
            if (!sel.Sefaz.Ok)
            {
                motivo = (sel.Sefaz.NaoConsultada ? "SEFAZ não consultada: " : "SEFAZ sem resposta: ") + sel.Sefaz.Erro;
                log.Info("  " + motivo);
                // Sem a SEFAZ nada é escolhido; o protNFe autorizado gravado localmente só serve de indicação no relatório.
                var local = arqs.FirstOrDefault(a => a.Prot != null && a.Prot.ChNFe == k && (a.Prot.CStat == "100" || a.Prot.CStat == "150"));
                if (local != null)
                {
                    log.Info("  Indicação pelo protNFe local de " + local.Nome + " (sem confirmação da SEFAZ):");
                    string m;
                    sel.Indicado = Escolher(arqs, local.Prot, "protNFe local", sel, out m, out copias);
                    motivo += sel.Indicado != null
                        ? "; arquivo indicado pelo protNFe local, sem confirmação da SEFAZ: " + sel.Indicado.Nome + " (valor no XML " + Moeda(sel.Indicado.VNF) + ")"
                        : "; protNFe local também não indica arquivo: " + m;
                }
                sel.MotivoPendente = motivo;
                return sel;
            }
            log.Info("  SEFAZ: " + sel.Sefaz.Situacao + (sel.Sefaz.Prot != null ? " | nProt " + sel.Sefaz.Prot.NProt + " | digVal " + sel.Sefaz.Prot.DigVal : ""));
            if (!sel.Sefaz.Autorizada) { sel.MotivoPendente = "situação na SEFAZ não é autorizada: " + sel.Sefaz.Situacao; return sel; }
            if (sel.Sefaz.Prot.ChNFe != k) { sel.MotivoPendente = "SEFAZ devolveu protocolo de outra chave: " + sel.Sefaz.Prot.ChNFe; return sel; }

            var escolhido = Escolher(arqs, sel.Sefaz.Prot, "SEFAZ", sel, out motivo, out copias);
            if (escolhido == null) { sel.MotivoPendente = motivo; return sel; }
            sel.Escolhido = escolhido;
            sel.ValorSefaz = escolhido.VNF;
            sel.MotivoEscolha = "chave autorizada no Id, DigestValue = digVal SEFAZ (conferido pelo conteúdo)" +
                (escolhido.Prot != null ? ", protNFe " + escolhido.Prot.CStat + " nProt " + escolhido.Prot.NProt : "") +
                (copias > 1 ? "; " + copias + " arquivos com o conteúdo autorizado, escolhido " + sel.Escolhido.Nome : "");
            log.Info("  Escolhido: " + escolhido.Caminho);
            return sel;
        }

        // Único arquivo cujo conteúdo é o autorizado no protocolo 'prot'; null com o motivo em caso de dúvida.
        NotaXml Escolher(List<NotaXml> arqs, ProtocoloXml prot, string fonte, Selecao sel, out string motivoPendente, out int copias)
        {
            motivoPendente = null;
            copias = 0;
            string digVal = prot.DigVal;
            if (string.IsNullOrEmpty(digVal)) { motivoPendente = fonte + " sem digVal"; return null; }

            var validos = new List<NotaXml>();
            sel.Candidatos.Clear();
            foreach (var a in arqs.OrderBy(x => x.Caminho))
            {
                string motivo = null;
                if (TiposDescartados.Contains(a.Tipo)) motivo = "descartado pelo tipo (" + a.Tipo + ")";
                else if (a.Erro != null) motivo = "erro de leitura: " + a.Erro;
                else if (a.DigestDeclarado != digVal) motivo = "DigestValue " + a.DigestDeclarado + " ≠ digVal " + fonte;
                else if (a.DigestCalculado != a.DigestDeclarado) motivo = "DigestValue declarado não confere com o conteúdo (calculado " + a.DigestCalculado + ")";
                else if (a.Prot != null && a.Prot.NProt != null && a.Prot.NProt != prot.NProt) motivo = "protNFe local com nProt " + a.Prot.NProt + " ≠ " + fonte + " " + prot.NProt;
                if (motivo == null) validos.Add(a);
                sel.Candidatos.Add(new Candidato
                {
                    Caminho = a.Caminho, Aceito = motivo == null, Tamanho = a.Tamanho, VNF = a.VNF,
                    Motivo = motivo ?? "chave autorizada no Id e DigestValue = digVal " + fonte + " (conferido pelo conteúdo)"
                });
                log.Info("  " + (motivo == null ? "[OK]  " : "[X]   ") + a.Caminho + " (" + a.Tamanho + " bytes, vNF " + Moeda(a.VNF) + ")" + (motivo == null ? "" : " - " + motivo));
            }

            if (validos.Count == 0)
            {
                motivoPendente = arqs.Count == 0
                    ? "nenhum arquivo com a chave autorizada"
                    : "nenhum dos " + arqs.Count + " arquivo(s) com a chave tem DigestValue igual ao digVal " + fonte;
                return null;
            }
            // Regra do Bruno: só se copia o XML AUTORIZADO (o _NFCE da pasta Autorizadas, com protNFe 100/150
            // e o mesmo nProt da consulta), com o conteúdo e o valor que a SEFAZ autorizou. O ASSINADA nunca
            // é copiado, mesmo quando tem o mesmo conteúdo.
            var preferidos = validos.Where(a => a.Tipo == TipoArquivo.Autorizada && a.Prot != null
                && (a.Prot.CStat == "100" || a.Prot.CStat == "150")
                && (string.IsNullOrEmpty(prot.NProt) || a.Prot.NProt == prot.NProt)).ToList();
            if (preferidos.Count == 0)
            {
                motivoPendente = "nenhum XML autorizado (_NFCE da pasta Autorizadas) com o conteúdo da SEFAZ; " +
                    "o ASSINADA não é copiado (" + string.Join(" | ", validos.Select(a => a.Nome)) + ")";
                return null;
            }
            var semSobra = preferidos.Where(a => !a.TemSobra).ToList();
            if (semSobra.Count == 0)
            {
                motivoPendente = "arquivo válido tem conteúdo residual após </nfeProc>: " + preferidos[0].Caminho;
                return null;
            }
            if (semSobra.GroupBy(a => a.Sha256).Count() > 1)
            {
                motivoPendente = "mais de um arquivo válido com conteúdo diferente: " + string.Join(" | ", semSobra.Select(a => a.Caminho));
                return null;
            }
            var escolhido = semSobra.OrderBy(a => a.Caminho.IndexOf(@"\Autorizadas\", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ThenBy(a => a.Caminho).First();
            copias = validos.Count;
            foreach (var c in sel.Candidatos.Where(c => c.Aceito && c.Caminho != escolhido.Caminho))
            {
                c.Aceito = false;
                c.Motivo = "mesmo conteúdo autorizado (digest confere), mas preterido: o escolhido é " + Path.GetFileName(escolhido.Caminho);
            }
            return escolhido;
        }

        LinhaRelatorio NovaLinha(Tentativa t)
        {
            var l = new LinhaRelatorio
            {
                Numero = t.Numero, Serie = t.Serie, Pedido = t.Pedido, DhEmiSistema = t.DhEmi,
                ValorSistema = t.Valor, ChaveSistema = t.Chave, ChaveAutorizada = t.ChaveAutorizada,
                NRec = t.NRec, Origem = string.Join("; ", t.Origens), Classificacao = t.Classificacao
            };
            List<NotaXml> arqs;
            if (t.ChaveAutorizada != null && porChave.TryGetValue(t.ChaveAutorizada, out arqs))
                l.ArquivosChaveAutorizada = arqs.Select(a => a.Caminho).OrderBy(c => c).ToList();
            return l;
        }

        // Sem retorno ou com outro erro: nada a consultar nem copiar.
        static bool ForaDoFluxo(Tentativa t, LinhaRelatorio l)
        {
            if (t.Classe == Classe.SemRetorno)
            {
                l.Status = Status.EmContingencia;
                l.Motivo = "número ainda sem autorização nos arquivos; aguardando transmissão da contingência";
                return true;
            }
            if (t.Classe == Classe.OutroErro)
            {
                l.Status = Status.OutroErro;
                l.Motivo = "rejeição " + t.Erro;
                return true;
            }
            return false;
        }

        // Lista as tentativas em contingência não autorizadas do período, sem consultar a SEFAZ (tela inicial da interface).
        public List<LinhaRelatorio> Listar(Historico historico)
        {
            var linhas = new List<LinhaRelatorio>();
            foreach (var t in Tentativas)
            {
                var l = NovaLinha(t);
                linhas.Add(l);
                var reg = historico.BuscarCopiado(t.ChaveAutorizada);
                if (ForaDoFluxo(t, l)) { }
                else if (t.ChaveAutorizada == null)
                    Pendente(l, "chave autorizada não identificada", new List<string>(t.Avisos));
                else if (reg != null)
                {
                    l.Status = Status.JaProcessada;
                    l.Motivo = "copiado em " + reg.DataHora + " (" + historico.Caminho + ")";
                    l.ArquivoEscolhido = reg.Arquivo;
                    l.CaminhoOrigem = reg.CaminhoOrigem;
                    l.CaminhoDestino = reg.CaminhoDestino;
                }
                else
                {
                    l.Status = Status.AProcessar;
                    l.Motivo = "aguardando processamento (consulta SEFAZ e escolha do arquivo)";
                }
            }
            return linhas;
        }

        // Linha de comando: só as notas da lista ("série/número").
        public List<LinhaRelatorio> AnalisarNotas(string pastaCopia, ICollection<string> notasAlvo, Historico historico, Action<int, int> progresso)
        {
            log.Info("Notas com 539 fora da lista (ignoradas, sem consulta à SEFAZ): " + string.Join(", ",
                Tentativas.Select(t => t.Nota).Distinct().Where(n => !notasAlvo.Contains(n))));
            var semTentativa = notasAlvo.Where(n => !Tentativas.Any(t => t.Nota == n)).ToList();
            return Analisar(pastaCopia, t => notasAlvo.Contains(t.Nota), semTentativa, historico, progresso);
        }

        // Consulta a SEFAZ e decide cada tentativa incluída; não copia nada.
        public List<LinhaRelatorio> Analisar(string pastaCopia, Func<Tentativa, bool> incluir, IEnumerable<string> notasSemTentativa,
            Historico historico, Action<int, int> progresso)
        {
            var linhas = new List<LinhaRelatorio>();
            foreach (string alvo in notasSemTentativa)
            {
                string[] sn = alvo.Split('/');
                string aut;
                if (autorizadaPorNota.TryGetValue(alvo, out aut))
                    linhas.Add(new LinhaRelatorio
                    {
                        Serie = sn[0], Numero = sn[1], Status = Status.JaAutorizada, Classificacao = "Já autorizada",
                        Motivo = "nota autorizada (chave " + aut + ") e sem tentativa em contingência pendente; nada a fazer"
                    });
                else
                    linhas.Add(new LinhaRelatorio
                    {
                        Serie = sn[0], Numero = sn[1], Status = Status.Pendente, Classificacao = "Não encontrada",
                        Motivo = "nenhuma tentativa em contingência nem autorização para esta nota no período"
                    });
            }

            var escolhidas = Tentativas.Where(incluir).ToList();
            int feitas = 0;
            foreach (var t in escolhidas)
            {
                if (progresso != null) progresso(feitas, escolhidas.Count);
                feitas++;
                var l = NovaLinha(t);
                linhas.Add(l);
                var avisos = new List<string>(t.Avisos);

                if (ForaDoFluxo(t, l)) continue;
                if (t.ChaveAutorizada == null)
                {
                    Pendente(l, "chave autorizada não identificada", avisos);
                    continue;
                }
                // Já copiada numa execução anterior: nem consulta a SEFAZ de novo.
                var reg = historico.BuscarCopiado(t.ChaveAutorizada);
                if (reg != null)
                {
                    l.ArquivoEscolhido = reg.Arquivo;
                    l.CaminhoOrigem = reg.CaminhoOrigem;
                    l.CaminhoDestino = reg.CaminhoDestino;
                    l.Status = Status.JaProcessada;
                    l.Motivo = "copiado em " + reg.DataHora + " (" + historico.Caminho + ")";
                    continue;
                }
                var sel = Selecionar(t.ChaveAutorizada);
                l.Sel = sel;
                l.SituacaoSefaz = sel.Sefaz != null ? sel.Sefaz.Situacao : "";
                if (sel.Sefaz != null && sel.Sefaz.Prot != null)
                {
                    l.NProtSefaz = sel.Sefaz.Prot.NProt;
                    l.DigValSefaz = sel.Sefaz.Prot.DigVal;
                }
                l.ValorSefaz = sel.ValorSefaz;
                if (sel.MotivoPendente != null)
                {
                    if (sel.Indicado != null)
                    {
                        l.ArquivoEscolhido = "(indicado) " + sel.Indicado.Nome;
                        l.CaminhoOrigem = sel.Indicado.Caminho;
                    }
                    Pendente(l, sel.MotivoPendente, avisos);
                    continue;
                }

                l.ArquivoEscolhido = sel.Escolhido.Nome;
                l.CaminhoOrigem = sel.Escolhido.Caminho;
                if (avisos.Count > 0) { Pendente(l, "verificar manualmente", avisos); continue; }
                if (!t.Valor.HasValue) { Pendente(l, "valor do sistema não encontrado", avisos); continue; }
                if (t.Valor == sel.ValorSefaz)
                {
                    // Mesmo valor: o arquivo autorizado não resolve; a chave precisa ser ajustada no banco do Metre.
                    l.Status = Status.AjusteChave;
                    l.Motivo = "valor do sistema igual ao da SEFAZ (" + Moeda(t.Valor) + "); ajustar a chave no banco do Metre; nenhuma ação tomada";
                    continue;
                }
                l.CaminhoDestino = Path.Combine(pastaCopia, sel.Escolhido.Nome);
                if (File.Exists(l.CaminhoDestino))
                {
                    bool igual = NotaXml.HashSha256(NotaXml.LerSomenteLeitura(l.CaminhoDestino)) == sel.Escolhido.Sha256;
                    Pendente(l, "já existe no destino (" + (igual ? "conteúdo idêntico" : "conteúdo DIFERENTE") + "); não será sobrescrito", avisos);
                    continue;
                }
                l.Fonte = sel.Escolhido;
                l.Status = Status.ACopiar;
                l.Motivo = "sistema " + Moeda(t.Valor) + " ≠ SEFAZ " + Moeda(sel.ValorSefaz) + "; " + sel.MotivoEscolha;
            }
            if (progresso != null) progresso(escolhidas.Count, escolhidas.Count);
            return linhas.OrderBy(x => int.Parse(x.Serie)).ThenBy(x => long.Parse(x.Numero)).ThenBy(x => x.DhEmiSistema).ToList();
        }

        static void Pendente(LinhaRelatorio l, string motivo, List<string> avisos)
        {
            l.Status = Status.Pendente;
            l.Motivo = motivo + (avisos.Count > 0 ? " [" + string.Join("; ", avisos) + "]" : "");
        }

        // cUF+AAMM ignorados: CNPJ(14) + modelo(2) + série(3) + número(9) identificam a numeração.
        static string ChaveNumero(string chave) { return chave.Substring(6, 28); }
        static string SerieDaChave(string chave) { return int.Parse(chave.Substring(22, 3)).ToString(); }
        static string NumeroDaChave(string chave) { return long.Parse(chave.Substring(25, 9)).ToString(); }

        public static string Moeda(decimal? v)
        {
            return v.HasValue ? v.Value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR")) : "?";
        }
    }
}
