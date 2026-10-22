using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;

namespace DuplicidadeNfce
{
    class RespostaSefaz
    {
        public bool Ok;            // houve resposta válida da SEFAZ
        public bool Simulado;      // modo --sem-sefaz: protNFe tirado de arquivo local
        public bool NaoConsultada; // sem certificado utilizável: a SEFAZ nem foi chamada
        public string Erro;
        public string CStat, XMotivo, DhRecbto;
        public ProtocoloXml Prot;
        public string XmlBruto;

        public bool Autorizada
        {
            get
            {
                return Ok && (CStat == "100" || CStat == "150") && Prot != null
                    && (Prot.CStat == "100" || Prot.CStat == "150");
            }
        }

        public string Situacao
        {
            get
            {
                if (NaoConsultada) return "NÃO CONSULTADA";
                if (!Ok) return "SEM RESPOSTA";
                string s = CStat + " - " + XMotivo;
                return Simulado ? "SIMULADO (protNFe local, SEFAZ não consultada): " + s : s;
            }
        }
    }

    // Consulta de situação (NFeConsultaProtocolo 4.00). Somente leitura: não transmite nem altera nada.
    class ClienteSefaz
    {
        const string NsWsdl = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeConsultaProtocolo4";
        const string NsNfe = "http://www.portalfiscal.inf.br/nfe";

        readonly Config cfg;
        readonly X509Certificate2 cert;
        readonly Dictionary<string, RespostaSefaz> cache = new Dictionary<string, RespostaSefaz>();
        public string Url;

        public ClienteSefaz(Config cfg, X509Certificate2 cert)
        {
            this.cfg = cfg;
            this.cert = cert;
            Url = UrlConsulta(cfg);
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            if (!cfg.ValidarCertificadoServidor)
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        }

        public static string UrlConsulta(Config cfg)
        {
            if (!string.IsNullOrEmpty(cfg.UrlConsulta)) return cfg.UrlConsulta;
            if (cfg.UF.ToUpperInvariant() == "MG")
                return cfg.Ambiente == 1
                    ? "https://nfce.fazenda.mg.gov.br/nfce/services/NFeConsultaProtocolo4"
                    : "https://hnfce.fazenda.mg.gov.br/nfce/services/NFeConsultaProtocolo4";
            throw new Exception("UF " + cfg.UF + " sem URL padrão; informe UrlConsulta no arquivo de configuração.");
        }

        public RespostaSefaz Consultar(string chave)
        {
            RespostaSefaz r;
            if (cache.TryGetValue(chave, out r)) return r;
            r = ConsultarSemCache(chave);
            cache[chave] = r;
            return r;
        }

