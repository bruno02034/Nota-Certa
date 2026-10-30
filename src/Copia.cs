using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuplicidadeNfce
{
    class RegistroHistorico
    {
        public string DataHora, Serie, Numero, ChaveAutorizada, Arquivo, CaminhoOrigem, CaminhoDestino, Tamanho, Sha256, Status;
    }

    // historico.csv na pasta de destino: uma linha por cópia feita (ou tentada).
    class Historico
    {
        public const string NomeArquivo = "historico.csv";
        static readonly string[] Cabecalho =
            { "Data/hora", "Serie", "Numero", "Chave autorizada", "Arquivo", "Caminho origem", "Caminho destino", "Tamanho", "SHA256", "Status" };

        readonly string caminho;
        readonly Dictionary<string, RegistroHistorico> copiados = new Dictionary<string, RegistroHistorico>();

        public Historico(string pastaDestino)
        {
            caminho = Path.Combine(pastaDestino, NomeArquivo);
            if (!File.Exists(caminho)) return;
            bool primeira = true;
            foreach (string linha in File.ReadAllLines(caminho, Encoding.UTF8))
            {
                if (primeira) { primeira = false; continue; }
                var c = Csv.Dividir(linha);
                if (c.Count < Cabecalho.Length) continue;
                var r = new RegistroHistorico
                {
                    DataHora = c[0], Serie = c[1], Numero = c[2], ChaveAutorizada = Csv.SemFormula(c[3]), Arquivo = c[4],
                    CaminhoOrigem = c[5], CaminhoDestino = c[6], Tamanho = c[7], Sha256 = c[8], Status = c[9]
                };
                Todos.Add(r);
                if (r.Status == Status.Copiado) copiados[r.ChaveAutorizada] = r;
            }
        }

        public readonly List<RegistroHistorico> Todos = new List<RegistroHistorico>();

        public string Caminho { get { return caminho; } }

        public RegistroHistorico BuscarCopiado(string chaveAutorizada)
        {
            RegistroHistorico r;
            return chaveAutorizada != null && copiados.TryGetValue(chaveAutorizada, out r) ? r : null;
        }

        // Último ajuste de chave no banco para esta chave autorizada, se ainda valer (não desfeito).
        // Nesses registros: Arquivo = "<tabela> id_nota_saida=N", CaminhoOrigem = chave antiga, CaminhoDestino = backup.
        public RegistroHistorico BuscarAjuste(string chaveAutorizada)
        {
            var r = Ultimo(chaveAutorizada, Status.ChaveAjustada, Status.AjusteDesfeito);
            return r != null && r.Status == Status.ChaveAjustada ? r : null;
        }

        // Última conferência manual do valor no portal da SEFAZ (Tamanho = valor do sistema conferido).
        public RegistroHistorico BuscarConferencia(string chaveAutorizada)
        {
            return Ultimo(chaveAutorizada, Status.ValorConfereManual, Status.ValorDiferenteManual);
        }

        RegistroHistorico Ultimo(string chave, params string[] status)
        {
            return chave == null ? null : Todos.LastOrDefault(r => r.ChaveAutorizada == chave && status.Contains(r.Status));
        }

        public void Registrar(RegistroHistorico r)
        {
            bool novo = !File.Exists(caminho);
            var sb = new StringBuilder();
            if (novo) sb.AppendLine(string.Join(";", Cabecalho));
            sb.AppendLine(string.Join(";", new[]
            {
                r.DataHora, r.Serie, r.Numero, Csv.Formula(r.ChaveAutorizada), r.Arquivo, r.CaminhoOrigem, r.CaminhoDestino, r.Tamanho, r.Sha256, r.Status
            }.Select(Csv.Campo)));
            // Ajuste de chave e conferência manual podem ser os primeiros registros (pasta ainda sem cópias).
            if (novo) Directory.CreateDirectory(Path.GetDirectoryName(caminho));
            if (novo) File.WriteAllText(caminho, sb.ToString(), new UTF8Encoding(true));
            else File.AppendAllText(caminho, sb.ToString(), new UTF8Encoding(false));
            Todos.Add(r);
            if (r.Status == Status.Copiado) copiados[r.ChaveAutorizada] = r;
        }
    }

    // Fase 2: copia (nunca move) os arquivos das linhas "A COPIAR" e confere a cópia.
    static class Copiador
    {
        public static void Executar(List<LinhaRelatorio> linhas, Historico historico, Log log)
        {
            var feitos = new Dictionary<string, LinhaRelatorio>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in linhas.Where(x => x.Status == Status.ACopiar))
            {
                LinhaRelatorio anterior;
                if (feitos.TryGetValue(l.CaminhoDestino, out anterior))
                {
                    // Outra tentativa do mesmo número: o arquivo autorizado é o mesmo.
                    l.Status = anterior.Status;
                    l.Motivo = anterior.Status == Status.Copiado
                        ? "mesmo arquivo autorizado copiado nesta execução para o pedido " + (anterior.Pedido ?? anterior.ChaveSistema)
                        : anterior.Motivo;
                    continue;
                }
                Copiar(l, historico, log);
                feitos[l.CaminhoDestino] = l;
            }
        }

        static void Copiar(LinhaRelatorio l, Historico historico, Log log)
        {
            var fonte = l.Fonte;
            string destino = l.CaminhoDestino;
            log.Info("Copiando " + fonte.Caminho + " -> " + destino);
            if (File.Exists(destino))
            {
                Fim(l, Status.Pendente, "já existe no destino", log);
                return;
            }
            byte[] dados = fonte.Conteudo ?? NotaXml.LerSomenteLeitura(fonte.Caminho);
            if (NotaXml.HashSha256(dados) != fonte.Sha256)
            {
                Fim(l, Status.Pendente, "arquivo de origem mudou desde a análise; rode de novo", log);
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destino));
                using (var fs = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(dados, 0, dados.Length);
                    fs.Flush(true);
                }
            }
            catch (Exception ex)
            {
                Fim(l, Status.ErroCopia, "falha ao gravar: " + ex.Message, log);
                RegistrarHistorico(l, historico, null);
                return;
            }

            // Conferência: existe, mesmo tamanho e mesmo hash do original.
            string erro = null, hashCopia = null;
            if (!File.Exists(destino)) erro = "cópia não encontrada após gravar";
            else if (new FileInfo(destino).Length != fonte.Tamanho) erro = "tamanho da cópia " + new FileInfo(destino).Length + " ≠ original " + fonte.Tamanho;
            else if ((hashCopia = NotaXml.HashSha256(NotaXml.LerSomenteLeitura(destino))) != fonte.Sha256) erro = "hash da cópia diferente do original";

            if (erro == null) Fim(l, Status.Copiado, "cópia conferida (" + fonte.Tamanho + " bytes, SHA-256 " + fonte.Sha256 + ")", log);
            else Fim(l, Status.ErroCopia, erro, log);
            RegistrarHistorico(l, historico, hashCopia);
        }

        static void Fim(LinhaRelatorio l, string status, string motivo, Log log)
        {
            l.Status = status;
            l.Motivo = motivo;
            log.Info("  " + status + ": " + motivo);
        }

        static void RegistrarHistorico(LinhaRelatorio l, Historico historico, string hashCopia)
        {
            historico.Registrar(new RegistroHistorico
            {
                DataHora = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                Serie = l.Serie, Numero = l.Numero, ChaveAutorizada = l.ChaveAutorizada,
                Arquivo = l.ArquivoEscolhido, CaminhoOrigem = l.CaminhoOrigem, CaminhoDestino = l.CaminhoDestino,
                Tamanho = l.Fonte.Tamanho.ToString(), Sha256 = hashCopia ?? l.Fonte.Sha256, Status = l.Status
            });
        }
    }
}
