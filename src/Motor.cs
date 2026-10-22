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
        public readonly string Raiz, Destino, PastaRel, ArqLog;
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

        public void Varrer()
        {
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
            Analisador = new Analisador(Notas, consultar, Log);
        }

        public List<LinhaRelatorio> Listar()
        {
            return Analisador.Listar(Historico);
        }

        public List<LinhaRelatorio> Processar(Func<Tentativa, bool> incluir, IEnumerable<string> notasSemTentativa, bool copiar, Action<int, int> progresso)
        {
            if (copiar && SemSefaz) throw new Exception("a cópia exige a confirmação da SEFAZ (modo --sem-sefaz)");
            var linhas = Analisador.Analisar(PastaCopia, incluir, notasSemTentativa, Historico, progresso);
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
            string pastaSefaz = Path.Combine(PastaRel, "sefaz_" + carimbo);
            var clientes = new Dictionary<string, ClienteSefaz>();
            var semCertificado = new Dictionary<string, string>();
            return (k, arqs) =>
            {
                // Um certificado por CNPJ emitente (posições 7-20 da chave), carregado na primeira consulta.
                string cnpj = k.Substring(6, 14);
                ClienteSefaz cli;
                string erro;
                if (!clientes.TryGetValue(cnpj, out cli) && !semCertificado.TryGetValue(cnpj, out erro))
                {
                    Log.Tela("Consulta SEFAZ: " + ClienteSefaz.UrlConsulta(Cfg) + " (ambiente " + Cfg.Ambiente + ")");
                    if (!Cfg.ValidarCertificadoServidor) Log.Tela("AVISO: validação do certificado do servidor da SEFAZ desligada.");
                    string arqPfx;
                    var cert = ClienteSefaz.CarregarCertificado(Cfg, cnpj, Log, out erro, out arqPfx);
                    if (cert == null)
                    {
                        semCertificado[cnpj] = erro;
                        Log.Tela("SEFAZ NÃO será consultada para o CNPJ " + cnpj + ": " + erro);
                    }
                    else
                    {
                        clientes[cnpj] = cli = new ClienteSefaz(Cfg, cert);
                        Log.Tela("Certificado CNPJ " + cnpj + ": " + arqPfx + " - " + cert.Subject + " (validade " + cert.NotAfter.ToString("dd/MM/yyyy") + ")");
                    }
                }
                if (cli == null)
                    return new RespostaSefaz { Ok = false, NaoConsultada = true, Erro = semCertificado[cnpj] };
                var r = cli.Consultar(k);
                if (r.XmlBruto != null)
                {
                    Directory.CreateDirectory(pastaSefaz);
                    File.WriteAllText(Path.Combine(pastaSefaz, k + ".xml"), r.XmlBruto, new UTF8Encoding(false));
                }
                return r;
            };
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
