using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

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
            PastaRel = Path.GetFullPath(Path.IsPathRooted(cfg.PastaRelatorios) ? cfg.PastaRelatorios : Path.Combine(dirExe, cfg.PastaRelatorios));
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
                    " (+" + Cfg.DiasExtrasCandidatos + " dia(s) para candidatos)";
            }
        }

        public List<LinhaContingencia> Contingencia;

        public void Varrer()
        {
            // Lista da aba Contingência do Metre (banco ou CSV). Com ela, o período de leitura dos XMLs
            // passa a ser o das próprias notas pendentes.
            // O erro também vai para o log (antes só aparecia na janela e o log parava em "conectando").
            try { Contingencia = Banco.Ler(Cfg, Log); }
            catch (Exception ex) { Log.Info("ERRO ao ler a contingência do Metre: " + ex.Message); throw; }
            if (Contingencia != null && Contingencia.Count > 0 && !Cfg.TodasDatas)
            {
                var datas = Contingencia.Select(c => c.DtEmissao ?? c.DtContingencia).Where(d => d.HasValue).Select(d => d.Value.Date).ToList();
                if (datas.Count > 0)
                {
                    Cfg.DataInicial = datas.Min();
                    Cfg.DataFinal = datas.Max();
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
                    if (dia < Cfg.DataInicial.Value || dia > fimCand) { foraDoPeriodo++; continue; }
                    n.DataPasta = dia;
                    n.NaFaixaPrincipal = dia <= Cfg.DataFinal.Value;
                    Notas.Add(n);
                    soltos++;
                }
                if (soltos + foraDoPeriodo > 0)
                    Log.Tela((soltos + foraDoPeriodo) + " XML(s) solto(s) na raiz " + Raiz + ": " + soltos + " no período (pela data de emissão), " + foraDoPeriodo + " fora");

                for (var d = Cfg.DataInicial.Value; d <= fimCand; d = d.AddDays(1))
                {
                    string dir = Path.Combine(Raiz, d.ToString("MM_yyyy"), d.ToString("dd"));
                    if (!Directory.Exists(dir)) { Log.Info("Pasta inexistente (ignorada): " + dir); continue; }
                    foreach (var f in Arquivos(dir))
                    {
                        var n = NotaXml.Ler(f);
                        n.DataPasta = d;
                        n.NaFaixaPrincipal = d <= Cfg.DataFinal.Value;
                        Notas.Add(n);
                    }
                }
            }
            Log.Tela(Notas.Count + " arquivo(s) NOTA_* lido(s); " + Notas.Count(n => n.Erro != null) + " com erro de leitura; " +
                Notas.Count(n => n.TemSobra) + " com conteúdo residual após </nfeProc>");
            foreach (var n in Notas.Where(n => n.Erro != null)) Log.Info("Erro de leitura: " + n.Caminho + " - " + n.Erro);
            bool comBanco = (Cfg.FonteContingencia ?? "").Trim().Equals("banco", StringComparison.OrdinalIgnoreCase);
            Analisador = new Analisador(Notas, consultar, Log, Contingencia, comBanco
                ? new FontesAjuste { BuscarXmls = BuscarXmlsChave, LinhasComChave = LinhasComChave }
                : null);
        }

        // ---------- Ajuste de chave: o XML da chave autorizada fora das pastas lidas ----------
        Dictionary<string, List<string>> indiceChaves;

        // Qualquer XML com essa chave no Id: em qualquer subpasta da raiz, com qualquer nome, e os que o Metre
        // guardou no banco (chefxml.xml) para a linha da contingência.
        List<NotaXml> BuscarXmlsChave(Tentativa t, string chave)
        {
            var lista = new List<NotaXml>();
            if (indiceChaves == null) IndexarChaves();
            List<string> arqs;
            if (indiceChaves.TryGetValue(chave, out arqs))
                lista.AddRange(arqs.Select(NotaXml.Ler));
            if (t.IdMetre != null)
                Log.Tela("Nota " + t.Nota + ": procurando o XML da chave autorizada no banco " + Cfg.DbBancoXml + "...");
            if (t.IdMetre != null)
                foreach (var x in Banco.XmlsDaNota(Cfg, t.IdMetre, Log))
                {
                    var n = NotaXml.LerConteudo(x.Key, x.Value);
                    if (n.Chave == chave || n.Erro != null) lista.Add(n);
                    else Log.Info("  " + x.Key + ": chave " + n.Chave + " (não é a autorizada)");
                }
            Log.Info("  XMLs da chave autorizada fora das pastas lidas: " + lista.Count(n => n.Chave == chave) +
                " (" + indiceChaves.Values.Sum(v => v.Count) + " arquivo(s) indexado(s) em " + Raiz + " e banco " + Cfg.DbBancoXml + ")");
            return lista;
        }

        static readonly System.Text.RegularExpressions.Regex ReIdNFe = new System.Text.RegularExpressions.Regex(@"Id=""NFe(\d{44})""");

        void IndexarChaves()
        {
            indiceChaves = new Dictionary<string, List<string>>();
            int lidos = 0;
            var inicio = DateTime.Now;
            Log.Tela("Procurando o XML da chave autorizada em " + Raiz + " (todas as subpastas)...");
            var buf = new byte[4096];
            foreach (var f in Directory.EnumerateFiles(Raiz, "*", SearchOption.AllDirectories)
                .Where(ExtensaoValida).Where(f => !excluidas.Any(x => Dentro(f, x))))
            {
                // O Id do infNFe fica no começo do arquivo (nfeProc, enviNFe ou NFe): basta o início de cada um.
                string texto;
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int n = 0, r;
                        while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                        texto = Encoding.UTF8.GetString(buf, 0, n);
                    }
                }
                catch (Exception ex) { Log.Info("  sem leitura (ignorado na busca): " + f + " - " + ex.Message); continue; }
                if (++lidos % 5000 == 0) Log.Tela("Procurando o XML da chave autorizada em " + Raiz + ": " + lidos + " arquivo(s) lido(s)...");
                foreach (var k in ReIdNFe.Matches(texto).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct())
                {
                    List<string> l;
                    if (!indiceChaves.TryGetValue(k, out l)) indiceChaves[k] = l = new List<string>();
                    l.Add(f);
                }
            }
            Log.Info("Índice de chaves para o ajuste: " + lidos + " arquivo(s) em " + Raiz + " (todas as subpastas, qualquer nome) em " +
                (int)(DateTime.Now - inicio).TotalSeconds + "s");
        }

        List<string> LinhasComChave(string chave)
        {
            var inicio = DateTime.Now;
            using (var s = Banco.Sessao.Abrir(Cfg, x => Log.Info("Banco: " + x), true))
            {
                var ids = Banco.IdsComChave(s, Cfg, chave);
                Log.Info("  linhas de nota_saida com a chave autorizada: " + (ids.Count == 0 ? "nenhuma" : string.Join(", ", ids)) +
                    " (" + (DateTime.Now - inicio).TotalSeconds.ToString("0.0") + "s)");
                return ids;
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
            string arq = Path.Combine(PastaRel, "duplicidade_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + (SemSefaz ? "_SIMULADO" : "") + ".csv");
            for (int i = 2; File.Exists(arq); i++)
                arq = Path.Combine(PastaRel, "duplicidade_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + i + ".csv");
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
            var a = arqs.FirstOrDefault(x => x.Prot != null && x.Prot.ChNFe == chave && (x.Prot.CStat == "100" || x.Prot.CStat == "150"));
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
            DateTimeOffset d;
            if (dhEmi != null && DateTimeOffset.TryParse(dhEmi, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d.DateTime.Date;
            return null;
        }

        public static bool Dentro(string caminho, string pasta)
        {
            string c = caminho.TrimEnd('\\') + "\\", p = pasta.TrimEnd('\\') + "\\";
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() { Log.Dispose(); }
    }
}
