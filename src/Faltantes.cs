using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DuplicidadeNfce
{
    // ===================== Notas Faltantes =====================
    // O relatório "Notas não lançadas de Saídas" lista números sem XML no fiscal. Para cada número digitado,
    // acha o XML da nota (ou do cancelamento/inutilização), confere na SEFAZ e copia para entregar ao cliente.

    static class StatusFaltante
    {
        public const string Encontrada = "ENCONTRADA";
        public const string Cancelada = "CANCELADA";
        public const string CanceladaSemEvento = "CANCELADA – SEM XML DO EVENTO";
        public const string Inutilizada = "INUTILIZADA";
        public const string NaoConsta = "NÃO CONSTA NA SEFAZ";
        public const string NaoEncontrada = "NÃO ENCONTRADA";
        public const string Pendente = "PENDENTE";

        public static bool Entrega(string s) { return s == Encontrada || s == Cancelada || s == CanceladaSemEvento || s == Inutilizada; }
    }

    static class CopiaFaltante
    {
        public const string Copiado = "COPIADO";
        public const string JaCopiado = "JÁ COPIADO";
        public const string Erro = "ERRO_COPIA";
        public const string NaoCopiado = "não copiado (só relatório)";
    }

    // Números digitados a partir da foto do relatório: "1201, 1202" (vírgula, ponto e vírgula, espaço ou
    // linha) e faixas com hífen ("1201-1205"). Repetidos e inválidos são avisados sem travar os demais.
    class EntradaNumeros
    {
        public List<long> Numeros = new List<long>();
        public List<string> Invalidos = new List<string>();
        public List<long> Repetidos = new List<long>();

        public static EntradaNumeros Ler(string texto, int limite)
        {
            var e = new EntradaNumeros();
            var vistos = new HashSet<long>();
            string t = Regex.Replace(texto ?? "", @"\s*[-–]\s*", "-");
            foreach (string item in Regex.Split(t, @"[,;\s]+"))
            {
                if (item == "") continue;
                var m = Regex.Match(item, @"^(\d{1,9})(?:-(\d{1,9}))?$");
                long ini, fim;
                if (!m.Success || !long.TryParse(m.Groups[1].Value, out ini)) { e.Invalidos.Add(item); continue; }
                fim = m.Groups[2].Success ? long.Parse(m.Groups[2].Value) : ini;
                if (ini <= 0 || fim < ini) { e.Invalidos.Add(item + (fim < ini ? " (faixa invertida)" : "")); continue; }
                if (fim - ini + 1 > limite) { e.Invalidos.Add(item + " (faixa com mais de " + limite + " números)"); continue; }
                for (long n = ini; n <= fim; n++)
                {
                    if (vistos.Add(n)) e.Numeros.Add(n);
                    else if (!e.Repetidos.Contains(n)) e.Repetidos.Add(n);
                }
            }
            return e;
        }

        public string Avisos()
        {
            var l = new List<string>();
            if (Invalidos.Count > 0) l.Add(Invalidos.Count + " inválido(s) ignorado(s): " + string.Join(", ", Invalidos));
            if (Repetidos.Count > 0) l.Add(Repetidos.Count + " repetido(s), buscado(s) uma vez: " + string.Join(", ", Repetidos));
            return string.Join(" · ", l);
        }
    }

    class ArquivoFaltante
    {
        public string Caminho, Nome, Tipo, Motivo;
        public bool Aceito;
    }

    // Uma linha do resultado: um número pedido.
    class ItemFaltante
    {
        public long Numero;
        public int Serie;
        public string Status = StatusFaltante.Pendente, Motivo, Copia = "";
        public string Chave, DhEmi, SituacaoSefaz, CStat, NProt, DigVal;
        public string Cnpj, Emitente;   // emitente descoberto pelos XMLs do número
        public bool ForaDoMesPrincipal; // emitida num mês diferente do da maioria das notas encontradas

        public string MesEmissao
        {
            get { var d = MotorFaltantes.DataEmissao(DhEmi); return d.HasValue ? d.Value.ToString("MM/yyyy") : null; }
        }
        public decimal? Valor;
        public List<ArquivoFaltante> Arquivos = new List<ArquivoFaltante>();
        public List<string> Destinos = new List<string>();
        // O que copiar ou gerar (preenchido na análise, executado na cópia).
        internal List<Entrega> Entregas = new List<Entrega>();

        public string Nota { get { return Serie + "/" + Numero; } }
    }

    // Um arquivo a entregar: cópia de um XML do disco ou XML montado com a resposta da SEFAZ.
    class Entrega
    {
        public string Origem;        // arquivo do disco (cópia)
        public string Sha256;        // hash do original no momento da análise
        public long Tamanho;
        public string NomeDestino;
        public string Conteudo;      // XML gerado (procNFe / procEventoNFe da SEFAZ)
        public string Descricao;
        public Func<string, string> Conferir;  // confere o arquivo gerado; devolve o erro ou null
    }

    class InutilizacaoXml
    {
        public string Caminho, Nome, Sha256, Cnpj, Mod, Ano, CStat, XMotivo, NProt;
        public long Tamanho;
        public int Serie;
        public long Inicial, Final;
        public bool Homologada { get { return CStat == "102"; } }
    }

    class EventoXml
    {
        public string Caminho, Nome, Sha256, ChNFe, TpEvento, CStat, NProt;
        public long Tamanho;
        public bool Cancelamento { get { return (TpEvento == "110111" || TpEvento == "110112") && (CStat == "135" || CStat == "155"); } }
    }

    class HistoricoFaltantes
    {
        public const string NomeArquivo = "historico.csv";
        static readonly string[] Cabecalho =
            { "Data/hora", "CNPJ", "Serie", "Numero", "Chave", "Arquivo", "Caminho origem", "Caminho destino", "Tamanho", "SHA256", "Status" };

        public class Registro { public string DataHora, Cnpj, Serie, Numero, Chave, Arquivo, CaminhoOrigem, CaminhoDestino, Tamanho, Sha256, Status; }

        readonly string caminho;
        public readonly List<Registro> Todos = new List<Registro>();

        public HistoricoFaltantes(string pastaBase)
        {
            caminho = Path.Combine(pastaBase, NomeArquivo);
            if (!File.Exists(caminho)) return;
            bool primeira = true;
            foreach (string linha in File.ReadAllLines(caminho, Encoding.UTF8))
            {
                if (primeira) { primeira = false; continue; }
                var c = Csv.Dividir(linha);
                if (c.Count < Cabecalho.Length) continue;
                Todos.Add(new Registro
                {
                    DataHora = c[0], Cnpj = Csv.SemFormula(c[1]), Serie = c[2], Numero = c[3], Chave = Csv.SemFormula(c[4]), Arquivo = c[5],
                    CaminhoOrigem = c[6], CaminhoDestino = c[7], Tamanho = c[8], Sha256 = c[9], Status = c[10]
                });
            }
        }

        public string Caminho { get { return caminho; } }

        public Registro BuscarCopiado(string cnpj, int serie, long numero)
        {
            string s = serie.ToString(), n = numero.ToString();
            return Todos.LastOrDefault(r => r.Status == CopiaFaltante.Copiado && r.Cnpj == cnpj && r.Serie == s && r.Numero == n);
        }

        public void Registrar(Registro r)
        {
            bool novo = !File.Exists(caminho);
            var sb = new StringBuilder();
            if (novo) sb.AppendLine(string.Join(";", Cabecalho));
            sb.AppendLine(string.Join(";", new[]
            {
                r.DataHora, Csv.Formula(r.Cnpj), r.Serie, r.Numero, Csv.Formula(r.Chave), r.Arquivo, r.CaminhoOrigem, r.CaminhoDestino, r.Tamanho, r.Sha256, r.Status
            }.Select(Csv.Campo)));
            Directory.CreateDirectory(Path.GetDirectoryName(caminho));
            if (novo) File.WriteAllText(caminho, sb.ToString(), new UTF8Encoding(true));
            else File.AppendAllText(caminho, sb.ToString(), new UTF8Encoding(false));
            Todos.Add(r);
        }
    }

    // Uma busca (linha de comando ou interface).
    class MotorFaltantes : IDisposable
    {
        const string NsNfe = "http://www.portalfiscal.inf.br/nfe";

        public readonly Config Cfg;
        public readonly Log Log;
        public readonly string Raiz, Base, PastaRel, ArqLog;
        public readonly HistoricoFaltantes Historico;
        // Emitente da busca, descoberto pelos XMLs (o que aparece em mais números); nomeia a pasta da entrega.
        public string Cnpj, Emitente, DescricaoEmitente;
        public int Serie;
        // Meses de emissão das notas encontradas (MM/AAAA -> quantidade), do mais frequente ao menos.
        public List<KeyValuePair<string, int>> Meses = new List<KeyValuePair<string, int>>();
        public List<ItemFaltante> Itens = new List<ItemFaltante>();
        public string PastaExecucao, ArqCsv;
        readonly string carimbo;
        readonly string[] excluidas;
        readonly Func<string, RespostaSefaz> consultar;

        public MotorFaltantes(Config cfg, string dirExe, Func<string, RespostaSefaz> consultaTeste = null)
        {
            Cfg = cfg;
            Raiz = Path.GetFullPath(cfg.PastaRaiz);
            if (!Directory.Exists(Raiz)) throw new Exception("pasta raiz não existe: " + Raiz);
            Base = Path.GetFullPath(cfg.PastaNotasFaltantes);
            PastaRel = Path.GetFullPath(Path.IsPathRooted(cfg.PastaRelatorios) ? cfg.PastaRelatorios : Path.Combine(dirExe, cfg.PastaRelatorios));
            foreach (var p in new[] { Base, PastaRel })
                if (Motor.Dentro(Raiz, p)) throw new Exception("por segurança, " + p + " não pode ser a pasta raiz nem conter os XMLs (" + Raiz + ")");
            Directory.CreateDirectory(PastaRel);
            // As entregas, as cópias da duplicidade e os relatórios nunca entram como candidatos.
            excluidas = new[] { Base, Path.GetFullPath(cfg.PastaDestino), PastaRel };
            carimbo = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            ArqLog = Path.Combine(PastaRel, "faltantes_" + carimbo + ".log");
            Log = new Log(ArqLog);
            Historico = new HistoricoFaltantes(Base);
            MetreIni.Carregar(cfg);
            consultar = consultaTeste ?? ClienteSefaz.CriarConsulta(cfg, Log, Path.Combine(PastaRel, "sefaz_faltantes_" + carimbo));
        }

        // ---------- Busca ----------

        static readonly Regex ReNomeNota = new Regex(@"^NOTA_(?:ONTIGENCIA_ENVIA_)?(\d+)_(?:MODELO_\d+_)?SERIE_(\d+)_", RegexOptions.IgnoreCase);

        public List<ItemFaltante> Executar(int serie, List<long> numeros, bool copiar, bool recopiar, Action<int, int> progresso)
        {
            Serie = serie;
            if (numeros.Count == 0) throw new Exception("nenhum número informado");
            if (numeros.Count > Cfg.LimiteNumerosFaltantes)
                throw new Exception(numeros.Count + " números a buscar passam do limite de " + Cfg.LimiteNumerosFaltantes +
                    " (confira os números ou aumente LimiteNumerosFaltantes no duplicidade.ini)");

            Log.Tela(Program.NomeSistema + " - Notas Faltantes - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + (copiar ? "" : " - só relatório"));
            Log.Tela("Série " + serie + " · " + numeros.Count + " número(s): " + string.Join(", ", numeros));
            Log.Tela("Varrendo " + Raiz + " (todas as subpastas)...");

            var alvo = new HashSet<string>(numeros.Select(n => serie + "/" + n));
            var candidatos = new Dictionary<string, List<NotaXml>>();
            var inuts = new List<InutilizacaoXml>();
            var eventos = new List<EventoXml>();
            int examinados = 0;
            foreach (string f in Directory.EnumerateFiles(Raiz, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".xml" && ext != "") continue;
                if (excluidas.Any(x => Motor.Dentro(f, x))) continue;
                examinados++;
                string nome = Path.GetFileName(f);
                var m = ReNomeNota.Match(nome);
                if (m.Success)
                {
                    // O nome só pré-filtra: série e número são confirmados pelo conteúdo.
                    string k = int.Parse(m.Groups[2].Value) + "/" + long.Parse(m.Groups[1].Value);
                    if (alvo.Contains(k))
                    {
                        List<NotaXml> l;
                        if (!candidatos.TryGetValue(k, out l)) candidatos[k] = l = new List<NotaXml>();
                        l.Add(NotaXml.Ler(f));
                    }
                    if (!Regex.IsMatch(nome, "CANC|INUT|EVENTO", RegexOptions.IgnoreCase)) continue;
                }
                Farejar(f, inuts, eventos);
            }
            Log.Tela(examinados + " XML(s) examinado(s); " + candidatos.Values.Sum(l => l.Count) + " arquivo(s) com número da lista; " +
                inuts.Count + " XML(s) de inutilização; " + eventos.Count + " XML(s) de evento");
            foreach (var i in inuts)
                Log.Info("Inutilização: " + i.Caminho + " - CNPJ " + i.Cnpj + " mod " + i.Mod + " série " + i.Serie + " " + i.Inicial + " a " + i.Final + " cStat " + (i.CStat ?? "(sem retorno)"));
            foreach (var e in eventos)
                Log.Info("Evento: " + e.Caminho + " - chave " + e.ChNFe + " tpEvento " + e.TpEvento + " cStat " + (e.CStat ?? "(sem retorno)"));

            Itens = new List<ItemFaltante>();
            int feitos = 0;
            foreach (var n in numeros)
            {
                if (progresso != null) progresso(feitos, numeros.Count);
                List<NotaXml> l;
                candidatos.TryGetValue(serie + "/" + n, out l);
                var item = Analisar(serie, n, l ?? new List<NotaXml>(), inuts, eventos);
                Itens.Add(item);
                feitos++;
            }
            if (progresso != null) progresso(feitos, numeros.Count);

            // O CNPJ não é informado: vem dos XMLs. No servidor do cliente só existe um emitente.
            var porEmitente = Itens.Where(i => i.Cnpj != null).GroupBy(i => i.Cnpj).OrderByDescending(g => g.Count()).ToList();
            if (porEmitente.Count == 0)
            {
                DescricaoEmitente = "nenhum emitente encontrado nos XMLs";
                Log.Tela("Emitente: " + DescricaoEmitente);
            }
            else
            {
                Cnpj = porEmitente[0].Key;
                Emitente = porEmitente[0].Select(i => i.Emitente).FirstOrDefault(x => x != null);
                DescricaoEmitente = FormatarCnpj(Cnpj) + (Emitente != null ? " - " + Emitente : "") +
                    (porEmitente.Count > 1 ? " · também: " + string.Join(", ", porEmitente.Skip(1).Select(g => FormatarCnpj(g.Key) + " (" + g.Count() + " número(s))")) : "");
                Log.Tela("Emitente: " + DescricaoEmitente);
            }
            string idEmitente = Cnpj ?? "SEM-EMITENTE";

            // Sem mês de referência: os meses de emissão aparecem, com destaque se as notas forem de meses diferentes.
            Meses = Itens.Where(i => i.MesEmissao != null).GroupBy(i => i.MesEmissao).OrderByDescending(g => g.Count())
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count())).ToList();
            if (Meses.Count > 0)
            {
                foreach (var i in Itens) i.ForaDoMesPrincipal = i.MesEmissao != null && i.MesEmissao != Meses[0].Key;
                Log.Tela("Emissão: " + DescricaoMeses + (Meses.Count > 1 ? " · ATENÇÃO: notas de meses diferentes" : ""));
            }

            if (copiar)
            {
                PastaExecucao = Path.Combine(Base, idEmitente + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH'h'mm'm'ss's'"));
                // Duas buscas no mesmo segundo não podem cair na mesma pasta.
                string pastaBase = PastaExecucao;
                for (int n = 2; Directory.Exists(PastaExecucao); n++) PastaExecucao = pastaBase + "_" + n;
                Directory.CreateDirectory(PastaExecucao);
                Log.Info("");
                Log.Tela("Copiando para " + PastaExecucao);
                Copiar(recopiar);
                ArqCsv = Path.Combine(PastaExecucao, "relatorio_" + idEmitente + ".csv");
            }
            else
            {
                foreach (var i in Itens.Where(x => x.Entregas.Count > 0)) i.Copia = CopiaFaltante.NaoCopiado;
                ArqCsv = Path.Combine(PastaRel, "faltantes_" + idEmitente + "_" + carimbo + ".csv");
            }
            GravarCsv(ArqCsv, Itens);

            Log.Tela("");
            foreach (var i in Itens)
                Log.Tela(string.Format("{0,-14} {1,-30} {2}", i.Nota, i.Status, i.Destinos.Count > 0 ? string.Join(" + ", i.Destinos.Select(Path.GetFileName)) : i.Motivo));
            Log.Tela("");
            Log.Tela(Itens.Count + " número(s): " + string.Join(", ", Itens.GroupBy(i => i.Status).Select(g => g.Count() + " " + g.Key)));
            if (PastaExecucao != null) Log.Tela("Entrega:   " + PastaExecucao);
            Log.Tela("Relatório: " + ArqCsv);
            Log.Tela("Log:       " + ArqLog);
            return Itens;
        }

        // Arquivos fora do padrão NOTA_*: lê o começo e, se for inutilização ou evento, interpreta.
        void Farejar(string f, List<InutilizacaoXml> inuts, List<EventoXml> eventos)
        {
            try
            {
                string inicio;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buf = new byte[8192];
                    int lidos = fs.Read(buf, 0, buf.Length);
                    inicio = Encoding.UTF8.GetString(buf, 0, lidos);
                }
                bool inut = Regex.IsMatch(inicio, "<(procInutNFe|retInutNFe|inutNFe)[\\s>]");
                bool evento = Regex.IsMatch(inicio, "<(procEventoNFe|evento|envEvento|retEnvEvento)[\\s>]");
                if (!inut && !evento) return;
                byte[] bytes = NotaXml.LerSomenteLeitura(f);
                var xd = new XmlDocument { XmlResolver = null };
                using (var sr = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('﻿')))
                using (var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    xd.Load(xr);
                var ns = new XmlNamespaceManager(xd.NameTable);
                ns.AddNamespace("n", NsNfe);
                if (inut) inuts.Add(LerInutilizacao(f, bytes, xd, ns));
                else eventos.AddRange(LerEventos(f, bytes, xd, ns));
            }
            catch (Exception ex)
            {
                Log.Info("Não foi possível ler " + f + ": " + ex.Message);
            }
        }

        static InutilizacaoXml LerInutilizacao(string f, byte[] bytes, XmlDocument xd, XmlNamespaceManager ns)
        {
            // O retorno (retInutNFe) é o que comprova; o pedido (inutNFe) completa os campos que faltarem.
            var ret = xd.SelectSingleNode("//n:retInutNFe/n:infInut", ns);
            var ped = xd.SelectSingleNode("//n:inutNFe/n:infInut", ns);
            Func<string, string> campo = t => Txt(ret, t, ns) ?? Txt(ped, t, ns);
            long ini, fim;
            int serie;
            long.TryParse(campo("n:nNFIni"), out ini);
            long.TryParse(campo("n:nNFFin"), out fim);
            int.TryParse(campo("n:serie"), out serie);
            return new InutilizacaoXml
            {
                Caminho = f, Nome = Path.GetFileName(f), Sha256 = NotaXml.HashSha256(bytes), Tamanho = bytes.LongLength,
                Cnpj = campo("n:CNPJ"), Mod = campo("n:mod"), Ano = campo("n:ano"), Serie = serie, Inicial = ini, Final = fim,
                CStat = Txt(ret, "n:cStat", ns), XMotivo = Txt(ret, "n:xMotivo", ns), NProt = Txt(ret, "n:nProt", ns)
            };
        }

        static IEnumerable<EventoXml> LerEventos(string f, byte[] bytes, XmlDocument xd, XmlNamespaceManager ns)
        {
            string sha = NotaXml.HashSha256(bytes);
            var procs = xd.SelectNodes("//n:procEventoNFe", ns).Cast<XmlNode>().ToList();
            if (procs.Count == 0) procs = xd.SelectNodes("//n:evento/..", ns).Cast<XmlNode>().ToList();
            foreach (var p in procs)
                yield return new EventoXml
                {
                    Caminho = f, Nome = Path.GetFileName(f), Sha256 = sha, Tamanho = bytes.LongLength,
                    ChNFe = Txt(p, "n:evento/n:infEvento/n:chNFe", ns), TpEvento = Txt(p, "n:evento/n:infEvento/n:tpEvento", ns),
                    // Só o procEventoNFe (evento + retorno da SEFAZ) comprova o cancelamento.
                    CStat = p.LocalName == "procEventoNFe" ? Txt(p, "n:retEvento/n:infEvento/n:cStat", ns) : null,
                    NProt = p.LocalName == "procEventoNFe" ? Txt(p, "n:retEvento/n:infEvento/n:nProt", ns) : null
                };
        }

        static string Txt(XmlNode raiz, string xp, XmlNamespaceManager ns)
        {
            if (raiz == null) return null;
            var n = raiz.SelectSingleNode(xp, ns);
            return n == null ? null : n.InnerText.Trim();
        }

        // ---------- Classificação de um número ----------

        static bool Descartavel(NotaXml n)
        {
            return n.Tipo == TipoArquivo.Rejeicao || n.Tipo == TipoArquivo.FalhaSchema || n.Tipo == TipoArquivo.Temp ||
                n.Tipo == TipoArquivo.ContingenciaEnvia || n.Nome.ToUpperInvariant().Contains("ERRO_SCHEMA");
        }

        ItemFaltante Analisar(int serie, long numero, List<NotaXml> arquivos, List<InutilizacaoXml> inuts, List<EventoXml> eventos)
        {
            var item = new ItemFaltante { Numero = numero, Serie = serie };
            Log.Info("");
            Log.Info("== " + item.Nota);
            var validos = new List<NotaXml>();
            foreach (var a in arquivos.OrderBy(x => x.Caminho))
            {
                string descarte = null;
                string chave = a.Chave;
                if (a.Erro != null) descarte = "não foi possível ler: " + a.Erro;
                else if (chave == null || chave.Length != 44) descarte = "sem chave de acesso no Id do infNFe";
                else if (chave.Substring(20, 2) != "65") descarte = "modelo " + chave.Substring(20, 2) + " (não é NFC-e)";
                else if (a.Numero != numero.ToString() || a.Serie != serie.ToString()) descarte = "o conteúdo é da nota " + a.Serie + "/" + a.Numero;
                else if (Descartavel(a)) descarte = "arquivo " + a.Tipo.ToString().ToUpperInvariant() + " (registro do emissor, não é entregue)";
                var af = new ArquivoFaltante { Caminho = a.Caminho, Nome = a.Nome, Tipo = a.Tipo.ToString(), Motivo = descarte };
                item.Arquivos.Add(af);
                if (descarte != null) { Log.Info("  descartado " + a.Caminho + ": " + descarte); continue; }
                validos.Add(a);
                Log.Info("  candidato " + a.Caminho + " - chave " + chave + " dhEmi " + a.DhEmi + " vNF " + Analisador.Moeda(a.VNF) + " digest " + a.DigestCalculado);
            }

            if (validos.Count == 0)
            {
                SemNota(item, inuts);
                return item;
            }

            // O emitente vem do XML. Mais de um para o mesmo número não se resolve no chute: fica pendente.
            var emitentes = validos.GroupBy(v => v.Cnpj).ToList();
            if (emitentes.Count > 1)
            {
                foreach (var a in item.Arquivos.Where(x => x.Motivo == null)) a.Motivo = "emitente " + FormatarCnpj(validos.First(v => v.Caminho == a.Caminho).Cnpj);
                Pendente(item, "mais de um emitente para esse número: " + string.Join("; ", emitentes.Select(g =>
                    FormatarCnpj(g.Key) + (g.First().NomeEmitente != null ? " - " + g.First().NomeEmitente : "") + " (" + string.Join(", ", g.Select(x => x.Nome)) + ")")));
                return item;
            }
            item.Cnpj = emitentes[0].Key;
            item.Emitente = validos.Select(v => v.NomeEmitente).FirstOrDefault(x => x != null);

            // Uma consulta por chave.
            var porChave = validos.GroupBy(v => v.Chave).ToList();
            var respostas = new Dictionary<string, RespostaSefaz>();
            foreach (var g in porChave)
            {
                var r = consultar(g.Key);
                respostas[g.Key] = r;
                Log.Info("  SEFAZ " + g.Key + ": " + Situacao(r));
            }

            var conhecidas = respostas.Where(kv => kv.Value.Ok && kv.Value.Prot != null && (kv.Value.Prot.CStat == "100" || kv.Value.Prot.CStat == "150")).ToList();
            if (conhecidas.Count > 1)
            {
                Pendente(item, "mais de um candidato autorizado na SEFAZ: " + string.Join(", ", conhecidas.Select(c => c.Key)));
                return item;
            }
            if (conhecidas.Count == 1)
            {
                Autorizada(item, conhecidas[0].Key, conhecidas[0].Value, porChave.First(g => g.Key == conhecidas[0].Key).ToList(), eventos);
                return item;
            }

            // Nenhuma chave autorizada.
            var umaChave = porChave.Count == 1 ? porChave[0].OrderByDescending(x => x.ModificadoEm).First() : null;
            if (umaChave != null) Preencher(item, umaChave);
            var semResposta = respostas.Where(kv => !kv.Value.Ok).ToList();
            string lista = string.Join("; ", porChave.Select(g => g.Key + " (" + string.Join(", ", g.Select(x => x.Nome)) + ")"));
            if (semResposta.Count > 0)
            {
                var r = semResposta[0].Value;
                item.SituacaoSefaz = Situacao(r);
                Pendente(item, (r.NaoConsultada ? "SEFAZ não consultada: " : "SEFAZ sem resposta: ") + r.Erro +
                    (porChave.Count > 1 ? " · mais de um candidato: " : " · candidato: ") + lista);
                foreach (var a in item.Arquivos.Where(x => x.Motivo == null)) a.Motivo = "candidato (falta a confirmação da SEFAZ)";
                return item;
            }
            if (respostas.Values.All(r => r.CStat == "217"))
            {
                item.Status = StatusFaltante.NaoConsta;
                item.CStat = "217";
                item.SituacaoSefaz = Situacao(respostas.Values.First());
                item.Motivo = "existe XML local (" + lista + "), mas a SEFAZ não conhece a chave: provavelmente contingência nunca transmitida";
                foreach (var a in item.Arquivos.Where(x => x.Motivo == null)) a.Motivo = "SEFAZ: 217 - NF-e não consta na base de dados";
                return item;
            }
            var outra = respostas.First(kv => kv.Value.CStat != "217");
            item.CStat = outra.Value.CStat;
            item.SituacaoSefaz = Situacao(outra.Value);
            Pendente(item, "SEFAZ respondeu " + outra.Value.CStat + " - " + outra.Value.XMotivo + " para " + outra.Key);
            foreach (var a in item.Arquivos.Where(x => x.Motivo == null)) a.Motivo = "SEFAZ: " + outra.Value.CStat + " - " + outra.Value.XMotivo;
            return item;
        }

        public string DescricaoMeses
        {
            get { return Meses.Count == 0 ? "nenhuma nota encontrada" : string.Join(", ", Meses.Select(m => m.Key + " (" + m.Value + ")")); }
        }

        void SemNota(ItemFaltante item, List<InutilizacaoXml> inuts)
        {
            var cobre = inuts.Where(i => i.Serie == item.Serie && i.Inicial <= item.Numero && item.Numero <= i.Final && (i.Mod == null || i.Mod == "65")).ToList();
            var homologadas = cobre.Where(i => i.Homologada).OrderByDescending(i => i.Caminho).ToList();
            var homologada = homologadas.FirstOrDefault();
            int descartados = item.Arquivos.Count;
            string sobreDescartados = descartados > 0 ? " Há " + descartados + " arquivo(s) descartado(s) com esse número: " + string.Join(", ", item.Arquivos.Select(a => a.Nome + " (" + a.Motivo + ")")) + "." : "";
            if (homologadas.Select(i => i.Cnpj).Distinct().Count() > 1)
            {
                Pendente(item, "mais de um emitente com inutilização desse número: " + string.Join("; ", homologadas.Select(i => FormatarCnpj(i.Cnpj) + " (" + i.Nome + ")")));
                return;
            }
            if (homologada != null)
            {
                item.Cnpj = homologada.Cnpj;
                item.Status = StatusFaltante.Inutilizada;
                item.CStat = homologada.CStat;
                item.NProt = homologada.NProt;
                item.SituacaoSefaz = "inutilização homologada (cStat 102) no XML local; série " + homologada.Serie + " de " + homologada.Inicial + " a " + homologada.Final;
                item.Motivo = "número coberto pela inutilização " + homologada.Nome + sobreDescartados;
                item.Arquivos.Add(new ArquivoFaltante { Caminho = homologada.Caminho, Nome = homologada.Nome, Tipo = "Inutilização", Aceito = true, Motivo = "inutilização homologada, protocolo " + homologada.NProt });
                item.Entregas.Add(new Entrega { Origem = homologada.Caminho, Sha256 = homologada.Sha256, Tamanho = homologada.Tamanho, NomeDestino = homologada.Nome, Descricao = "XML da inutilização" });
                Log.Info("  INUTILIZADA por " + homologada.Caminho);
                return;
            }
            item.Status = StatusFaltante.NaoEncontrada;
            item.Motivo = "nenhum XML da nota nem de inutilização para esse número/série (pode ser número pulado que precisa ser inutilizado)." +
                (cobre.Count > 0 ? " Há pedido de inutilização sem retorno 102: " + string.Join(", ", cobre.Select(c => c.Nome + " (cStat " + (c.CStat ?? "nenhum") + ")")) + "." : "") +
                sobreDescartados;
            Log.Info("  " + item.Status + ": " + item.Motivo);
        }

        void Autorizada(ItemFaltante item, string chave, RespostaSefaz r, List<NotaXml> arqs, List<EventoXml> eventos)
        {
            string digVal = r.Prot.DigVal;
            item.Chave = chave;
            item.CStat = r.CStat;
            item.NProt = r.Prot.NProt;
            item.DigVal = digVal;
            item.SituacaoSefaz = Situacao(r);
            // O arquivo certo é o de conteúdo igual ao autorizado: DigestValue (recalculado) = digVal da SEFAZ.
            var bons = arqs.Where(a => a.DigestCalculado == digVal && a.DigestDeclarado == digVal).ToList();
            foreach (var a in arqs)
            {
                var af = item.Arquivos.First(x => x.Caminho == a.Caminho);
                if (bons.Contains(a)) af.Motivo = "digest " + a.DigestCalculado + " = digVal da SEFAZ";
                else if (a.DigestDeclarado == digVal) af.Motivo = "declara o digVal da SEFAZ, mas o conteúdo foi alterado (digest recalculado " + a.DigestCalculado + ")";
                else af.Motivo = "digest " + a.DigestCalculado + " ≠ digVal da SEFAZ " + digVal;
            }
            foreach (var a in item.Arquivos.Where(x => x.Motivo == null)) a.Motivo = "outra chave";
            if (bons.Count == 0)
            {
                Preencher(item, arqs.OrderByDescending(x => x.ModificadoEm).First());
                Pendente(item, "nenhum candidato com digest igual ao digVal da SEFAZ (" + digVal + ")");
                return;
            }
            // Preferência: nfeProc completo e limpo (com o protocolo dessa chave), depois ASSINADA, depois os demais.
            Func<NotaXml, bool> procCompleto = a => a.Prot != null && a.Prot.ChNFe == chave && a.Prot.DigVal == digVal && (a.Prot.CStat == "100" || a.Prot.CStat == "150") && !a.TemSobra;
            var escolhido = bons
                .OrderByDescending(procCompleto)
                .ThenByDescending(a => !a.TemSobra)
                .ThenByDescending(a => a.Tipo == TipoArquivo.Assinada)
                .ThenByDescending(a => a.ModificadoEm)
                .First();
            Preencher(item, escolhido);
            foreach (var a in item.Arquivos) a.Aceito = a.Caminho == escolhido.Caminho;
            item.Arquivos.First(x => x.Caminho == escolhido.Caminho).Motivo += " · escolhido";

            item.Entregas.Add(new Entrega { Origem = escolhido.Caminho, Sha256 = escolhido.Sha256, Tamanho = escolhido.Tamanho, NomeDestino = escolhido.Nome, Descricao = "XML da nota" });
            var motivo = new List<string> { "arquivo com o mesmo conteúdo autorizado (digest = digVal)" };
            if (!procCompleto(escolhido))
            {
                if (r.ProtNFeXml != null)
                {
                    item.Entregas.Add(GerarProcNFe(escolhido, chave, digVal, r.ProtNFeXml));
                    motivo.Add("procNFe montado com o protocolo da SEFAZ");
                }
                else motivo.Add("SEFAZ não devolveu o protNFe: vai só o XML original");
            }

            bool cancelada = r.CStat == "101" || r.CStat == "151" || r.CStat == "155" || r.Eventos.Any(e => (e.TpEvento == "110111" || e.TpEvento == "110112") && (e.CStat == "135" || e.CStat == "155"));
            if (!cancelada)
            {
                item.Status = StatusFaltante.Encontrada;
                item.Motivo = string.Join("; ", motivo);
                Log.Info("  " + item.Status + ": " + escolhido.Caminho);
                return;
            }

            var local = eventos.Where(e => e.ChNFe == chave && e.Cancelamento).OrderByDescending(e => e.Caminho).FirstOrDefault();
            var daSefaz = r.Eventos.FirstOrDefault(e => (e.TpEvento == "110111" || e.TpEvento == "110112") && (e.CStat == "135" || e.CStat == "155"));
            if (local != null)
            {
                item.Status = StatusFaltante.Cancelada;
                item.Arquivos.Add(new ArquivoFaltante { Caminho = local.Caminho, Nome = local.Nome, Tipo = "Evento", Aceito = true, Motivo = "evento de cancelamento " + local.TpEvento + ", protocolo " + local.NProt });
                item.Entregas.Add(new Entrega { Origem = local.Caminho, Sha256 = local.Sha256, Tamanho = local.Tamanho, NomeDestino = local.Nome, Descricao = "XML do evento de cancelamento" });
                motivo.Add("evento de cancelamento do disco");
            }
            else if (daSefaz != null)
            {
                item.Status = StatusFaltante.Cancelada;
                item.Entregas.Add(GerarEvento(chave, daSefaz));
                motivo.Add("evento de cancelamento obtido da SEFAZ (protocolo " + daSefaz.NProt + ")");
            }
            else
            {
                item.Status = StatusFaltante.CanceladaSemEvento;
                motivo.Add("XML do evento de cancelamento não encontrado no disco nem na resposta da SEFAZ");
            }
            item.Motivo = string.Join("; ", motivo);
            Log.Info("  " + item.Status + ": " + item.Motivo);
        }

        // nfeProc = a NFe do arquivo, sem mexer em um caractere, + o protNFe exatamente como a SEFAZ devolveu.
        Entrega GerarProcNFe(NotaXml nota, string chave, string digVal, string protNFe)
        {
            string texto = Encoding.UTF8.GetString(NotaXml.LerSomenteLeitura(nota.Caminho)).TrimStart('﻿');
            var m = Regex.Match(texto, @"<NFe[\s>]");
            int fim = m.Success ? texto.IndexOf("</NFe>", m.Index, StringComparison.Ordinal) : -1;
            if (fim < 0) throw new Exception("NFe não encontrada em " + nota.Caminho);
            string nfe = texto.Substring(m.Index, fim + 6 - m.Index);
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><nfeProc versao=\"4.00\" xmlns=\"" + NsNfe + "\">" + nfe + protNFe + "</nfeProc>";
            return new Entrega
            {
                Origem = nota.Caminho, Sha256 = nota.Sha256, Conteudo = xml, NomeDestino = chave + "-procNFe.xml", Descricao = "procNFe (nota + protocolo da SEFAZ)",
                Conferir = caminho =>
                {
                    var g = NotaXml.Ler(caminho);
                    if (g.Erro != null) return "procNFe gerado ilegível: " + g.Erro;
                    if (g.Chave != chave || g.DigestCalculado != digVal || g.DigestDeclarado != digVal) return "procNFe gerado não confere com a chave/digVal";
                    if (g.Prot == null || g.Prot.ChNFe != chave || g.Prot.DigVal != digVal) return "protocolo do procNFe gerado não confere";
                    return null;
                }
            };
        }

        static Entrega GerarEvento(string chave, EventoSefaz ev)
        {
            string x = ev.Xml;
            // Na resposta o namespace vem do retConsSitNFe; sozinho o arquivo precisa declará-lo.
            var abre = Regex.Match(x, @"^<procEventoNFe\b[^>]*>");
            if (abre.Success && !abre.Value.Contains("xmlns=")) x = "<procEventoNFe xmlns=\"" + NsNfe + "\"" + x.Substring("<procEventoNFe".Length);
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + x;
            return new Entrega
            {
                Conteudo = xml, NomeDestino = chave + "-procEventoNFe-" + ev.TpEvento + ".xml", Descricao = "evento de cancelamento (resposta da SEFAZ)",
                Conferir = caminho =>
                {
                    try
                    {
                        var xd = new XmlDocument { XmlResolver = null };
                        xd.Load(caminho);
                        var ns = new XmlNamespaceManager(xd.NameTable);
                        ns.AddNamespace("n", NsNfe);
                        return Txt(xd, "/n:procEventoNFe/n:evento/n:infEvento/n:chNFe", ns) == chave &&
                            Txt(xd, "/n:procEventoNFe/n:retEvento/n:infEvento/n:nProt", ns) == ev.NProt ? null : "evento gerado não confere";
                    }
                    catch (Exception e) { return "evento gerado ilegível: " + e.Message; }
                }
            };
        }

        public static string FormatarCnpj(string c)
        {
            return c != null && c.Length == 14 ? c.Substring(0, 2) + "." + c.Substring(2, 3) + "." + c.Substring(5, 3) + "/" + c.Substring(8, 4) + "-" + c.Substring(12) : c ?? "";
        }

        static void Preencher(ItemFaltante item, NotaXml n)
        {
            item.Chave = item.Chave ?? n.Chave;
            item.DhEmi = n.DhEmi;
            item.Valor = n.VNF;
        }

        void Pendente(ItemFaltante item, string motivo)
        {
            item.Status = StatusFaltante.Pendente;
            item.Motivo = motivo;
            Log.Info("  PENDENTE: " + motivo);
        }

        static string Situacao(RespostaSefaz r)
        {
            if (r.NaoConsultada) return "NÃO CONSULTADA: " + r.Erro;
            if (!r.Ok) return "SEM RESPOSTA: " + r.Erro;
            return r.CStat + " - " + r.XMotivo + (r.Prot != null ? " (protocolo " + r.Prot.NProt + ", cStat " + r.Prot.CStat + ")" : "");
        }

        public static DateTime? DataEmissao(string dhEmi)
        {
            DateTimeOffset d;
            if (dhEmi != null && DateTimeOffset.TryParse(dhEmi, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.DateTime;
            return null;
        }

        // ---------- Cópia ----------

        void Copiar(bool recopiar)
        {
            // Uma inutilização pode cobrir vários números: o arquivo vai uma vez só.
            var feitos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Itens.Where(i => i.Entregas.Count > 0))
            {
                var ant = Historico.BuscarCopiado(item.Cnpj, item.Serie, item.Numero);
                if (ant != null && !recopiar)
                {
                    item.Copia = CopiaFaltante.JaCopiado;
                    item.Motivo += " · já copiado em " + ant.DataHora + " para " + Path.GetDirectoryName(ant.CaminhoDestino);
                    item.Destinos.Add(ant.CaminhoDestino);
                    continue;
                }
                string erro = null;
                foreach (var e in item.Entregas)
                {
                    string destino = Path.Combine(PastaExecucao, e.NomeDestino);
                    string ja;
                    if (feitos.TryGetValue(destino, out ja))
                    {
                        if (ja == null)
                        {
                            item.Destinos.Add(destino);
                            Registrar(item, e, destino, e.Tamanho, e.Sha256, CopiaFaltante.Copiado);
                        }
                        else erro = ja;
                        continue;
                    }
                    erro = CopiarUm(item, e, destino);
                    feitos[destino] = erro;
                    if (erro == null) item.Destinos.Add(destino);
                    if (erro != null) break;
                }
                if (erro == null) { item.Copia = CopiaFaltante.Copiado; continue; }
                if (erro.StartsWith("já existe no destino"))
                {
                    item.Status = StatusFaltante.Pendente;
                    item.Copia = "";
                    item.Motivo = erro;
                }
                else
                {
                    item.Copia = CopiaFaltante.Erro;
                    item.Motivo = erro;
                }
                Log.Tela("  " + item.Nota + ": " + erro);
            }
        }

        string CopiarUm(ItemFaltante item, Entrega e, string destino)
        {
            Log.Info("Gravando " + (e.Conteudo != null ? e.Descricao : e.Origem) + " -> " + destino);
            if (File.Exists(destino)) return "já existe no destino: " + destino;
            byte[] dados;
            if (e.Conteudo != null)
            {
                if (e.Origem != null && NotaXml.HashSha256(NotaXml.LerSomenteLeitura(e.Origem)) != e.Sha256)
                    return "arquivo de origem mudou desde a análise; rode de novo";
                dados = new UTF8Encoding(false).GetBytes(e.Conteudo);
            }
            else
            {
                dados = NotaXml.LerSomenteLeitura(e.Origem);
                if (NotaXml.HashSha256(dados) != e.Sha256) return "arquivo de origem mudou desde a análise; rode de novo";
            }
            string hash = NotaXml.HashSha256(dados);
            try
            {
                using (var fs = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(dados, 0, dados.Length);
                    fs.Flush(true);
                }
            }
            catch (Exception ex)
            {
                Registrar(item, e, destino, dados.LongLength, hash, CopiaFaltante.Erro);
                return "falha ao gravar " + destino + ": " + ex.Message;
            }
            // Conferência: mesmo tamanho e mesmo hash; o XML gerado também é relido e conferido.
            string erro = null;
            if (new FileInfo(destino).Length != dados.LongLength) erro = "tamanho da cópia diferente do original";
            else if (NotaXml.HashSha256(NotaXml.LerSomenteLeitura(destino)) != hash) erro = "hash da cópia diferente do original";
            else if (e.Conferir != null) erro = e.Conferir(destino);
            Registrar(item, e, destino, dados.LongLength, hash, erro == null ? CopiaFaltante.Copiado : CopiaFaltante.Erro);
            Log.Info("  " + (erro ?? "conferido (" + dados.LongLength + " bytes, SHA-256 " + hash + ")"));
            return erro;
        }

        void Registrar(ItemFaltante item, Entrega e, string destino, long tamanho, string hash, string status)
        {
            Historico.Registrar(new HistoricoFaltantes.Registro
            {
                DataHora = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"), Cnpj = item.Cnpj, Serie = item.Serie.ToString(), Numero = item.Numero.ToString(),
                Chave = item.Chave, Arquivo = e.NomeDestino, CaminhoOrigem = e.Origem ?? "(resposta da SEFAZ)", CaminhoDestino = destino,
                Tamanho = tamanho.ToString(), Sha256 = hash, Status = status
            });
        }

        // ---------- Relatório e zip ----------

        static readonly string[] Cabecalho =
        {
            "Numero", "Serie", "CNPJ emitente", "Status", "Chave", "Emissao", "Mes emissao", "Valor", "Protocolo", "cStat SEFAZ", "Situacao SEFAZ",
            "Arquivo(s) de origem", "Arquivo(s) entregue(s)", "Copia", "Motivo"
        };

        public static void GravarCsv(string caminho, IEnumerable<ItemFaltante> itens)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(";", Cabecalho));
            foreach (var i in itens)
            {
                var origens = i.Entregas.Where(e => e.Origem != null).Select(e => e.Origem).Distinct().ToList();
                if (origens.Count == 0) origens = i.Arquivos.Select(a => a.Caminho).ToList();
                var d = DataEmissao(i.DhEmi);
                sb.AppendLine(string.Join(";", new[]
                {
                    i.Numero.ToString(), i.Serie.ToString(), FormatarCnpj(i.Cnpj), i.Status, Csv.Formula(i.Chave), d.HasValue ? d.Value.ToString("dd/MM/yyyy HH:mm:ss") : "", i.MesEmissao + (i.ForaDoMesPrincipal ? " (mês diferente das demais)" : ""),
                    i.Valor.HasValue ? i.Valor.Value.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR")) : "", Csv.Formula(i.NProt), i.CStat,
                    i.SituacaoSefaz, string.Join(" | ", origens), string.Join(" | ", i.Destinos.Select(Path.GetFileName)), i.Copia, i.Motivo
                }.Select(Csv.Campo)));
            }
            File.WriteAllText(caminho, sb.ToString(), new UTF8Encoding(true));
        }

        // Zip da pasta da entrega, ao lado dela (nunca sobrescreve).
        public static string GerarZip(string pasta)
        {
            if (pasta == null || !Directory.Exists(pasta)) throw new Exception("nenhuma pasta de entrega gerada ainda");
            string zip = pasta.TrimEnd('\\') + ".zip";
            for (int i = 2; File.Exists(zip); i++) zip = pasta.TrimEnd('\\') + "_" + i + ".zip";
            ZipFile.CreateFromDirectory(pasta, zip, CompressionLevel.Optimal, false);
            return zip;
        }

        public void Dispose() { Log.Dispose(); }
    }
}
