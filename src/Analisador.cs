using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DuplicidadeNfce
{
    // Uma tentativa do sistema (chave própria) que usou um número já autorizado com outra chave.
    class Tentativa
    {
        public string Chave, Numero, Serie, Pedido, DhEmi, ChaveAutorizada, NRec;
        public decimal? Valor;
        public bool Rejeicao539;
        public List<string> Origens = new List<string>();
        public List<string> Avisos = new List<string>();
        public string Nota { get { return Serie + "/" + Numero; } }
    }

    class LinhaRelatorio
    {
        public string Numero, Serie, Pedido, DhEmiSistema, ChaveSistema, Origem;
        public decimal? ValorSistema, ValorSefaz;
        public string ChaveAutorizada, NRec, SituacaoSefaz, NProtSefaz, DigValSefaz;
        public string ArquivoEscolhido, CaminhoOrigem, CaminhoDestino, Status, Motivo;
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

            // 1) Retornos 539 nos REJEICAO_NOTA (documento principal e restos de gravações anteriores).
            foreach (var rej in notas.Where(n => n.Tipo == TipoArquivo.Rejeicao && n.NaFaixaPrincipal && n.Chave != null))
            {
                if (rej.Prot != null && rej.Prot.CStat == "539")
                    Registrar539(lista, autorizadasPorNumero, rej.Prot, rej, "539 em " + rej.Nome);
                else
                    log.Info("Ignorado (sem cStat 539 no retorno principal): " + rej.Caminho +
                        (rej.Prot != null ? " cStat " + rej.Prot.CStat : ""));
                foreach (var p in rej.ProtsNaSobra.Where(p => p.CStat == "539" && p.ChNFe != null && p.ChNFe != rej.Chave))
                    Registrar539(lista, autorizadasPorNumero, p, rej, "539 em resto de gravação anterior de " + rej.Nome);
            }

            // 2) Outras tentativas do mesmo número/série/CNPJ com chave diferente da autorizada.
            foreach (var n in notas.Where(x => x.Chave != null && x.NaFaixaPrincipal))
            {
                HashSet<string> ks;
                if (!autorizadasPorNumero.TryGetValue(ChaveNumero(n.Chave), out ks)) continue;
                if (ks.Contains(n.Chave) || lista.ContainsKey(n.Chave)) continue;
                var t = new Tentativa { Chave = n.Chave };
                t.Origens.Add("tentativa sem retorno 539 registrado (vista em " + n.Nome + ")");
                if (ks.Count == 1) t.ChaveAutorizada = ks.First();
                else t.Avisos.Add("mais de uma chave autorizada para o mesmo número: " + string.Join(", ", ks));
                lista[n.Chave] = t;
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

            string num = ChaveNumero(k);
            HashSet<string> ks;
            if (!autPorNum.TryGetValue(num, out ks)) autPorNum[num] = ks = new HashSet<string>();
            ks.Add(k);
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
                (copias > 1 ? ", " + copias + " cópias idênticas" : "");
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
            if (validos.GroupBy(a => a.Sha256).Count() > 1)
            {
                motivoPendente = "mais de um arquivo válido com conteúdo diferente: " + string.Join(" | ", validos.Select(a => a.Caminho));
                return null;
            }
            var escolhido = validos.OrderBy(a => a.Caminho.IndexOf(@"\Autorizadas\", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ThenBy(a => a.Caminho).First();
            if (escolhido.TemSobra)
            {
                motivoPendente = "arquivo válido tem conteúdo residual após </nfeProc>: " + escolhido.Caminho;
                return null;
            }
            copias = validos.Count;
            return escolhido;
        }

        LinhaRelatorio NovaLinha(Tentativa t)
        {
            var l = new LinhaRelatorio
            {
                Numero = t.Numero, Serie = t.Serie, Pedido = t.Pedido, DhEmiSistema = t.DhEmi,
                ValorSistema = t.Valor, ChaveSistema = t.Chave, ChaveAutorizada = t.ChaveAutorizada,
                NRec = t.NRec, Origem = string.Join("; ", t.Origens)
            };
            List<NotaXml> arqs;
            if (t.ChaveAutorizada != null && porChave.TryGetValue(t.ChaveAutorizada, out arqs))
                l.ArquivosChaveAutorizada = arqs.Select(a => a.Caminho).OrderBy(c => c).ToList();
            return l;
        }

        // Lista todas as notas com 539 do período, sem consultar a SEFAZ (tela inicial da interface).
        public List<LinhaRelatorio> Listar(Historico historico)
        {
            var linhas = new List<LinhaRelatorio>();
            foreach (var t in Tentativas)
            {
                var l = NovaLinha(t);
                linhas.Add(l);
                var reg = historico.BuscarCopiado(t.ChaveAutorizada);
                if (!t.Rejeicao539)
                    Pendente(l, "sem retorno 539", new List<string>(t.Avisos));
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
                linhas.Add(new LinhaRelatorio
                {
                    Serie = sn[0], Numero = sn[1], Status = Status.Pendente,
                    Motivo = "sem retorno 539 (nenhum REJEICAO_NOTA com cStat 539 para esta nota no período)"
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
                    if (!t.Rejeicao539)
                    {
                        avisos.Add("arquivo autorizado desta numeração já copiado em " + reg.DataHora);
                        Pendente(l, "sem retorno 539", avisos);
                        continue;
                    }
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
                if (!t.Rejeicao539) { Pendente(l, "sem retorno 539", avisos); continue; }
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
