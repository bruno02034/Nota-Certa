using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DuplicidadeNfce
{
    enum TipoArquivo { Autorizada, Assinada, Rejeicao, FalhaSchema, Temp, ContingenciaEnvia, Outro }

    class ProtocoloXml
    {
        public string CStat, XMotivo, ChNFe, NProt, DigVal, DhRecbto;

        // infProt do protNFe (prefixo n = namespace da NF-e).
        public static ProtocoloXml De(XmlNode ip, XmlNamespaceManager ns)
        {
            return new ProtocoloXml
            {
                ChNFe = NotaXml.Texto(ip, "n:chNFe", ns),
                CStat = NotaXml.Texto(ip, "n:cStat", ns),
                XMotivo = NotaXml.Texto(ip, "n:xMotivo", ns),
                NProt = NotaXml.Texto(ip, "n:nProt", ns),
                DigVal = NotaXml.Texto(ip, "n:digVal", ns),
                DhRecbto = NotaXml.Texto(ip, "n:dhRecbto", ns)
            };
        }

        // cStat 100/150 = autorizada (no protocolo da SEFAZ vale também o 120 "com alerta": RespostaSefaz.ProtAutorizado).
        public static bool Autorizado(string cStat) { return cStat == "100" || cStat == "150"; }
    }

    // Um arquivo NOTA_* lido do disco (somente leitura).
    class NotaXml
    {
        const string NsNfe = "http://www.portalfiscal.inf.br/nfe";
        const string NsDsig = "http://www.w3.org/2000/09/xmldsig#";

        public string Caminho, Nome;
        public byte[] Conteudo;     // só o XML que veio do banco (não há arquivo de onde copiar)
        public DateTime? DataPasta;
        public bool NaFaixaPrincipal = true;
        public TipoArquivo Tipo;
        public long Tamanho;
        public string Sha256;
        public DateTime ModificadoEm;

        public string Chave, Numero, Serie, DhEmi, TpEmis, Pedido, NomeEmitente;
        public decimal? VNF;
        public string DigestDeclarado, DigestCalculado;
        public ProtocoloXml Prot;   // null quando <protNFe/> vem vazio
        public string ItemRejeitado; // rejeição que cita [nItem: N]: produto e causa, para corrigir no Metre

        // O Metre regrava os arquivos sem truncar: depois do primeiro </nfeProc> pode sobrar
        // o final de uma gravação anterior. Só o primeiro documento é considerado.
        public bool TemSobra;
        public List<ProtocoloXml> ProtsNaSobra = new List<ProtocoloXml>();
        public string Erro;

        public string Cnpj { get { return Chave != null && Chave.Length == 44 ? Chave.Substring(6, 14) : null; } }

        public static TipoArquivo Classificar(string nome)
        {
            string n = nome.ToUpperInvariant();
            if (n.Contains("REJEICAO")) return TipoArquivo.Rejeicao;
            if (n.Contains("FALHA_SCHEMA")) return TipoArquivo.FalhaSchema;
            if (n.Contains("ONTIGENCIA_ENVIA")) return TipoArquivo.ContingenciaEnvia;
            if (n.Contains("_TEMP")) return TipoArquivo.Temp;
            if (n.Contains("_ASSINADA")) return TipoArquivo.Assinada;
            if (n.Contains("_NFCE")) return TipoArquivo.Autorizada;
            return TipoArquivo.Outro;
        }

        public static byte[] LerSomenteLeitura(string caminho)
        {
            using (var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static string HashSha256(byte[] dados)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(dados)).Replace("-", "");
        }

        public static NotaXml Ler(string caminho)
        {
            var nota = new NotaXml();
            nota.Caminho = caminho;
            nota.Nome = Path.GetFileName(caminho);
            nota.Tipo = Classificar(nota.Nome);
            try
            {
                nota.ModificadoEm = File.GetLastWriteTime(caminho);
                byte[] bytes = LerSomenteLeitura(caminho);
                nota.Tamanho = bytes.LongLength;
                nota.Sha256 = HashSha256(bytes);
                string texto = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
                string doc = CortarPrimeiroDocumento(nota, texto);
                Interpretar(nota, doc);
            }
            catch (Exception ex)
            {
                nota.Erro = ex.Message;
            }
            return nota;
        }

        // XML que não está em arquivo (ex.: guardado pelo Metre no banco chefxml). 'origem' faz o papel do caminho.
        public static NotaXml LerConteudo(string origem, byte[] bytes)
        {
            var nota = new NotaXml { Caminho = origem, Nome = origem, Tipo = TipoArquivo.Outro, Conteudo = bytes };
            try
            {
                nota.Tamanho = bytes.LongLength;
                nota.Sha256 = HashSha256(bytes);
                string texto = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
                if (!texto.TrimStart().StartsWith("<")) throw new Exception("conteúdo não é XML em texto (" + bytes.Length + " bytes)");
                Interpretar(nota, CortarPrimeiroDocumento(nota, texto));
            }
            catch (Exception ex)
            {
                nota.Erro = ex.Message;
            }
            return nota;
        }

        static string CortarPrimeiroDocumento(NotaXml nota, string texto)
        {
            // nfeProc (normal), enviNFe (Temp_ANTES_ENVIO: lote de envio) ou NFe solta.
            string fecha = null;
            int fim = -1;
            foreach (string f in new[] { "</nfeProc>", "</enviNFe>", "</NFe>" })
            {
                fim = texto.IndexOf(f, StringComparison.Ordinal);
                if (fim >= 0) { fecha = f; break; }
            }
            if (fim < 0) return texto;
            fim += fecha.Length;
            string sobra = texto.Substring(fim);
            nota.TemSobra = sobra.Trim().Length > 0;
            if (nota.TemSobra)
                nota.ProtsNaSobra = ExtrairProtocolosTexto(sobra);
            return texto.Substring(0, fim);
        }

        static readonly Regex ReInfProt = new Regex(@"<infProt[^>]*>(.*?)</infProt>", RegexOptions.Singleline);

        static List<ProtocoloXml> ExtrairProtocolosTexto(string texto)
        {
            var lista = new List<ProtocoloXml>();
            foreach (Match m in ReInfProt.Matches(texto))
            {
                string c = m.Groups[1].Value;
                lista.Add(new ProtocoloXml
                {
                    ChNFe = Tag(c, "chNFe"),
                    CStat = Tag(c, "cStat"),
                    XMotivo = Tag(c, "xMotivo"),
                    NProt = Tag(c, "nProt"),
                    DigVal = Tag(c, "digVal"),
                    DhRecbto = Tag(c, "dhRecbto")
                });
            }
            return lista;
        }

        static string Tag(string conteudo, string nome)
        {
            var m = Regex.Match(conteudo, "<" + nome + ">(.*?)</" + nome + ">", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        // XML do disco, do banco ou da rede: sem DTD nem entidades. preservarEspacos para recalcular o digest.
        public static XmlDocument CarregarXml(string texto, bool preservarEspacos)
        {
            var xd = new XmlDocument { PreserveWhitespace = preservarEspacos, XmlResolver = null };
            using (var sr = new StringReader(texto))
            using (var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                xd.Load(xr);
            return xd;
        }

        public static XmlNamespaceManager Namespaces(XmlDocument xd)
        {
            var ns = new XmlNamespaceManager(xd.NameTable);
            ns.AddNamespace("n", NsNfe);
            ns.AddNamespace("ds", NsDsig);
            return ns;
        }

        static void Interpretar(NotaXml nota, string doc)
        {
            var xd = CarregarXml(doc, true);
            var ns = Namespaces(xd);

            var inf = xd.SelectSingleNode("//n:NFe/n:infNFe", ns) as XmlElement;
            if (inf == null) { nota.Erro = "infNFe não encontrado"; return; }

            string id = inf.GetAttribute("Id");
            if (id.StartsWith("NFe")) nota.Chave = id.Substring(3);
            nota.Numero = Texto(inf, "n:ide/n:nNF", ns);
            nota.Serie = Texto(inf, "n:ide/n:serie", ns);
            nota.DhEmi = Texto(inf, "n:ide/n:dhEmi", ns);
            nota.TpEmis = Texto(inf, "n:ide/n:tpEmis", ns);
            nota.Pedido = Texto(inf, "n:det/n:prod/n:xPed", ns);
            nota.NomeEmitente = Texto(inf, "n:emit/n:xNome", ns);
            nota.VNF = Decimal(Texto(inf, "n:total/n:ICMSTot/n:vNF", ns));
            nota.DigestDeclarado = Texto(xd, "//n:NFe/ds:Signature/ds:SignedInfo/ds:Reference/ds:DigestValue", ns);
            nota.DigestCalculado = CalcularDigest(inf);

            var ip = xd.SelectSingleNode("//n:protNFe/n:infProt", ns);
            if (ip != null)
            {
                nota.Prot = ProtocoloXml.De(ip, ns);
                nota.ItemRejeitado = DescreverItem(inf, ns, nota.Prot.CStat, nota.Prot.XMotivo);
            }
        }

        // "Rejeição: ... [nItem: 7]" → "item 7: ORIGINAL 600ML (código 376, NCM ..., CFOP ...): 3 UN x 13,90 = 41,70, desconto 63,43"
        // mais a causa quando o código é conhecido.
        internal static string DescreverItem(XmlElement inf, XmlNamespaceManager ns, string cStat, string xMotivo)
        {
            var m = Regex.Match(xMotivo ?? "", @"nItem\s*:?\s*(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string n = m.Groups[1].Value;
            var det = inf.SelectSingleNode("n:det[@nItem='" + n + "']", ns);
            if (det == null) return "item " + n + " não encontrado no XML";
            Func<string, string> p = x => Texto(det, "n:prod/n:" + x, ns);
            decimal vProd = Decimal(p("vProd")) ?? 0, vDesc = Decimal(p("vDesc")) ?? 0;
            string s = "item " + n + ": " + p("xProd") + " (código " + p("cProd") + ", NCM " + p("NCM") + ", CFOP " + p("CFOP") + "): " +
                Num(p("qCom")) + " " + p("uCom") + " x " + Analisador.Moeda(Decimal(p("vUnCom"))) + " = " + Analisador.Moeda(vProd) +
                (vDesc > 0 ? ", desconto " + Analisador.Moeda(vDesc) : "");
            if (cStat == "483" && vDesc > vProd)
            {
                decimal totProd = Decimal(Texto(inf, "n:total/n:ICMSTot/n:vProd", ns)) ?? 0, totDesc = Decimal(Texto(inf, "n:total/n:ICMSTot/n:vDesc", ns)) ?? 0;
                s += ". Causa: o desconto do item é maior que o valor dele; o Metre rateou o desconto da venda (" + Analisador.Moeda(totDesc) +
                    " de " + Analisador.Moeda(totProd) + ") deixando o resto nesse item";
                if (totProd > 0)
                    s += ". Em proporção (" + (totDesc / totProd * 100).ToString("0.00", Br) + "% em cada item) o total da nota não muda; corrija os descontos no Metre e transmita de novo";
            }
            return s;
        }

        static readonly CultureInfo Br = new CultureInfo("pt-BR");
        static string Num(string s) { decimal? d = Decimal(s); return d == null ? s : d.Value.ToString("0.####", Br); }

        // SHA-1 do infNFe canonicalizado (C14N), como na assinatura da NF-e.
        // Confirma que o DigestValue declarado corresponde ao conteúdo do arquivo.
        static string CalcularDigest(XmlElement inf)
        {
            try
            {
                var d2 = new XmlDocument();
                d2.PreserveWhitespace = true;
                d2.LoadXml(inf.OuterXml);
                var t = new XmlDsigC14NTransform();
                t.LoadInput(d2);
                using (var s = (Stream)t.GetOutput(typeof(Stream)))
                using (var sha = SHA1.Create())
                    return Convert.ToBase64String(sha.ComputeHash(s));
            }
            catch
            {
                return null;
            }
        }

        // Texto do nó (sem espaços nas pontas); null se a raiz ou o nó não existirem.
        public static string Texto(XmlNode raiz, string xpath, XmlNamespaceManager ns)
        {
            var n = raiz == null ? null : raiz.SelectSingleNode(xpath, ns);
            return n == null ? null : n.InnerText.Trim();
        }

        // Valor com ponto decimal (XML, banco, histórico).
        public static decimal? Decimal(string s)
        {
            decimal d;
            if (s != null && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        // Data e hora no fuso gravado (ex.: 2026-10-19T17:05:40-03:00 → 19/10/2026 17:05:40).
        public static DateTime? DataHora(string iso)
        {
            DateTimeOffset d;
            if (iso != null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.DateTime;
            return null;
        }
    }
}
