using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DuplicidadeNfce
{
    // Um ciclo de trabalho (linha de comando ou interface): varre os XMLs, lista, processa e copia.
    class Motor : IDisposable
    {
        public readonly Config Cfg;
        public readonly bool SemSefaz;
        public readonly string Raiz, Destino, PastaRel, ArqLog, PastaBackups;
        public readonly Log Log;
        public List<NotaXml> Notas = new List<NotaXml>();
        public Analisador Analisador;
        public Historico Historico;
        readonly string carimbo;
        readonly string[] excluidas;
        readonly Func<string, List<NotaXml>, RespostaSefaz> consultar;

        // Cópias do dia: Duplicidade\AAAA-MM-DD. O histórico fica na pasta base.
        public string PastaCopia { get { return Path.Combine(Destino, DateTime.Today.ToString("yyyy-MM-dd")); } }

        public Motor(Config cfg, string dirExe, bool semSefaz)
        {
            Cfg = cfg;
            SemSefaz = semSefaz;
            if (!cfg.TodasDatas)
            {
                if (cfg.DataInicial == null) throw new Exception("informe DataInicial (ou --de) ou use --todas-datas");
                if (cfg.DataFinal == null) cfg.DataFinal = cfg.DataInicial;
                if (cfg.DataFinal < cfg.DataInicial) throw new Exception("DataFinal anterior à DataInicial");
            }
            Raiz = Path.GetFullPath(cfg.PastaRaiz);
            if (!Directory.Exists(Raiz)) throw new Exception("pasta raiz não existe: " + Raiz);
            Destino = Path.GetFullPath(cfg.PastaDestino);
            PastaRel = cfg.PastaRelatoriosEm(dirExe);
            foreach (var p in new[] { Destino, PastaRel })
                if (Dentro(Raiz, p)) throw new Exception("por segurança, " + p + " não pode ser a pasta raiz nem conter os XMLs (" + Raiz + ")");
            Directory.CreateDirectory(PastaRel);
            // Backups das linhas do banco antes do ajuste de chave, ao lado do exe.
            PastaBackups = Path.Combine(dirExe, "backups");
            // Cópias e relatórios nunca são lidos como candidatos.
            excluidas = new[] { Destino, PastaRel };

            carimbo = DateTime.Now.ToString("yyyyMMdd_HHmmss") + (semSefaz ? "_SIMULADO" : "");
            ArqLog = Path.Combine(PastaRel, "duplicidade_" + carimbo + ".log");
            Log = new Log(ArqLog);
            Historico = new Historico(Destino);
            MetreIni.Carregar(cfg);
            consultar = semSefaz ? (Func<string, List<NotaXml>, RespostaSefaz>)Simular : CriarConsultaSefaz();
        }

        public string DescricaoPeriodo
        {
            get
            {
                return Cfg.TodasDatas ? "todas as datas" :
                    Cfg.DataInicial.Value.ToString("dd/MM/yyyy") + " a " + Cfg.DataFinal.Value.ToString("dd/MM/yyyy") +
                    (DiasNotas != null ? ", só os " + DiasNotas.Count + " dia(s) das notas" : "") +
                    " (+" + Cfg.DiasExtrasCandidatos + " dia(s) para candidatos" +
                    (DiasTentativa.Count > 0 ? "; + dia(s) da tentativa de transmissão: " + string.Join(", ", DiasTentativa.Select(d => d.ToString("dd/MM/yyyy"))) : "") + ")";
            }
        }

        public List<LinhaContingencia> Contingencia;
        // Dias de dt_tentativa_emissao fora do período principal: o REJEICAO (539) é gravado quando a nota é transmitida,
        // que pode ser dias depois da emissão. Entram como período principal (os dias extras de candidatos não valem
        // para REJEICAO); só as pastas desses dias, nunca o C:\Chef\NFCe inteiro.
        public List<DateTime> DiasTentativa = new List<DateTime>();
        // Com a lista do Metre: os dias lidos (null = a faixa inteira do período escolhido na tela).
        public List<DateTime> DiasNotas;

        public void Varrer()
        {
            // Lista da aba Contingência do Metre (banco ou CSV). Com ela, o período de leitura dos XMLs
            // passa a ser o das próprias notas pendentes.
            // O erro também vai para o log (antes só aparecia na janela e o log parava em "conectando").
            try { Contingencia = Banco.Ler(Cfg, Log); }
            catch (Exception ex) { Log.Info("ERRO ao ler a contingência do Metre: " + ex.Message); throw; }
            if (Contingencia != null && Contingencia.Count > 0 && !Cfg.TodasDatas)
            {
                // O período da tela filtra a contingência (pedido de 11/11): entram as notas emitidas ou transmitidas nele.
                DateTime de = Cfg.DataInicial.Value, ate = Cfg.DataFinal.Value;
                Func<DateTime?, bool> dentro = d => d.HasValue && d.Value.Date >= de && d.Value.Date <= ate;
                var fora = Contingencia.Where(c => !dentro(c.DtEmissao ?? c.DtContingencia) && !dentro(c.DtTentativa)).ToList();
                if (fora.Count > 0)
                {
                    Contingencia = Contingencia.Except(fora).ToList();
                    Log.Tela(fora.Count + " nota(s) da contingência fora do período " + de.ToString("dd/MM/yyyy") + " a " + ate.ToString("dd/MM/yyyy") +
                        " (ignoradas): " + string.Join(", ", fora.Take(20).Select(c => c.Serie + "/" + c.Numero)) + (fora.Count > 20 ? "..." : ""));
                }
                var datas = Contingencia.Select(c => c.DtEmissao ?? c.DtContingencia).Where(d => d.HasValue).Select(d => d.Value.Date).ToList();
                if (datas.Count > 0)
                {
                    Cfg.DataInicial = datas.Min();
                    Cfg.DataFinal = datas.Max();
                    // Só os dias das notas (e os extras de candidatos de cada um): uma nota antiga esticava a faixa por meses
                    // e a leitura passava de 10 minutos no cliente (11/11: 25/03 a 06/10).
                    DiasNotas = datas.SelectMany(d => Enumerable.Range(0, Cfg.DiasExtrasCandidatos + 1).Select(i => d.AddDays(i))).Distinct().OrderBy(d => d).ToList();
                    DiasTentativa = Contingencia.Where(c => c.DtTentativa.HasValue).Select(c => c.DtTentativa.Value.Date)
                        .Where(d => d < Cfg.DataInicial.Value || d > Cfg.DataFinal.Value).Distinct().OrderBy(d => d).ToList();
                    Log.Tela("Período ajustado pela contingência do Metre: " + DescricaoPeriodo);
                }
            }
            Notas = new List<NotaXml>();
            if (Cfg.TodasDatas)
                foreach (var f in Arquivos(Raiz)) Notas.Add(NotaXml.Ler(f));
            else
            {
                DateTime fimCand = Cfg.DataFinal.Value.AddDays(Cfg.DiasExtrasCandidatos);
                // XMLs soltos direto na raiz (fora de MM_AAAA\DD): o dia vem da data de emissão gravada no XML.
                int soltos = 0, foraDoPeriodo = 0;
                foreach (var f in Directory.EnumerateFiles(Raiz, "NOTA_*", SearchOption.TopDirectoryOnly).Where(ExtensaoValida))
                {
                    var n = NotaXml.Ler(f);
                    DateTime? dia = DiaEmissao(n.DhEmi);
                    if (dia == null)
                    {
                        Log.Info("Arquivo solto na raiz sem data de emissão legível (ignorado): " + f + (n.Erro != null ? " - " + n.Erro : ""));
                        continue;
                    }
                    bool diaTentativa = DiasTentativa.Contains(dia.Value);
                    bool noPeriodo = DiasNotas != null ? DiasNotas.Contains(dia.Value) : dia >= Cfg.DataInicial.Value && dia <= fimCand;
                    if (!noPeriodo && !diaTentativa) { foraDoPeriodo++; continue; }
                    n.DataPasta = dia;
                    n.NaFaixaPrincipal = dia <= Cfg.DataFinal.Value || diaTentativa;
                    Notas.Add(n);
                    soltos++;
                }
                if (soltos + foraDoPeriodo > 0)
                    Log.Tela((soltos + foraDoPeriodo) + " XML(s) solto(s) na raiz " + Raiz + ": " + soltos + " no período (pela data de emissão), " + foraDoPeriodo + " fora");

                var dias = DiasNotas != null ? new List<DateTime>(DiasNotas) : new List<DateTime>();
                if (DiasNotas == null)
                    for (var d = Cfg.DataInicial.Value; d <= fimCand; d = d.AddDays(1)) dias.Add(d);
                // Dias da tentativa entram como o período principal: é onde pode estar o REJEICAO com a 539.
                dias.AddRange(DiasTentativa.Where(d => !dias.Contains(d)));
                foreach (var d in dias)
                {
                    string dir = Path.Combine(Raiz, d.ToString("MM_yyyy"), d.ToString("dd"));
                    if (!Directory.Exists(dir)) { Log.Info("Pasta inexistente (ignorada): " + dir); continue; }
                    foreach (var f in Arquivos(dir))
                    {
                        var n = NotaXml.Ler(f);
                        n.DataPasta = d;
                        n.NaFaixaPrincipal = d <= Cfg.DataFinal.Value || DiasTentativa.Contains(d);
                        Notas.Add(n);
                    }
                }
            }
            Log.Tela(Notas.Count + " arquivo(s) NOTA_* lido(s); " + Notas.Count(n => n.Erro != null) + " com erro de leitura; " +
                Notas.Count(n => n.TemSobra) + " com conteúdo residual após </nfeProc>");
            foreach (var n in Notas.Where(n => n.Erro != null)) Log.Info("Erro de leitura: " + n.Caminho + " - " + n.Erro);
            Analisador = new Analisador(Notas, consultar, Log, Contingencia, Cfg.UsaBanco
                ? new FontesAjuste { BuscarXmls = BuscarXmlsChave, LinhasComChave = LinhasComChave, XmlDaLinha = XmlDaLinha }
                : null);
            Analisador.XmlsDoDia = XmlsDoDiaDaAutorizacao;
        }

        // ---------- Ajuste de chave: o XML da chave autorizada guardado pelo Metre no banco ----------
        // Só o chefxml.xml da linha da contingência e os do mesmo pedido em volta dela (consultas por índice, rápidas).
        // As pastas continuam sendo só as do período: varrer C:\Chef\NFCe inteiro deixava o processamento parado por minutos.
        List<NotaXml> BuscarXmlsChave(Tentativa t, string chave)
        {
            var lista = new List<NotaXml>();
            if (t.IdMetre == null) return lista;
            Log.Tela("Nota " + t.Nota + ": procurando o XML da chave autorizada no banco " + Cfg.DbBancoXml + "...");
            foreach (var x in Banco.XmlsDaNota(Cfg, t.IdMetre, Log))
            {
                var n = NotaXml.LerConteudo(x.Key, x.Value);
                if (n.Chave == chave || n.Erro != null) lista.Add(n);
                else Log.Info("  " + x.Key + ": chave " + n.Chave + " (não é a autorizada)");
            }
            Log.Info("  XMLs da chave autorizada no banco " + Cfg.DbBancoXml + ": " + lista.Count(n => n.Chave == chave));
            return lista;
        }

        // Número reaproveitado (o XML autorizado é de um dia fora do período da contingência): a data de autorização da
        // SEFAZ diz a pasta. Só os arquivos desse número, nesse dia e no anterior (virada da meia-noite), sem varrer C:\Chef\NFCe.
        // Vale também nas PastasXmlExtras (caixa, pen drive), onde a pasta pode ter os arquivos soltos.
        List<NotaXml> XmlsDoDiaDaAutorizacao(string chave, string dhRecbto)
        {
            var lista = new List<NotaXml>();
            DateTime? dia = DiaEmissao(dhRecbto);
            string mascara = "NOTA_" + long.Parse(chave.Substring(25, 9)) + "_*";
            Action<string, SearchOption> ler = (dir, modo) =>
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, mascara, modo).Where(ExtensaoValida).Where(f => !excluidas.Any(x => Dentro(f, x))))
                {
                    var n = NotaXml.Ler(f);
                    if (n.Chave == chave) lista.Add(n);
                }
            };
            Action<string> lerDias = raiz =>
            {
                if (dia == null) return;
                foreach (var d in new[] { dia.Value, dia.Value.AddDays(-1) })
                    ler(Path.Combine(raiz, d.ToString("MM_yyyy"), d.ToString("dd")), SearchOption.AllDirectories);
            };
            lerDias(Raiz);
            foreach (string extra in ExtrasAcessiveis())
            {
                try
                {
                    ler(extra, SearchOption.TopDirectoryOnly);
                    lerDias(extra);
                }
                catch (Exception ex) { Log.Info("  pasta extra " + extra + ": " + ex.Message); }
            }
            return lista;
        }

        // Caixa desligado faz cada acesso à rede demorar: cada pasta extra é testada uma vez só, no primeiro uso.
        List<string> extrasAcessiveis;
        List<string> ExtrasAcessiveis()
        {
            if (extrasAcessiveis != null) return extrasAcessiveis;
            extrasAcessiveis = new List<string>();
            foreach (string p in (Cfg.PastasXmlExtras ?? "").Split(';').Select(x => x.Trim()).Where(x => x != ""))
            {
                if (Directory.Exists(p)) extrasAcessiveis.Add(p);
                else Log.Tela("Pasta extra de XMLs inacessível (ignorada): " + p);
            }
            return extrasAcessiveis;
        }

        NotaXml XmlDaLinha(Tentativa t)
        {
            if (t.IdMetre == null) return null;
            var x = Banco.XmlDaLinha(Cfg, t.IdMetre, Log);
            return x.Key == null ? null : NotaXml.LerConteudo(x.Key, x.Value);
        }

        List<LinhaContingencia> LinhasComChave(string chave)
        {
            var inicio = DateTime.Now;
            using (var s = Banco.Sessao.Abrir(Cfg, x => Log.Info("Banco: " + x), true))
            {
                var ls = Banco.LinhasComChave(s, Cfg, chave);
                Log.Info("  linhas de nota_saida com a chave autorizada: " + (ls.Count == 0 ? "nenhuma" : string.Join(", ", ls.Select(Analisador.DescreverLinha))) +
                    " (" + (DateTime.Now - inicio).TotalSeconds.ToString("0.0") + "s)");
                return ls;
            }
        }

        public List<LinhaRelatorio> Listar()
        {
            return Analisador.Listar(Historico);
        }

        // Cada processamento copia numa pasta própria (Duplicidade\AAAA-MM-DD\16h50m12s\importar-N\...), para
        // nunca esbarrar nas cópias de uma execução anterior que ainda vão ser importadas.
        public string UltimaPastaExecucao;

        public string NovaPastaExecucao()
        {
            UltimaPastaExecucao = Path.Combine(PastaCopia, DateTime.Now.ToString("HH'h'mm'm'ss's'"));
            return UltimaPastaExecucao;
        }

        public List<LinhaRelatorio> Processar(Func<Tentativa, bool> incluir, IEnumerable<string> notasSemTentativa, bool copiar, Action<int, int> progresso)
        {
            if (copiar && SemSefaz) throw new Exception("a cópia exige a confirmação da SEFAZ (modo --sem-sefaz)");
            var linhas = Analisador.Analisar(NovaPastaExecucao(), incluir, notasSemTentativa, Historico, progresso);
            if (copiar)
            {
                Log.Info("");
                Copiador.Executar(linhas, Historico, Log);
            }
            return linhas;
        }

        public string GravarRelatorio(List<LinhaRelatorio> linhas)
        {
            // Um carimbo só: o nome com _2, _3... mantém o mesmo horário e o _SIMULADO do modo de teste.
            string nome = Path.Combine(PastaRel, "duplicidade_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + (SemSefaz ? "_SIMULADO" : ""));
            string arq = nome + ".csv";
            for (int i = 2; File.Exists(arq); i++) arq = nome + "_" + i + ".csv";
            Relatorio.GravarCsv(arq, linhas);
            return arq;
        }

        Func<string, List<NotaXml>, RespostaSefaz> CriarConsultaSefaz()
        {
            var f = ClienteSefaz.CriarConsulta(Cfg, Log, Path.Combine(PastaRel, "sefaz_" + carimbo));
            return (k, arqs) => f(k);
        }

        // Modo de teste: o protNFe autorizado gravado no próprio XML faz o papel da resposta da SEFAZ.
        static RespostaSefaz Simular(string chave, List<NotaXml> arqs)
        {
            var a = arqs.FirstOrDefault(x => x.Prot != null && x.Prot.ChNFe == chave && ProtocoloXml.Autorizado(x.Prot.CStat));
            if (a == null)
                return new RespostaSefaz { Ok = false, Simulado = true, Erro = "simulação: nenhum protNFe autorizado local para a chave" };
            return new RespostaSefaz
            {
                Ok = true, Simulado = true, CStat = a.Prot.CStat, XMotivo = a.Prot.XMotivo + " (de " + a.Nome + ")",
                DhRecbto = a.Prot.DhRecbto, Prot = a.Prot
            };
        }

        IEnumerable<string> Arquivos(string dir)
        {
            return Directory.EnumerateFiles(dir, "NOTA_*", SearchOption.AllDirectories)
                .Where(ExtensaoValida)
                .Where(f => !excluidas.Any(x => Dentro(f, x)));
        }

        static bool ExtensaoValida(string f)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            return e == ".xml" || e == "";
        }

        // Dia da emissão no fuso gravado no próprio XML (ex.: 2026-10-19T17:05:40-03:00 → 19/10/2026).
        static DateTime? DiaEmissao(string dhEmi)
        {
            var d = NotaXml.DataHora(dhEmi);
            return d.HasValue ? d.Value.Date : (DateTime?)null;
        }

        public static bool Dentro(string caminho, string pasta)
        {
            string c = caminho.TrimEnd('\\') + "\\", p = pasta.TrimEnd('\\') + "\\";
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() { Log.Dispose(); }
    }
}