        RespostaSefaz ConsultarSemCache(string chave)
        {
            var r = new RespostaSefaz();
            string envelope =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<soap12:Envelope xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:soap12=\"http://www.w3.org/2003/05/soap-envelope\">" +
                "<soap12:Body><nfeDadosMsg xmlns=\"" + NsWsdl + "\">" +
                "<consSitNFe xmlns=\"" + NsNfe + "\" versao=\"4.00\"><tpAmb>" + cfg.Ambiente + "</tpAmb><xServ>CONSULTAR</xServ><chNFe>" + chave + "</chNFe></consSitNFe>" +
                "</nfeDadosMsg></soap12:Body></soap12:Envelope>";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Url);
                req.Method = "POST";
                req.ContentType = "application/soap+xml; charset=utf-8; action=\"" + NsWsdl + "/nfeConsultaNF\"";
                req.Timeout = cfg.TimeoutSegundos * 1000;
                req.ServicePoint.Expect100Continue = false;
                req.ClientCertificates.Add(cert);
                byte[] corpo = Encoding.UTF8.GetBytes(envelope);
                req.ContentLength = corpo.Length;
                using (var s = req.GetRequestStream()) s.Write(corpo, 0, corpo.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    r.XmlBruto = sr.ReadToEnd();
                Interpretar(r, chave);
            }
            catch (WebException ex)
            {
                r.Ok = false;
                r.Erro = ex.Message;
                if (ex.Response != null)
                {
                    try
                    {
                        using (var sr = new StreamReader(ex.Response.GetResponseStream()))
                            r.XmlBruto = sr.ReadToEnd();
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Erro = ex.Message;
            }
            return r;
        }

        static void Interpretar(RespostaSefaz r, string chave)
        {
            var xd = new XmlDocument();
            xd.XmlResolver = null;
            xd.LoadXml(r.XmlBruto);
            var ns = new XmlNamespaceManager(xd.NameTable);
            ns.AddNamespace("n", NsNfe);
            var ret = xd.SelectSingleNode("//n:retConsSitNFe", ns);
            if (ret == null) { r.Ok = false; r.Erro = "resposta sem retConsSitNFe"; return; }
            r.Ok = true;
            r.CStat = Txt(ret, "n:cStat", ns);
            r.XMotivo = Txt(ret, "n:xMotivo", ns);
            r.DhRecbto = Txt(ret, "n:dhRecbto", ns);
            var ip = ret.SelectSingleNode("n:protNFe/n:infProt", ns);
            if (ip != null)
                r.Prot = new ProtocoloXml
                {
                    ChNFe = Txt(ip, "n:chNFe", ns),
                    CStat = Txt(ip, "n:cStat", ns),
                    XMotivo = Txt(ip, "n:xMotivo", ns),
                    NProt = Txt(ip, "n:nProt", ns),
                    DigVal = Txt(ip, "n:digVal", ns),
                    DhRecbto = Txt(ip, "n:dhRecbto", ns)
                };
        }

        static string Txt(XmlNode raiz, string xp, XmlNamespaceManager ns)
        {
            var n = raiz.SelectSingleNode(xp, ns);
            return n == null ? null : n.InnerText.Trim();
        }

        // Certificado A1 (.pfx) cujo CNPJ é o do emitente. Devolve null com o motivo em 'erro'.
        // A senha nunca entra em mensagens.
        public static X509Certificate2 CarregarCertificado(Config cfg, string cnpj, Log log, out string erro, out string arquivo)
        {
            erro = null;
            arquivo = null;
            var arquivos = ListarPfx(cfg);
            if (arquivos.Count == 0)
            {
                erro = "nenhum .pfx encontrado em " + cfg.CertificadoPfx + " nem em " + cfg.PastaEmpresas + @"\*\Certificado";
                return null;
            }
            if (MetreIni.Senhas.Count == 0)
            {
                erro = "senha do certificado não encontrada (" + DescreverFontes(cfg) + ")";
                return null;
            }

            X509Certificate2 melhor = null;
            var falhas = new List<string>();
            foreach (string f in arquivos)
            {
                string origem;
                var c = Abrir(f, out origem);
                if (c == null)
                {
                    falhas.Add(f + ": nenhuma senha encontrada abriu o arquivo (senha incorreta, criptografada ou arquivo inválido)");
                    continue;
                }
                log.Info("Certificado " + f + " aberto com a senha de " + origem);
                if (!TemCnpj(c, cnpj)) { log.Info("Certificado " + f + " é de outro CNPJ (" + c.Subject + ")"); continue; }
                if (!c.HasPrivateKey) { falhas.Add(f + ": sem chave privada"); continue; }
                if (c.NotAfter < DateTime.Now) { falhas.Add(f + ": vencido em " + c.NotAfter.ToString("dd/MM/yyyy")); continue; }
                if (melhor == null || c.NotAfter > melhor.NotAfter) { melhor = c; arquivo = f; }
            }
            if (melhor == null)
                erro = "nenhum certificado válido do CNPJ " + cnpj + (falhas.Count > 0 ? " (" + string.Join("; ", falhas) + ")" : "");
            return melhor;
        }

        // Tenta as senhas conhecidas (duplicidade.ini e INIs do emissor). Só a origem da senha é devolvida.
        static X509Certificate2 Abrir(string arquivo, out string origem)
        {
            origem = null;
            foreach (var s in MetreIni.Senhas)
            {
                try
                {
                    var c = new X509Certificate2(arquivo, s.Valor);
                    origem = s.Origem;
                    return c;
                }
                catch (CryptographicException) { }
            }
            return null;
        }

        static string DescreverFontes(Config cfg)
        {
            return MetreIni.Lidos.Count == 0
                ? "nenhum INI encontrado em " + cfg.IniMetre + " e CertificadoSenha vazia"
                : "procurado em " + string.Join(", ", MetreIni.Lidos);
        }

        // Para o selo da interface: o certificado que abrir (prefere o indicado no INI do emissor e o de maior validade).
        public static X509Certificate2 CertificadoParaSelo(Config cfg, out string erro, out string arquivo)
        {
            erro = null;
            arquivo = null;
            var arquivos = ListarPfx(cfg);
            if (arquivos.Count == 0) { erro = "nenhum .pfx encontrado"; return null; }
            if (MetreIni.Senhas.Count == 0) { erro = "senha não encontrada (" + DescreverFontes(cfg) + ")"; return null; }
            X509Certificate2 melhor = null;
            foreach (string f in arquivos)
            {
                string origem;
                var c = Abrir(f, out origem);
                if (c == null) continue;
                if (melhor == null || c.NotAfter > melhor.NotAfter) { melhor = c; arquivo = f; }
                if (MetreIni.Pfxs.Contains(f, StringComparer.OrdinalIgnoreCase)) { melhor = c; arquivo = f; break; }
            }
            if (melhor == null) erro = "nenhuma senha encontrada abriu o certificado";
            return melhor;
        }

        static List<string> ListarPfx(Config cfg)
        {
            var lista = new List<string>();
            foreach (string p in MetreIni.Pfxs)
                if (File.Exists(p)) lista.Add(Path.GetFullPath(p));
            Action<string> pasta = dir =>
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string e = Path.GetExtension(f).ToLowerInvariant();
                    if (e == ".pfx" || e == ".p12") lista.Add(Path.GetFullPath(f));
                }
            };
            if (!string.IsNullOrEmpty(cfg.CertificadoPfx))
            {
                if (File.Exists(cfg.CertificadoPfx)) lista.Add(Path.GetFullPath(cfg.CertificadoPfx));
                else pasta(cfg.CertificadoPfx);
            }
            if (!string.IsNullOrEmpty(cfg.PastaEmpresas) && Directory.Exists(cfg.PastaEmpresas))
                foreach (string emp in Directory.GetDirectories(cfg.PastaEmpresas))
                    pasta(Path.Combine(emp, "Certificado"));
            return lista.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ICP-Brasil: CNPJ no CN ("RAZAO:CNPJ") e no SubjectAltName (OID 2.16.76.1.3.3).
        static bool TemCnpj(X509Certificate2 c, string cnpj)
        {
            if (c.Subject.Contains(cnpj)) return true;
            foreach (var ext in c.Extensions)
                if (ext.Oid != null && ext.Oid.Value == "2.5.29.17" && Encoding.ASCII.GetString(ext.RawData).Contains(cnpj))
                    return true;
            return false;
        }
    }
}
