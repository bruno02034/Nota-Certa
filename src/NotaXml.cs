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
    }

    // Um arquivo NOTA_* lido do disco (somente leitura).
    class NotaXml
    {
        const string NsNfe = "http://www.portalfiscal.inf.br/nfe";
        const string NsDsig = "http://www.w3.org/2000/09/xmldsig#";

        public string Caminho, Nome;
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

        static void Interpretar(NotaXml nota, string doc)
        {
            var xd = new XmlDocument();
            xd.PreserveWhitespace = true;
            xd.XmlResolver = null;
            var conf = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var sr = new StringReader(doc))
            using (var xr = XmlReader.Create(sr, conf))
                xd.Load(xr);

            var ns = new XmlNamespaceManager(xd.NameTable);
            ns.AddNamespace("n", NsNfe);
            ns.AddNamespace("ds", NsDsig);

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
                nota.Prot = new ProtocoloXml
                {
                    ChNFe = Texto(ip, "n:chNFe", ns),
                    CStat = Texto(ip, "n:cStat", ns),
                    XMotivo = Texto(ip, "n:xMotivo", ns),
                    NProt = Texto(ip, "n:nProt", ns),
                    DigVal = Texto(ip, "n:digVal", ns),
                    DhRecbto = Texto(ip, "n:dhRecbto", ns)
                };
            }
        }

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

        static string Texto(XmlNode raiz, string xpath, XmlNamespaceManager ns)
        {
            var n = raiz.SelectSingleNode(xpath, ns);
            return n == null ? null : n.InnerText.Trim();
        }

        public static decimal? Decimal(string s)
        {
            decimal d;
            if (s != null && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }
    }
}
