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
        // Trechos da resposta, como a SEFAZ mandou: protNFe e eventos (procEventoNFe), para montar os XMLs de entrega.
        public string ProtNFeXml;
        public List<EventoSefaz> Eventos = new List<EventoSefaz>();

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

    class EventoSefaz
    {
        public string TpEvento, CStat, XMotivo, NProt, ChNFe, Xml;
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
        }

        // Desligar a validação do certificado do servidor só vale para o simulador local.
        static bool ValidacaoDesligada(Config cfg)
        {
            Uri u;
            return !cfg.ValidarCertificadoServidor && Uri.TryCreate(UrlConsulta(cfg), UriKind.Absolute, out u) && u.IsLoopback;
        }

        // A resposta da SEFAZ decide o que é copiado e o que é gravado no banco (ajuste de chave): só o servidor
        // oficial (https em *.gov.br) ou um simulador local (127.0.0.1, testes) são aceitos no UrlConsulta.
        public static string UrlConsulta(Config cfg)
        {
            if (!string.IsNullOrEmpty(cfg.UrlConsulta))
            {
                Uri u;
                if (!Uri.TryCreate(cfg.UrlConsulta, UriKind.Absolute, out u) ||
                    !(u.IsLoopback || (u.Scheme == Uri.UriSchemeHttps && u.Host.ToLowerInvariant().EndsWith(".gov.br"))))
                    throw new Exception("UrlConsulta não permitida: " + cfg.UrlConsulta + " (só https em *.gov.br, ou 127.0.0.1 para testes)");
                return cfg.UrlConsulta;
            }
            if (cfg.UF.ToUpperInvariant() == "MG")
                return cfg.Ambiente == 1
                    ? "https://nfce.fazenda.mg.gov.br/nfce/services/NFeConsultaProtocolo4"
                    : "https://hnfce.fazenda.mg.gov.br/nfce/services/NFeConsultaProtocolo4";
            throw new Exception("UF " + cfg.UF + " sem URL padrão; informe UrlConsulta no arquivo de configuração.");
        }

        // Consulta por chave com um certificado por CNPJ emitente (posições 7-20 da chave), carregado na primeira
        // consulta. Cada resposta é guardada em pastaSefaz\<chave>.xml.
        public static Func<string, RespostaSefaz> CriarConsulta(Config cfg, Log log, string pastaSefaz)
        {
            var clientes = new Dictionary<string, ClienteSefaz>();
            var semCertificado = new Dictionary<string, string>();
            return k =>
            {
                string cnpj = k.Substring(6, 14);
                ClienteSefaz cli;
                string erro;
                if (!clientes.TryGetValue(cnpj, out cli) && !semCertificado.TryGetValue(cnpj, out erro))
                {
                    log.Tela("Consulta SEFAZ: " + UrlConsulta(cfg) + " (ambiente " + cfg.Ambiente + ")");
                    if (!cfg.ValidarCertificadoServidor)
                        log.Tela(ValidacaoDesligada(cfg) ? "AVISO: validação do certificado do servidor desligada (simulador local)."
                            : "AVISO: ValidarCertificadoServidor=nao ignorado; o certificado da SEFAZ é sempre validado fora de 127.0.0.1.");
                    string arqPfx;
                    var cert = CarregarCertificado(cfg, cnpj, log, out erro, out arqPfx);
                    if (cert == null)
                    {
                        semCertificado[cnpj] = erro;
                        log.Tela("SEFAZ NÃO será consultada para o CNPJ " + cnpj + ": " + erro);
                    }
                    else
                    {
                        clientes[cnpj] = cli = new ClienteSefaz(cfg, cert);
                        log.Tela("Certificado CNPJ " + cnpj + ": " + arqPfx + " - " + cert.Subject + " (validade " + cert.NotAfter.ToString("dd/MM/yyyy") + ")");
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
                if (ValidacaoDesligada(cfg)) req.ServerCertificateValidationCallback = delegate { return true; };
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
            // Resposta de rede: sem DTD nem entidades (como na leitura dos XMLs do disco, NotaXml).
            var xd = new XmlDocument { XmlResolver = null };
            using (var sr = new StringReader(r.XmlBruto))
            using (var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                xd.Load(xr);
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
            var prot = ret.SelectSingleNode("n:protNFe", ns) as XmlElement;
            if (prot != null) r.ProtNFeXml = Trecho(r.XmlBruto, "protNFe") ?? prot.OuterXml;
            var brutos = Trechos(r.XmlBruto, "procEventoNFe");
            int i = 0;
            foreach (XmlElement pe in ret.SelectNodes("n:procEventoNFe", ns))
            {
                r.Eventos.Add(new EventoSefaz
                {
                    TpEvento = Txt(pe, "n:evento/n:infEvento/n:tpEvento", ns),
                    ChNFe = Txt(pe, "n:evento/n:infEvento/n:chNFe", ns),
                    CStat = Txt(pe, "n:retEvento/n:infEvento/n:cStat", ns),
                    XMotivo = Txt(pe, "n:retEvento/n:infEvento/n:xMotivo", ns),
                    NProt = Txt(pe, "n:retEvento/n:infEvento/n:nProt", ns),
                    Xml = i < brutos.Count ? brutos[i] : pe.OuterXml
                });
                i++;
            }
        }

        // O texto exato do elemento na resposta (sem prefixo), para não mexer em nada do que a SEFAZ assinou.
        static string Trecho(string xml, string tag)
        {
            var l = Trechos(xml, tag);
            return l.Count > 0 ? l[0] : null;
        }

        static List<string> Trechos(string xml, string tag)
        {
            var lista = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, "<" + tag + @"\b[\s\S]*?</" + tag + ">"))
                lista.Add(m.Value);
            return lista;
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
