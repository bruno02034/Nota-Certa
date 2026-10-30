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
        OutroErro,      // rejeição com outro código (ex.: 537)
        AutorizadaNaContingencia  // a própria chave já está autorizada (linha criada ao importar um NFCE)
    }

    // Uma tentativa do sistema (chave própria) emitida em contingência e ainda não autorizada.
    class Tentativa
    {
        public string Chave, Numero, Serie, Pedido, DhEmi, ChaveAutorizada, NRec, Erro, IdMetre;
        public decimal? Valor;
        public bool Rejeicao539;
        public Classe Classe = Classe.Duplicidade;
        public List<string> Origens = new List<string>();
        public List<string> Avisos = new List<string>();
        // Número sem XML autorizado no computador: tentativas que não estão na contingência do Metre e
        // podem ter sido autorizadas (o Metre nem sempre grava o _NFCE). Confirmadas na SEFAZ ao processar.
        public List<string> Candidatas = new List<string>();
        public string Nota { get { return Serie + "/" + Numero; } }

        public string Classificacao
        {
            get
            {
                switch (Classe)
                {
                    case Classe.SemRetorno: return Candidatas.Count > 0
                    ? "Duplicidade provável (autorizada sem XML na pasta Autorizadas; confirmar na SEFAZ)"
                    : "Em contingência sem retorno";
                    case Classe.OutroErro: return "Outro erro";
                    case Classe.AutorizadaNaContingencia: return "Já autorizada (entrada criada por importação)";
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
        public InfoAjuste Ajuste;     // sem XML da chave autorizada para copiar: ajuste da chave no banco
        public Tentativa Tentativa;   // para reavaliar o ajuste depois de uma conferência manual
    }

    // Onde mais procurar o XML da chave autorizada e o que conferir no banco antes de propor o ajuste de chave
    // (o Motor liga às pastas e ao banco do Metre; null = sem banco, sem ajuste).
    class FontesAjuste
    {
        public Func<Tentativa, string, List<NotaXml>> BuscarXmls;   // qualquer pasta/nome e chefxml.xml
        public Func<string, List<string>> LinhasComChave;           // ids de nota_saida que já têm a chave
    }

    class Selecao
    {
        public string ChaveAutorizada;
        public RespostaSefaz Sefaz;
        public NotaXml Escolhido;
        public NotaXml Indicado;        // só pelo protNFe local, quando a SEFAZ não respondeu (nunca é copiado)
        public decimal? ValorSefaz;
        public string MotivoPendente;   // null = escolhido
        public bool ValorNaoConferido;  // nenhum arquivo conferiu com o digVal: vale o ASSINADA único da chave
        public decimal? ValorConferido; // vNF dos arquivos que conferem com o digVal, mesmo sem ASSINADA para copiar
        public string MotivoEscolha;
        public bool SemArquivo;         // nenhum arquivo lido tem a chave autorizada no Id
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

        readonly List<LinhaContingencia> contingencia;   // lista do Metre (banco/CSV); null = deduzir dos XMLs
        readonly FontesAjuste fontes;
        Historico historicoAtual;
        string pastaCopiaAtual;
        Dictionary<string, int> rodadas = new Dictionary<string, int>();
        // Chave + dhRecbto da SEFAZ: arquivos fora do período lido (pasta do dia da autorização e pastas extras). Não depende do banco.
        public Func<string, string, List<NotaXml>> XmlsDoDia;

        public Analisador(List<NotaXml> notas, Func<string, List<NotaXml>, RespostaSefaz> consultar, Log log,
            List<LinhaContingencia> contingencia = null, FontesAjuste fontes = null)
        {
            this.contingencia = contingencia;
            this.fontes = fontes;
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

            if (contingencia != null)
                lista = DaContingenciaDoMetre(lista, autorizadas, autorizadasPorNumero);
            else
                DeduzirDosArquivos(lista, autorizadas, autorizadasPorNumero);
            log.Info("Chaves autorizadas nos arquivos: " + autorizadas.Count + "; tentativas em contingência não autorizadas: " + lista.Count +
                " (" + string.Join(", ", lista.Values.GroupBy(t => t.Classificacao).Select(g => g.Count() + " " + g.Key)) + ")");
            return Completar(lista);
        }

        // A lista exata da aba Contingência (banco do Metre): cada linha é uma tentativa; os XMLs só classificam.
        Dictionary<string, Tentativa> DaContingenciaDoMetre(Dictionary<string, Tentativa> do539, HashSet<string> autorizadas,
            Dictionary<string, HashSet<string>> autorizadasPorNumero)
        {
            var lista = new Dictionary<string, Tentativa>();
            foreach (var c in contingencia)
            {
                if (string.IsNullOrEmpty(c.Chave) || c.Chave.Length != 44)
                {
                    log.Info("Contingência do Metre sem chave válida (id " + c.Id + ", nota " + c.Serie + "/" + c.Numero + "): ignorada");
                    continue;
                }
                Tentativa t;
                if (!do539.TryGetValue(c.Chave, out t)) t = new Tentativa { Chave = c.Chave };
                t.IdMetre = c.Id;
                t.Pedido = c.Pedido;
                t.Valor = c.Valor;
                if (c.DtContingencia.HasValue || c.DtEmissao.HasValue)
                    t.DhEmi = (c.DtContingencia ?? c.DtEmissao).Value.ToString("yyyy-MM-ddTHH:mm:ss");
                t.Origens.Insert(0, "aba Contingência do Metre (id_nota_saida " + c.Id + ")");

                HashSet<string> ks;
                if (autorizadas.Contains(c.Chave))
                {
                    t.Classe = Classe.AutorizadaNaContingencia;
                    t.ChaveAutorizada = c.Chave;
                }
                else if (t.Rejeicao539) { }
                else if (autorizadasPorNumero.TryGetValue(ChaveNumero(c.Chave), out ks) && !ks.Contains(c.Chave))
                {
                    t.Origens.Add("número já autorizado com outra chave");
                    if (ks.Count == 1) t.ChaveAutorizada = ks.First();
                    else t.Avisos.Add("mais de uma chave autorizada para o mesmo número: " + string.Join(", ", ks));
                }
                else
                    t.Classe = Classe.SemRetorno;
                lista[c.Chave] = t;
            }

            // Números sem nenhuma autorização nos arquivos: as outras tentativas desse número que NÃO estão
            // na contingência do Metre (ex.: 6/89257 das 08:42) são candidatas a ter sido autorizadas.
            var naContingencia = new HashSet<string>(lista.Keys);
            foreach (var t in lista.Values.Where(x => x.Classe == Classe.SemRetorno))
            {
                string num = ChaveNumero(t.Chave);
                t.Candidatas = notas.Where(n => n.Chave != null && n.Tipo == TipoArquivo.Assinada && ChaveNumero(n.Chave) == num
                        && !naContingencia.Contains(n.Chave))
                    .OrderBy(n => n.DhEmi).Select(n => n.Chave).Distinct().ToList();
                if (t.Candidatas.Count > 0)
                    t.Origens.Add("número sem XML autorizado; tentativa(s) fora da contingência a confirmar na SEFAZ: " + string.Join(", ", t.Candidatas));
            }
            return lista;
        }

        // Sem acesso ao Metre: deduz as tentativas pendentes pelos XMLs.
        void DeduzirDosArquivos(Dictionary<string, Tentativa> lista, HashSet<string> autorizadas,
            Dictionary<string, HashSet<string>> autorizadasPorNumero)
        {
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
            // Paliativo até ler o banco do Metre: só continua na contingência o número que o Metre reenviou
            // no último reenvio (ONTIGENCIA_ENVIA regravado no dia do reenvio mais recente). Número sem ENVIA
            // ou com ENVIA de dia anterior já saiu da aba Contingência (ex.: 6/84643, 6/89110).
            var envia = notas.Where(x => x.Tipo == TipoArquivo.ContingenciaEnvia && x.Chave != null)
                .GroupBy(x => ChaveNumero(x.Chave)).ToDictionary(g => g.Key, g => g.Max(x => x.ModificadoEm));
            if (envia.Count > 0)
            {
                DateTime diaReenvio = envia.Values.Max().Date;
                var fora = lista.Values.Where(t =>
                {
                    DateTime m;
                    return !envia.TryGetValue(ChaveNumero(t.Chave), out m) || m.Date < diaReenvio;
                }).ToList();
                foreach (var t in fora)
                {
                    DateTime m;
                    log.Info("Fora da contingência (Metre não reenviou em " + diaReenvio.ToString("dd/MM/yyyy") + "): " + t.Chave +
                        (envia.TryGetValue(ChaveNumero(t.Chave), out m) ? " — último ENVIA em " + m.ToString("dd/MM/yyyy HH:mm") : " — sem ONTIGENCIA_ENVIA"));
                    lista.Remove(t.Chave);
                }
                log.Info("Último reenvio da contingência: " + diaReenvio.ToString("dd/MM/yyyy") + "; " + fora.Count + " tentativa(s) fora da contingência ignorada(s)");
            }
        }

        List<Tentativa> Completar(Dictionary<string, Tentativa> lista)
        {
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
                // Valor, pedido e data do Metre (banco) têm prioridade; os XMLs completam o que faltar.
                if (comValor.Count > 0)
                {
                    if (!t.Valor.HasValue) t.Valor = comValor[0].VNF;
                    var distintos = comValor.Select(a => a.VNF.Value).Concat(new[] { t.Valor.Value }).Distinct().ToList();
                    if (distintos.Count > 1)
                        t.Avisos.Add("valores diferentes entre arquivos da mesma chave" + (t.IdMetre != null ? " e o Metre (" + Moeda(t.Valor) + ")" : "") + ": " +
                            string.Join(", ", comValor.Select(a => a.Nome + "=" + Moeda(a.VNF))));
                }
                var comPedido = ordenados.FirstOrDefault(a => !string.IsNullOrEmpty(a.Pedido));
                if (comPedido != null && string.IsNullOrEmpty(t.Pedido)) t.Pedido = comPedido.Pedido;
                var comData = ordenados.FirstOrDefault(a => a.DhEmi != null);
                if (comData != null && t.DhEmi == null) t.DhEmi = comData.DhEmi;
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
            sel.SemArquivo = arqs.Count == 0;
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

            // Sem arquivo que o Metre importe nas pastas lidas: procura no dia da autorização e nas pastas extras.
            if (XmlsDoDia != null && !arqs.Any(Importavel))
            {
                var achados = new List<NotaXml>();
                try { achados = XmlsDoDia(k, sel.Sefaz.Prot.DhRecbto); }
                catch (Exception ex) { log.Info("  busca na pasta do dia da autorização falhou: " + ex.Message); }
                var novos = achados.Where(n => !arqs.Any(a => a.Caminho.Equals(n.Caminho, StringComparison.OrdinalIgnoreCase))).ToList();
                if (novos.Count > 0)
                {
                    arqs = arqs.Concat(novos).ToList();
                    porChave[k] = arqs;
                    sel.SemArquivo = false;
                    log.Info("  " + novos.Count + " arquivo(s) da chave fora das pastas lidas (autorização em " + sel.Sefaz.Prot.DhRecbto + "): " +
                        string.Join(" | ", novos.Select(n => n.Caminho)));
                }
            }
            var escolhido = Escolher(arqs, sel.Sefaz.Prot, "SEFAZ", sel, out motivo, out copias);
            // Sem ASSINADA para copiar o valor conferido pelo conteúdo ainda vale (aparece na tela e permite o ajuste de chave).
            if (escolhido == null) { sel.ValorSefaz = sel.ValorConferido; sel.MotivoPendente = motivo; return sel; }
            sel.Escolhido = escolhido;
            sel.ValorSefaz = escolhido.VNF;
            sel.MotivoEscolha = (escolhido.Tipo == TipoArquivo.Temp
                    ? "Temp_ANTES_ENVIO da chave autorizada, sem ASSINADA (nProt " + sel.Sefaz.Prot.NProt + "), conteúdo idêntico ao autorizado"
                    : sel.ValorNaoConferido
                    ? "ASSINADA da chave autorizada (nProt " + sel.Sefaz.Prot.NProt + "); nenhum XML do computador confere com o digVal, valor não conferido pelo conteúdo"
                    : "ASSINADA da chave autorizada com o mesmo valor da SEFAZ (nProt " + sel.Sefaz.Prot.NProt + ")" +
                      (escolhido.DigestDeclarado == sel.Sefaz.Prot.DigVal ? ", conteúdo idêntico ao autorizado" : ", conteúdo assinado difere do XML autorizado")) +
                (copias > 1 ? "; " + copias + " cópias idênticas" : "");
            log.Info("  Escolhido: " + escolhido.Caminho);
            return sel;
        }

        static bool Importavel(NotaXml a)
        {
            return a.Tipo == TipoArquivo.Assinada || (a.Tipo == TipoArquivo.Temp && a.Nome.ToUpperInvariant().Contains("TEMP_ANTES_ENVIO"));
        }

        // Único arquivo cujo conteúdo é o autorizado no protocolo 'prot'; null com o motivo em caso de dúvida.
        NotaXml Escolher(List<NotaXml> arqs, ProtocoloXml prot, string fonte, Selecao sel, out string motivoPendente, out int copias)
        {
            motivoPendente = null;
            copias = 0;
            string digVal = prot.DigVal;
            if (string.IsNullOrEmpty(digVal)) { motivoPendente = fonte + " sem digVal"; return null; }

            // Qualquer arquivo da chave que confira com a SEFAZ (inclusive Temp/ENVIA/FALHA_SCHEMA) serve para
            // fixar o valor autorizado; copiado é sempre o ASSINADA.
            var validos = new List<NotaXml>();
            sel.Candidatos.Clear();
            foreach (var a in arqs.OrderBy(x => x.Caminho))
            {
                string motivo = null;
                if (a.Erro != null) motivo = "erro de leitura: " + a.Erro;
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

            string chaveAut = prot.ChNFe ?? (arqs.Count > 0 ? arqs[0].Chave : null);
            decimal valorAut;
            sel.ValorNaoConferido = false;
            sel.ValorConferido = null;
            if (validos.Count == 0)
            {
                // Nenhum arquivo confere com a SEFAZ (o Metre regravou o XML ao transmitir e não salvou o
                // _NFCE, ex.: 6/89257). Fica o ASSINADA da própria chave autorizada, se houver um só.
                if (arqs.Any(a => a.DigestDeclarado == digVal && a.DigestCalculado != a.DigestDeclarado))
                {
                    motivoPendente = "há arquivo com o DigestValue da SEFAZ mas conteúdo alterado; conferir manualmente";
                    return null;
                }
                var unicos = arqs.Where(a => a.Tipo == TipoArquivo.Assinada && a.Erro == null && a.Chave == chaveAut && !a.TemSobra && a.VNF.HasValue)
                    .GroupBy(a => a.Sha256).ToList();
                if (unicos.Count != 1)
                {
                    motivoPendente = arqs.Count == 0
                        ? "nenhum arquivo com a chave autorizada"
                        : "nenhum dos " + arqs.Count + " arquivo(s) com a chave tem DigestValue igual ao digVal " + fonte +
                          (unicos.Count > 1 ? " e há mais de um ASSINADA diferente dessa chave" : " e não há ASSINADA dessa chave");
                    return null;
                }
                valorAut = unicos[0].First().VNF.Value;
                sel.ValorNaoConferido = true;
            }
            else
            {
                // Regra do Bruno (22/10): o arquivo copiado é o ASSINADA da chave autorizada com o mesmo valor
                // da SEFAZ. É o que o Metre importa como solução da contingência; importar o _NFCE (que traz o
                // protNFe) aumentou a contingência. Os válidos (digest = digVal) só fixam o valor autorizado.
                var valores = validos.Where(a => a.VNF.HasValue).Select(a => a.VNF.Value).Distinct().ToList();
                if (valores.Count != 1)
                {
                    motivoPendente = "valor autorizado indefinido entre os arquivos que conferem com a SEFAZ";
                    return null;
                }
                valorAut = valores[0];
                sel.ValorConferido = valorAut;
            }
            var assinadas = arqs.Where(a => a.Tipo == TipoArquivo.Assinada && a.Erro == null && a.Chave == chaveAut).ToList();
            var preferidos = assinadas.Where(a => a.VNF == valorAut).ToList();
            // Sem ASSINADA da chave, o Temp_ANTES_ENVIO que confere com o digVal é o que o Metre importa (testado em 30/10).
            if (assinadas.Count == 0)
                preferidos = validos.Where(a => Importavel(a) && a.Chave == chaveAut && a.VNF == valorAut).ToList();
            foreach (var c in sel.Candidatos.Where(c => assinadas.Any(a => a.Caminho == c.Caminho)))
            {
                var a = assinadas.First(x => x.Caminho == c.Caminho);
                c.Aceito = a.VNF == valorAut;
                c.Motivo = c.Aceito
                    ? "ASSINADA da chave autorizada com o mesmo valor da SEFAZ (" + Moeda(valorAut) + ")" +
                      (a.DigestDeclarado == digVal ? ", conteúdo idêntico ao autorizado" : "")
                    : "ASSINADA da chave autorizada, mas valor " + Moeda(a.VNF) + " ≠ SEFAZ " + Moeda(valorAut);
            }
            if (preferidos.Count == 0)
            {
                motivoPendente = "nenhum ASSINADA da chave autorizada com o valor da SEFAZ (" + Moeda(valorAut) + ")" +
                    (assinadas.Count > 0 ? "; ASSINADA encontrados: " + string.Join(" | ", assinadas.Select(a => a.Nome + "=" + Moeda(a.VNF))) : "");
                return null;
            }
            // Mais de um ASSINADA igual em valor: fica o de conteúdo idêntico ao autorizado, se houver.
            var identicos = preferidos.Where(a => a.DigestDeclarado == digVal).ToList();
            if (identicos.Count > 0) preferidos = identicos;
            var semSobra = preferidos.Where(a => !a.TemSobra).ToList();
            if (semSobra.Count == 0)
            {
                motivoPendente = "arquivo válido tem conteúdo residual após </nfeProc>: " + preferidos[0].Caminho;
                return null;
            }
            if (semSobra.GroupBy(a => a.Sha256).Count() > 1)
            {
                motivoPendente = "mais de um ASSINADA com o valor da SEFAZ e conteúdo diferente: " + string.Join(" | ", semSobra.Select(a => a.Caminho));
                return null;
            }
            var escolhido = semSobra.OrderBy(a => a.Caminho).First();
            copias = semSobra.Count;
            foreach (var c in sel.Candidatos.Where(c => c.Caminho != escolhido.Caminho && validos.Any(v => v.Caminho == c.Caminho)))
            {
                c.Aceito = false;
                c.Motivo = "confere com a SEFAZ (confirma o conteúdo e o valor " + Moeda(valorAut) + "), mas não é copiado: vale " + escolhido.Nome;
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

        // Número sem XML autorizado no computador: pergunta à SEFAZ qual das tentativas fora da contingência
        // foi autorizada. Achou: vira duplicidade com essa chave autorizada. Não achou: fica na linha 'l'.
        bool ConfirmarCandidata(Tentativa t, LinhaRelatorio l)
        {
            var semResposta = new List<string>();
            foreach (string cand in t.Candidatas)
            {
                var s = Selecionar(cand);
                if (s.Sefaz != null && s.Sefaz.Autorizada && s.Sefaz.Prot != null && s.Sefaz.Prot.ChNFe == cand)
                {
                    t.ChaveAutorizada = cand;
                    t.Classe = Classe.Duplicidade;
                    t.Origens.Add("autorização confirmada na SEFAZ para a tentativa " + cand + " (o Metre não gravou o XML autorizado)");
                    l.ChaveAutorizada = cand;
                    l.Classificacao = "Duplicidade (autorizada sem XML na pasta Autorizadas, confirmada na SEFAZ)";
                    l.Origem = string.Join("; ", t.Origens);
                    List<NotaXml> arqs;
                    if (porChave.TryGetValue(cand, out arqs)) l.ArquivosChaveAutorizada = arqs.Select(a => a.Caminho).OrderBy(c => c).ToList();
                    return true;
                }
                if (s.Sefaz == null || !s.Sefaz.Ok) semResposta.Add(cand + " (" + (s.Sefaz != null ? s.Sefaz.Situacao + ": " + s.Sefaz.Erro : "sem consulta") + ")");
            }
            if (semResposta.Count > 0)
                Pendente(l, "número sem XML autorizado e a SEFAZ não respondeu para: " + string.Join("; ", semResposta), new List<string>());
            else
            {
                l.Status = Status.EmContingencia;
                l.Motivo = "a SEFAZ não tem autorização para nenhuma tentativa deste número (" + string.Join(", ", t.Candidatas) + "); aguardando transmissão";
            }
            return false;
        }

        public const string TransmitaPrimeiro = "Transmita a nota no Metre primeiro; o retorno de duplicidade (539) traz a chave autorizada. " +
            "Depois clique em Listar contingência de novo.";

        // Sem retorno ou com outro erro: nada a consultar nem copiar.
        bool ForaDoFluxo(Tentativa t, LinhaRelatorio l)
        {
            if (t.Classe == Classe.AutorizadaNaContingencia)
            {
                l.Status = Status.JaAutorizada;
                l.Motivo = "esta chave já está autorizada na SEFAZ; a linha na contingência foi criada ao importar o XML autorizado (NFCE). " +
                    "Não transmitir nem importar de novo: tratar no Metre";
                return true;
            }
            // Sem 539 não há chave autorizada: quem grava o REJEICAO com ela é o Metre, ao transmitir a nota.
            if (t.Classe == Classe.SemRetorno && t.Candidatas.Count == 0)
            {
                l.Status = Status.EmContingencia;
                l.Motivo = TransmitaPrimeiro;
                return true;
            }
            if (t.Classe == Classe.OutroErro)
            {
                l.Status = Status.OutroErro;
                l.Motivo = "rejeição " + t.Erro;
                return true;
            }
            // Sem a lista do Metre, duplicidade só deduzida pelos arquivos não é processada: pelos XMLs não dá
            // para saber se a nota ainda está na aba Contingência. Com o banco/CSV a lista já é a do Metre.
            if (!t.Rejeicao539 && contingencia == null)
            {
                l.Status = Status.Pendente;
                l.Motivo = "sem retorno 539: duplicidade provável (número já autorizado com a chave " + (t.ChaveAutorizada ?? "?") +
                    "), mas o Metre ainda não registrou a rejeição; nada é copiado";
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
                // Com a lista do Metre (banco/CSV), estar na aba Contingência já diz que não foi resolvida:
                // o histórico não bloqueia (ele pode ter cópias erradas, como os _NFCE de 22/10).
                var reg = contingencia == null ? historico.BuscarCopiado(t.ChaveAutorizada) : null;
                if (Ajustada(t, l, historico)) { }
                else if (ForaDoFluxo(t, l)) { }
                else if (t.ChaveAutorizada == null && t.Candidatas.Count > 0)
                {
                    l.Status = Status.AProcessar;
                    l.Motivo = "número sem XML autorizado no computador; ao processar, a SEFAZ confirma qual tentativa foi autorizada";
                }
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
            historicoAtual = historico;
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
            rodadas = new Dictionary<string, int>();
            pastaCopiaAtual = pastaCopia;
            int feitas = 0;
            foreach (var t in escolhidas)
            {
                if (progresso != null) progresso(feitas, escolhidas.Count);
                feitas++;
                var l = NovaLinha(t);
                linhas.Add(l);
                var avisos = new List<string>(t.Avisos);

                if (Ajustada(t, l, historico)) continue;
                if (ForaDoFluxo(t, l)) continue;
                if (t.ChaveAutorizada == null && t.Candidatas.Count > 0 && !ConfirmarCandidata(t, l))
                    continue;
                if (t.ChaveAutorizada == null)
                {
                    Pendente(l, "chave autorizada não identificada", avisos);
                    continue;
                }
                // Já copiada numa execução anterior: nem consulta a SEFAZ de novo.
                // Com a lista do Metre (banco/CSV), estar na aba Contingência já diz que não foi resolvida:
                // o histórico não bloqueia (ele pode ter cópias erradas, como os _NFCE de 22/10).
                var reg = contingencia == null ? historico.BuscarCopiado(t.ChaveAutorizada) : null;
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
                l.Tentativa = t;
                List<NotaXml> daChave;
                if (porChave.TryGetValue(t.ChaveAutorizada, out daChave)) l.ArquivosChaveAutorizada = daChave.Select(a => a.Caminho).OrderBy(c => c).ToList();
                string outraVenda = OutraVenda(t, sel);
                // Autorizada na SEFAZ, mas nenhum XML dessa chave para copiar: o caminho é ajustar a chave no banco.
                if (sel.MotivoPendente != null && sel.SemArquivo && sel.Sefaz != null && sel.Sefaz.Autorizada && sel.Sefaz.Prot.ChNFe == t.ChaveAutorizada)
                {
                    AvaliarAjuste(t, l, avisos);
                    log.Info("  Ajuste de chave: " + l.Status + " - " + l.Motivo);
                    continue;
                }
                // Há XML da chave que confere com a SEFAZ (ex.: só o Temp_ANTES_ENVIO), mas nenhum ASSINADA para copiar.
                // Com o mesmo valor do sistema o caminho é o ajuste de chave, que não copia nada.
                if (sel.MotivoPendente != null && sel.ValorConferido.HasValue && t.Valor == sel.ValorConferido && outraVenda == null && fontes != null && t.IdMetre != null &&
                    sel.Sefaz != null && sel.Sefaz.Autorizada && sel.Sefaz.Prot.ChNFe == t.ChaveAutorizada)
                {
                    AvaliarAjuste(t, l, avisos, sel.ValorConferido,
                        "valor autorizado conferido pelo XML da chave (DigestValue = digVal da SEFAZ); sem ASSINADA para copiar");
                    log.Info("  Ajuste de chave: " + l.Status + " - " + l.Motivo);
                    continue;
                }
                // Valor conhecido, mas sem arquivo para importar (ex.: só o _NFCE): o XML pode estar no chefxml.
                if (sel.MotivoPendente != null && sel.ValorConferido.HasValue && (t.Valor != sel.ValorConferido || outraVenda != null) &&
                    fontes != null && t.IdMetre != null && sel.Sefaz != null && sel.Sefaz.Autorizada && sel.Sefaz.Prot.ChNFe == t.ChaveAutorizada)
                {
                    var candidatos = new List<Candidato>(sel.Candidatos);
                    AvaliarAjuste(t, l, avisos);
                    if (l.Status == Status.ACopiar) continue;
                    sel.Candidatos = candidatos;
                    l.Ajuste = null;
                    l.ValorSefaz = sel.ValorConferido;
                }
                if (sel.MotivoPendente != null)
                {
                    if (sel.Indicado != null)
                    {
                        l.ArquivoEscolhido = "(indicado) " + sel.Indicado.Nome;
                        l.CaminhoOrigem = sel.Indicado.Caminho;
                    }
                    Pendente(l, (outraVenda != null ? outraVenda + "; " : "") + sel.MotivoPendente, avisos);
                    continue;
                }

                l.ArquivoEscolhido = sel.Escolhido.Nome;
                l.CaminhoOrigem = sel.Escolhido.Caminho;
                if (avisos.Count > 0) { Pendente(l, "verificar manualmente", avisos); continue; }
                if (!t.Valor.HasValue) { Pendente(l, "valor do sistema não encontrado", avisos); continue; }
                // Regra do Bruno (26/10): valor do sistema IGUAL ao da SEFAZ não vai para importação; só a chave_acesso
                // da linha é trocada no banco (ajuste de chave). Valor DIFERENTE segue o fluxo de cópia abaixo.
                // Sem o banco do Metre (arquivos/CSV) não há como ajustar: continua a cópia, como antes.
                // Número reaproveitado é outra venda: mesmo com valor igual vai para importação, nunca para o ajuste.
                bool mesmoValor = t.Valor == sel.ValorSefaz;
                if (mesmoValor && outraVenda == null && fontes != null && t.IdMetre != null)
                {
                    l.ArquivoEscolhido = null;
                    l.CaminhoOrigem = null;
                    AvaliarAjuste(t, l, avisos, sel.ValorSefaz, sel.ValorNaoConferido
                        ? "valor do ASSINADA único da chave autorizada (nenhum XML confere com o digVal, como no fluxo de cópia)"
                        : "valor autorizado conferido pelo XML da chave (DigestValue = digVal da SEFAZ)");
                    log.Info("  Ajuste de chave: " + l.Status + " - " + l.Motivo);
                    continue;
                }
                // Uma cópia por linha da contingência: a 2ª tentativa do mesmo número se resolve importando
                // de novo o mesmo ASSINADA, então cada rodada vai numa pasta (importar-1, importar-2...).
                l.CaminhoDestino = DestinoImportacao(t.ChaveAutorizada, sel.Escolhido.Nome);
                if (File.Exists(l.CaminhoDestino))
                {
                    bool igual = NotaXml.HashSha256(NotaXml.LerSomenteLeitura(l.CaminhoDestino)) == sel.Escolhido.Sha256;
                    Pendente(l, "já existe no destino (" + (igual ? "conteúdo idêntico" : "conteúdo DIFERENTE") + "); não será sobrescrito", avisos);
                    continue;
                }
                l.Fonte = sel.Escolhido;
                l.Status = Status.ACopiar;
                l.Motivo = (outraVenda != null ? outraVenda + "; " : "") +
                    (mesmoValor ? "sistema " + Moeda(t.Valor) + " = SEFAZ (mesmo valor); " : "sistema " + Moeda(t.Valor) + " ≠ SEFAZ " + Moeda(sel.ValorSefaz) + "; ") +
                    sel.MotivoEscolha;
            }
            if (progresso != null) progresso(escolhidas.Count, escolhidas.Count);
            return linhas.OrderBy(x => int.Parse(x.Serie)).ThenBy(x => long.Parse(x.Numero)).ThenBy(x => x.DhEmiSistema).ToList();
        }

        string DestinoImportacao(string chave, string nome)
        {
            int rodada;
            rodadas.TryGetValue(chave, out rodada);
            rodadas[chave] = ++rodada;
            return Path.Combine(pastaCopiaAtual, "importar-" + rodada, nome);
        }

        // O XML da chave autorizada está só no banco chefxml (a nota foi emitida em outro computador): com DigestValue = digVal
        // é o conteúdo autorizado, então vai para importar-N gravado como o Temp_ANTES_ENVIO, o arquivo que o Metre importa.
        void CopiarDoBanco(Tentativa t, LinhaRelatorio l, NotaXml x, string outraVenda, List<string> avisos)
        {
            l.Ajuste = null;
            l.ValorSefaz = x.VNF;
            if (avisos.Count > 0) { Pendente(l, "verificar manualmente", avisos); return; }
            if (x.TemSobra) { Pendente(l, "XML do banco com conteúdo residual após o documento: " + x.Caminho, avisos); return; }
            string k = t.ChaveAutorizada;
            string quando = x.DhEmi != null && x.DhEmi.Length >= 19
                ? x.DhEmi.Substring(8, 2) + "_" + x.DhEmi.Substring(5, 2) + "_" + x.DhEmi.Substring(0, 4) + "_" + x.DhEmi.Substring(11, 8).Replace(":", "_")
                : "banco";
            l.ArquivoEscolhido = "NOTA_" + t.Numero + "_MODELO_" + k.Substring(20, 2) + "_SERIE_" + t.Serie + "_CHAVE_" + k + "_Temp_ANTES_ENVIO_" + quando + ".xml";
            l.CaminhoOrigem = x.Caminho;
            l.CaminhoDestino = DestinoImportacao(k, l.ArquivoEscolhido);
            l.Fonte = x;
            l.Status = Status.ACopiar;
            l.Motivo = (outraVenda != null ? outraVenda + "; " : "") +
                "sistema " + Moeda(t.Valor) + (t.Valor == x.VNF ? " = SEFAZ (mesmo valor); " : " ≠ SEFAZ " + Moeda(x.VNF) + "; ") +
                "XML da chave autorizada só no banco (" + x.Caminho + ", DigestValue = digVal da SEFAZ), gravado para importar";
        }

        // O número foi usado por outra venda quando o XML autorizado tem outro pedido e outro dia de emissão que a linha
        // do sistema (visto em 30/10: número de 12/10 reaproveitado em 18/10). Importar troca a venda do sistema pela autorizada.
        string OutraVenda(Tentativa t, Selecao sel)
        {
            var aut = sel.Escolhido;
            List<NotaXml> arqs;
            if (aut == null && sel.Sefaz != null && sel.Sefaz.Prot != null && porChave.TryGetValue(t.ChaveAutorizada, out arqs))
                aut = arqs.FirstOrDefault(a => a.Erro == null && a.DigestCalculado == sel.Sefaz.Prot.DigVal);
            return OutraVendaDe(t, aut);
        }

        static string OutraVendaDe(Tentativa t, NotaXml aut)
        {
            if (aut == null || string.IsNullOrEmpty(aut.Pedido) || string.IsNullOrEmpty(t.Pedido) || aut.Pedido == t.Pedido) return null;
            if (aut.DhEmi == null || t.DhEmi == null || aut.DhEmi.Length < 10 || t.DhEmi.Length < 10 || aut.DhEmi.Substring(0, 10) == t.DhEmi.Substring(0, 10)) return null;
            return "NÚMERO REAPROVEITADO: a autorizada é outra venda (emitida em " + aut.DhEmi.Substring(0, 10) + ", pedido " + aut.Pedido + ", " + Moeda(aut.VNF) +
                "); no sistema: " + t.DhEmi.Substring(0, 10) + ", pedido " + t.Pedido + ", " + Moeda(t.Valor) + ". Importar troca a venda do sistema pela autorizada";
        }

        // Linha do Metre cuja chave_acesso já foi trocada pelo NotaCerta: o Metre só precisa ser reiniciado.
        bool Ajustada(Tentativa t, LinhaRelatorio l, Historico historico)
        {
            if (contingencia == null || t.IdMetre == null) return false;
            var reg = historico.BuscarAjuste(t.Chave);
            if (reg == null || reg.Arquivo == null || !reg.Arquivo.EndsWith("id_nota_saida=" + t.IdMetre)) return false;
            l.Status = Status.ChaveAjustada;
            l.ChaveAutorizada = t.Chave;
            l.Classificacao = "Duplicidade (chave ajustada no banco)";
            l.Motivo = "chave_acesso ajustada em " + reg.DataHora + " (antes " + reg.CaminhoOrigem + "); feche e abra o Metre para atualizar";
            l.Ajuste = new InfoAjuste
            {
                IdMetre = t.IdMetre, Numero = t.Numero, Serie = t.Serie, ChaveAtual = t.Chave, ChaveNova = t.Chave,
                ChaveAntiga = reg.CaminhoOrigem, Backup = reg.CaminhoDestino, ValorSistema = t.Valor
            };
            return true;
        }

        // Duplicidade autorizada na SEFAZ sem nenhum XML dessa chave nas pastas lidas. O valor autorizado vem de um
        // XML da chave achado em outro lugar (DigestValue = digVal) ou da conferência manual no portal; com valor
        // igual ao do sistema e todas as travas, a linha vira AJUSTE DE CHAVE (troca da chave_acesso no banco).
        // valorAutorizado: já conferido pelos arquivos da chave (DigestValue = digVal); null = procurar no banco/portal.
        void AvaliarAjuste(Tentativa t, LinhaRelatorio l, List<string> avisos, decimal? valorAutorizado = null, string origemValor = null)
        {
            var sel = l.Sel;
            string k = t.ChaveAutorizada, digVal = sel.Sefaz.Prot.DigVal;
            l.Ajuste = null;
            l.ValorSefaz = null;
            if (fontes == null || t.IdMetre == null)
            {
                Pendente(l, "nenhum arquivo com a chave autorizada; o ajuste de chave exige a lista do banco do Metre (FonteContingencia=banco)", avisos);
                return;
            }
            var a = l.Ajuste = new InfoAjuste
            {
                IdMetre = t.IdMetre, Numero = t.Numero, Serie = t.Serie, ChaveAtual = t.Chave, ChaveNova = k, ValorSistema = t.Valor
            };
            // Primeiro o que bloqueia de qualquer jeito, para não mandar conferir no portal à toa.
            List<string> outras;
            try { outras = fontes.LinhasComChave(k); }
            catch (Exception ex)
            {
                Pendente(l, "não foi possível conferir no banco se a chave autorizada já está em outra linha: " + ex.Message, avisos);
                return;
            }
            if (outras.Count > 0)
            {
                Pendente(l, "a chave autorizada já está em outra linha de nota_saida (id_nota_saida " + string.Join(", ", outras) + "); ajuste bloqueado", avisos);
                return;
            }

            if (valorAutorizado.HasValue)
            {
                a.ValorConferido = valorAutorizado;
                a.OrigemValor = origemValor;
                ConcluirAjuste(t, l, avisos);
                return;
            }
            List<NotaXml> extras;
            try { extras = fontes.BuscarXmls(t, k); }
            catch (Exception ex)
            {
                log.Info("  busca do XML da chave autorizada falhou: " + ex.Message);
                avisos.Add("busca do XML da chave autorizada falhou: " + ex.Message);
                extras = new List<NotaXml>();
            }
            sel.Candidatos.Clear();
            var conferem = new List<NotaXml>();
            foreach (var x in extras)
            {
                string motivo = null;
                if (x.Erro != null) motivo = "erro de leitura: " + x.Erro;
                else if (x.Chave != k) motivo = "outra chave no Id: " + x.Chave;
                else if (x.DigestDeclarado != digVal) motivo = "DigestValue " + x.DigestDeclarado + " ≠ digVal SEFAZ";
                else if (x.DigestCalculado != x.DigestDeclarado) motivo = "DigestValue declarado não confere com o conteúdo";
                else if (!x.VNF.HasValue) motivo = "sem vNF";
                if (motivo == null) conferem.Add(x);
                sel.Candidatos.Add(new Candidato
                {
                    Caminho = x.Caminho, Aceito = motivo == null, Tamanho = x.Tamanho, VNF = x.VNF,
                    Motivo = motivo ?? "chave autorizada no Id e DigestValue = digVal SEFAZ: fixa o valor autorizado (não é copiado)"
                });
                log.Info("  " + (motivo == null ? "[OK]  " : "[X]   ") + x.Caminho + " (vNF " + Moeda(x.VNF) + ")" + (motivo == null ? "" : " - " + motivo));
            }

            var valores = conferem.Select(x => x.VNF.Value).Distinct().ToList();
            if (valores.Count > 1)
            {
                Pendente(l, "XMLs da chave autorizada com valores diferentes: " + string.Join(" | ", conferem.Select(x => x.Nome + "=" + Moeda(x.VNF))), avisos);
                return;
            }
            if (valores.Count == 1)
            {
                a.ValorConferido = valores[0];
                a.OrigemValor = "valor autorizado pelo XML " + conferem[0].Nome + " (DigestValue = digVal da SEFAZ)";
                string outraVenda = OutraVendaDe(t, conferem[0]);
                if ((valores[0] != t.Valor || outraVenda != null) && pastaCopiaAtual != null)
                {
                    CopiarDoBanco(t, l, conferem[0], outraVenda, avisos);
                    return;
                }
            }
            else
            {
                a.PodeConferirPortal = true;
                var c = historicoAtual != null ? historicoAtual.BuscarConferencia(k) : null;
                if (c != null && c.Status == Status.ValorConfereManual && AjusteChave.ValorDoRegistro(c) == t.Valor)
                {
                    a.ValorConferido = t.Valor;
                    a.ValorManual = true;
                    a.OrigemValor = "valor conferido MANUALMENTE no portal da SEFAZ em " + c.DataHora + " (" + c.Arquivo + ")";
                }
                else if (c != null && c.Status == Status.ValorDiferenteManual && AjusteChave.ValorDoRegistro(c) == t.Valor)
                {
                    Pendente(l, "valor diferente no portal da SEFAZ (conferido manualmente em " + c.DataHora + "): seria o fluxo de importação, " +
                        "mas não há XML da chave autorizada para copiar", avisos);
                    return;
                }
                else
                {
                    Pendente(l, "nenhum XML com a chave autorizada (pastas do período e banco do Metre) e a consulta da SEFAZ não traz o valor: " +
                        "confira no portal (botão \"Conferir na SEFAZ\")", avisos);
                    return;
                }
            }
            ConcluirAjuste(t, l, avisos);
        }

        void ConcluirAjuste(Tentativa t, LinhaRelatorio l, List<string> avisos)
        {
            var a = l.Ajuste;
            var p = l.Sel.Sefaz.Prot;
            l.ValorSefaz = a.ValorConferido;
            if (avisos.Count > 0) { Pendente(l, "verificar manualmente", avisos); return; }
            if (a.ValorConferido != t.Valor)
            {
                Pendente(l, "valor diferente: falta o ASSINADA para importar (valor autorizado " + Moeda(a.ValorConferido) + " ≠ sistema " +
                    Moeda(t.Valor) + "; " + a.OrigemValor + ")", avisos);
                return;
            }
            l.Status = Status.AjusteChave;
            string trava = AjusteChave.Trava(l);
            if (trava != null) { Pendente(l, "ajuste de chave bloqueado: " + trava, avisos); return; }
            l.Motivo = "sistema " + Moeda(t.Valor) + " = SEFAZ (mesmo valor; " + a.OrigemValor + "): nada a importar, só ajustar a chave_acesso no banco: " +
                t.Chave + " → " + a.ChaveNova + " (SEFAZ cStat " + p.CStat + ", nProt " + p.NProt + ")";
        }

        // Depois de uma conferência manual no portal: refaz a avaliação do ajuste da linha.
        public void ReavaliarAjuste(LinhaRelatorio l, Historico historico)
        {
            if (l.Tentativa == null || l.Sel == null) throw new Exception("linha não processada");
            historicoAtual = historico;
            AvaliarAjuste(l.Tentativa, l, new List<string>(l.Tentativa.Avisos));
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
