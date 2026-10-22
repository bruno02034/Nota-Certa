using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DuplicidadeNfce
{
    // Opções comuns à linha de comando e à interface.
    class Opcoes
    {
        public string ArqConfig, Captura;
        public bool SemSefaz, Copiar, TodasDatas, Gui;
        public List<KeyValuePair<string, string>> Sobrepor = new List<KeyValuePair<string, string>>();
        public List<string> TextoNotas = new List<string>();
        public string ArqNotas;

        public Config CarregarConfig()
        {
            var cfg = Config.Carregar(ArqConfig);
            foreach (var kv in Sobrepor) cfg.Definir(kv.Key, kv.Value, "linha de comando");
            cfg.TodasDatas = TodasDatas;
            return cfg;
        }
    }

    static class Program
    {
        public const string NomeSistema = "NotaCerta";

        const string Ajuda =
@"NotaCerta - conferência de NFC-e rejeitadas por duplicidade (539)

Sem argumentos abre a interface gráfica. Linha de comando:

     DuplicidadeNfce.exe --notas 6/88867,6/88868 [opções]
     DuplicidadeNfce.exe --notas-arquivo notas.txt [opções]

  --notas <lista>    notas a processar, série/número (obrigatório, ou --notas-arquivo)
  --notas-arquivo <arq>  arquivo com uma nota série/número por linha (# = comentário)
  --copiar           copia as linhas ""A COPIAR"" para Duplicidade\AAAA-MM-DD e confere a cópia
  --config <arq>     arquivo de configuração (padrão: duplicidade.ini ao lado do exe)
  --pasta <dir>      pasta raiz dos XMLs (sobrepõe PastaRaiz)
  --de <data>        data inicial  (dd/mm/aaaa, hoje, ontem, -N)
  --ate <data>       data final
  --todas-datas      varre a pasta raiz inteira, sem filtro de data
  --destino <dir>    pasta base das cópias (sobrepõe PastaDestino)
  --pfx <arq|dir>    certificado .pfx ou pasta dele (sobrepõe CertificadoPfx)
  --senha-certificado <s>  senha do .pfx (normalmente vem do C:\Chef\NFCe.ini)
  --sem-sefaz        TESTE: usa o protNFe dos XMLs locais no lugar da SEFAZ (não combina com --copiar)
  --gui              abre a interface gráfica com as opções acima

Sem --copiar: só relatório. Os arquivos do emissor são abertos somente para leitura;
a cópia nunca move nem sobrescreve, e fica registrada em historico.csv na pasta base.";

        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] lista, uint qtd);

        [STAThread]
        static int Main(string[] args)
        {
            Opcoes op;
            try
            {
                op = LerArgumentos(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERRO: " + ex.Message);
                return 2;
            }
            if (op == null) { Console.WriteLine(Ajuda); return 0; }

            if (args.Length == 0 || op.Gui)
            {
                // Aberto com duplo clique: o console foi criado só para este processo; some com ele.
                if (GetConsoleProcessList(new uint[2], 2) <= 1) FreeConsole();
                return Gui.Executar(op);
            }

            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try
            {
                return ExecutarLinhaDeComando(op);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERRO: " + ex.Message);
                return 2;
            }
        }

        static Opcoes LerArgumentos(string[] args)
        {
            var op = new Opcoes { ArqConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "duplicidade.ini") };
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                Func<string> prox = () =>
                {
                    if (i + 1 >= args.Length) throw new Exception("faltou o valor de " + args[i]);
                    return args[++i];
                };
                Action<string> sobrepor = chave => op.Sobrepor.Add(new KeyValuePair<string, string>(chave, prox()));
                switch (a)
                {
                    case "--config": op.ArqConfig = prox(); break;
                    case "--pasta": sobrepor("PastaRaiz"); break;
                    case "--de": sobrepor("DataInicial"); break;
                    case "--ate": sobrepor("DataFinal"); break;
                    case "--destino": sobrepor("PastaDestino"); break;
                    case "--pfx": sobrepor("CertificadoPfx"); break;
                    case "--senha-certificado": sobrepor("CertificadoSenha"); break;
                    case "--todas-datas": op.TodasDatas = true; break;
                    case "--sem-sefaz": op.SemSefaz = true; break;
                    case "--copiar": op.Copiar = true; break;
                    case "--notas": op.TextoNotas.Add(prox()); break;
                    case "--notas-arquivo": op.ArqNotas = prox(); break;
                    case "--gui": op.Gui = true; break;
                    case "--captura": op.Captura = prox(); op.Gui = true; break;
                    case "--ajuda": case "-h": case "/?": return null;
                    default: throw new Exception("opção desconhecida: " + args[i] + " (use --ajuda)");
                }
            }
            return op;
        }

        static int ExecutarLinhaDeComando(Opcoes op)
        {
            if (op.ArqNotas != null)
            {
                if (!File.Exists(op.ArqNotas)) throw new Exception("arquivo de notas não existe: " + op.ArqNotas);
                op.TextoNotas.AddRange(File.ReadAllLines(op.ArqNotas, Encoding.UTF8).Select(x => x.Split('#')[0]));
            }
            var notasAlvo = LerNotas(op.TextoNotas);
            if (notasAlvo.Count == 0)
            {
                Console.WriteLine("Informe as notas a processar (série/número) com --notas ou --notas-arquivo.");
                Console.WriteLine();
                Console.WriteLine(Ajuda);
                return 1;
            }
            if (op.Copiar && op.SemSefaz)
                throw new Exception("--copiar não pode ser usado com --sem-sefaz (a cópia exige a confirmação da SEFAZ)");

            using (var motor = new Motor(op.CarregarConfig(), AppDomain.CurrentDomain.BaseDirectory, op.SemSefaz))
            {
                var log = motor.Log;
                log.Tela(NomeSistema + " - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") +
                    (op.Copiar ? " - modo CÓPIA (--copiar)" : " - modo RELATÓRIO (nada é copiado)"));
                log.Tela("Notas: " + string.Join(", ", notasAlvo));
                if (op.SemSefaz) log.Tela("*** TESTE --sem-sefaz: SEFAZ NÃO consultada; usando protNFe dos XMLs locais ***");
                log.Tela("Pasta raiz: " + motor.Raiz);
                log.Tela("Período: " + motor.DescricaoPeriodo);

                motor.Varrer();
                var linhas = motor.Analisador.AnalisarNotas(motor.PastaCopia, notasAlvo, motor.Historico, null);
                if (op.Copiar)
                {
                    log.Info("");
                    Copiador.Executar(linhas, motor.Historico, log);
                }
                string arqCsv = motor.GravarRelatorio(linhas);

                log.Tela("");
                foreach (var l in linhas)
                    log.Tela(string.Format("{0}/{1} pedido {2,-7} sistema {3,8}  SEFAZ {4,8}  {5,-10} {6}",
                        l.Numero, l.Serie, l.Pedido ?? "-", Analisador.Moeda(l.ValorSistema),
                        l.ValorSefaz.HasValue ? Analisador.Moeda(l.ValorSefaz) : "-", l.Status,
                        l.ArquivoEscolhido ?? l.Motivo));
                log.Tela("");
                log.Tela(linhas.Count + " linha(s): " +
                    string.Join(", ", linhas.GroupBy(l => l.Status).Select(g => g.Count() + " " + g.Key)));
                if (op.Copiar) log.Tela("Cópias:    " + motor.PastaCopia);
                if (op.Copiar) log.Tela("Histórico: " + motor.Historico.Caminho);
                log.Tela("Relatório: " + arqCsv);
                log.Tela("Log:       " + motor.ArqLog);
            }
            return 0;
        }

        // "6/88867", separados por vírgula, ponto e vírgula, espaço ou linha. Devolve "série/número" normalizado.
        static List<string> LerNotas(IEnumerable<string> textos)
        {
            var lista = new List<string>();
            foreach (string texto in textos)
                foreach (string item in texto.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] p = item.Split('/');
                    int serie;
                    long numero;
                    if (p.Length != 2 || !int.TryParse(p[0], out serie) || !long.TryParse(p[1], out numero) || serie < 0 || numero <= 0)
                        throw new Exception("nota inválida '" + item + "' (use série/número, ex.: 6/88867)");
                    string n = serie + "/" + numero;
                    if (!lista.Contains(n)) lista.Add(n);
                }
            return lista;
        }
    }
}
