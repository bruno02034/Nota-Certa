using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DuplicidadeNfce
{
    static class Status
    {
        public const string AProcessar = "A PROCESSAR";
        public const string ACopiar = "A COPIAR";
        public const string Copiado = "COPIADO";
        public const string ErroCopia = "ERRO_COPIA";
        public const string JaProcessada = "JÁ PROCESSADA";
        public const string Pendente = "PENDENTE";
        // Sem XML da chave autorizada para copiar: troca a chave_acesso no banco do Metre (AjusteChave).
        public const string AjusteChave = "AJUSTE DE CHAVE";
        public const string ChaveAjustada = "CHAVE AJUSTADA";
        public const string AjusteDesfeito = "AJUSTE DESFEITO";
        public const string ErroAjuste = "ERRO_AJUSTE";
        public const string ValorConfereManual = "VALOR CONFERE (MANUAL)";
        public const string ValorDiferenteManual = "VALOR DIFERENTE (MANUAL)";
        public const string EmContingencia = "EM CONTINGÊNCIA";
        public const string OutroErro = "OUTRO ERRO";
        public const string JaAutorizada = "JÁ AUTORIZADA";
    }

    class Log : IDisposable
    {
        readonly StreamWriter arq;

        public Log(string caminho)
        {
            arq = new StreamWriter(caminho, false, new UTF8Encoding(true));
            arq.AutoFlush = true;
        }

        // Mensagens de andamento para quem estiver olhando (console ou barra de status da interface).
        public Action<string> AoMostrar = Console.WriteLine;

        public void Info(string msg) { lock (arq) arq.WriteLine(msg); }

        public void Tela(string msg)
        {
            if (AoMostrar != null) AoMostrar(msg);
            Info(msg);
        }

        public void Dispose() { arq.Dispose(); }
    }

    static class Csv
    {
        public static string Campo(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // Chaves e protocolos como fórmula de texto, senão o Excel converte para notação científica.
        public static string Formula(string s) { return string.IsNullOrEmpty(s) ? "" : "=\"" + s + "\""; }

        public static string SemFormula(string s)
        {
            return s != null && s.StartsWith("=\"") && s.EndsWith("\"") ? s.Substring(2, s.Length - 3) : s;
        }

        public static List<string> Dividir(string linha)
        {
            var campos = new List<string>();
            var sb = new StringBuilder();
            bool aspas = false;
            for (int i = 0; i < linha.Length; i++)
            {
                char c = linha[i];
                if (aspas)
                {
                    if (c == '"' && i + 1 < linha.Length && linha[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') aspas = false;
                    else sb.Append(c);
                }
                else if (c == '"') aspas = true;
                else if (c == ';') { campos.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            campos.Add(sb.ToString());
            return campos;
        }
    }

    static class Relatorio
    {
        static readonly string[] Cabecalho =
        {
            "Numero", "Serie", "Pedido", "Emissao sistema", "Valor sistema", "Chave do sistema", "Origem",
            "Chave autorizada", "nRec", "Situacao SEFAZ", "nProt SEFAZ", "digVal SEFAZ", "Valor SEFAZ",
            "Arquivo escolhido", "Caminho origem", "Caminho destino", "Status", "Classificacao", "Motivo"
        };

        // CSV com ';' e UTF-8 com BOM para abrir direto no Excel em português.
        public static void GravarCsv(string caminho, List<LinhaRelatorio> linhas)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(";", Cabecalho));
            foreach (var l in linhas)
            {
                sb.AppendLine(string.Join(";", new[]
                {
                    l.Numero, l.Serie, l.Pedido, DataHora(l.DhEmiSistema), Valor(l.ValorSistema), Csv.Formula(l.ChaveSistema), l.Origem,
                    Csv.Formula(l.ChaveAutorizada), Csv.Formula(l.NRec), l.SituacaoSefaz, Csv.Formula(l.NProtSefaz), l.DigValSefaz, Valor(l.ValorSefaz),
                    l.ArquivoEscolhido, l.CaminhoOrigem, l.CaminhoDestino, l.Status, l.Classificacao, l.Motivo
                }.Select(Csv.Campo)));
            }
            File.WriteAllText(caminho, sb.ToString(), new UTF8Encoding(true));
        }

        static string Valor(decimal? v) { return v.HasValue ? v.Value.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR")) : ""; }

        static string DataHora(string iso)
        {
            DateTimeOffset d;
            if (iso != null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d.ToString("dd/MM/yyyy HH:mm:ss");
            return iso ?? "";
        }
    }
}
