using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DuplicidadeNfce
{
    class Config
    {
        public string PastaRaiz = @"C:\Chef\NFCe";
        public DateTime? DataInicial, DataFinal;
        public bool TodasDatas;
        // Dias após DataFinal em que também se procuram candidatos (a autorização pode cair em outra pasta de dia).
        public int DiasExtrasCandidatos = 3;
        public string PastaDestino = @"C:\Chef\NFCe\Duplicidade";
        public string PastaRelatorios = "relatorios";
        public string UF = "MG";
        public int Ambiente = 1;
        public string UrlConsulta = "";
        // Arquivo .pfx ou pasta com .pfx; PastaEmpresas\*\Certificado também é examinada (escolhe pelo CNPJ do emitente).
        public string CertificadoPfx = @"C:\Chef\Empresa\1\Certificado";
        public string PastaEmpresas = @"C:\Chef\Empresa";
        // Opcional: se vazia, a senha vem dos INIs do sistema emissor (IniMetre).
        public string CertificadoSenha = "";
        public string IniMetre = @"C:\Chef\NFCe.ini;C:\Chef\Config.ini";

        // De onde vem a lista da contingência: "banco" (chef.nota_saida, só leitura), "csv:<arquivo>" (exportado do
        // HeidiSQL) ou "arquivos" (deduzida dos XMLs, sem saber o que já foi resolvido no Metre).
        public string FonteContingencia = "arquivos";
        // Host e porta vazios = os do Metre (IniConexaoMetre, [Configuracao] ip/porta); sem ele, 127.0.0.1:3307.
        // Usuário e senha não ficam no Conexao.ini: vêm daqui (duplicidade.ini).
        public string DbHost = "";
        public int DbPorta = 0;
        public string IniConexaoMetre = @"C:\Chef\Conexao.ini";
        public string OrigemConexaoBanco;
        public string DbUsuario = "metre";
        public string DbSenha = "";
        // A mesma senha criptografada pelo Windows (gravada pela tela de senha da interface; ver SenhaBanco).
        public string DbSenhaProtegida = "";
        public bool SenhaEmTextoNoIni;
        public string ErroSenhaProtegida;
        public string DbBanco = "chef";
        public string DbBancoXml = "chefxml";
        public string DbSituacao = "Em contigencia";
        public string MariaDbDll = "";
        public string PastaBancoMetre = @"C:\Chef\Banco de Dados";
        // Outros lugares com XMLs (caixa em \\CAIXA1\Chef\NFCe, pasta trazida num pen drive), separados por ';'.
        public string PastasXmlExtras = "";
        public bool ValidarCertificadoServidor = true;
        public int TimeoutSegundos = 30;

        // Notas Faltantes: cópias em <PastaNotasFaltantes>\<CNPJ>_<data-hora>; histórico na pasta base.
        public string PastaNotasFaltantes = @"C:\Chef\NFCe\NotasFaltantes";
        // Trava contra número ou faixa digitada errada (ex.: um dígito a mais no fim da faixa).
        public int LimiteNumerosFaltantes = 5000;

        public static Config Carregar(string caminho)
        {
            var c = new Config();
            if (!File.Exists(caminho)) return c;
            int linhaN = 0;
            foreach (string bruta in File.ReadAllLines(caminho, Encoding.UTF8))
            {
                linhaN++;
                string linha = bruta.Trim();
                if (linha == "" || linha.StartsWith("#") || linha.StartsWith(";") || linha.StartsWith("[")) continue;
                int eq = linha.IndexOf('=');
                if (eq < 0) throw new Exception(caminho + " linha " + linhaN + ": esperado chave=valor");
                c.Definir(linha.Substring(0, eq).Trim(), linha.Substring(eq + 1).Trim(), caminho + " linha " + linhaN);
            }
            c.SenhaEmTextoNoIni = c.DbSenha != "";
            if (c.DbSenha == "" && c.DbSenhaProtegida != "")
            {
                try { c.DbSenha = SenhaBanco.Abrir(c.DbSenhaProtegida); }
                catch (Exception ex) { c.ErroSenhaProtegida = ex.Message; }
            }
            return c;
        }

        public void Definir(string chave, string valor, string origem)
        {
            switch (chave.ToLowerInvariant())
            {
                case "pastaraiz": PastaRaiz = valor; break;
                case "datainicial": DataInicial = Data(valor, origem); break;
                case "datafinal": DataFinal = Data(valor, origem); break;
                case "diasextrascandidatos": DiasExtrasCandidatos = int.Parse(valor); break;
                case "pastadestino": PastaDestino = valor; break;
                case "pastarelatorios": PastaRelatorios = valor; break;
                case "uf": UF = valor; break;
                case "ambiente": Ambiente = int.Parse(valor); break;
                case "urlconsulta": UrlConsulta = valor; break;
                case "certificadopfx": CertificadoPfx = valor; break;
                case "pastaempresas": PastaEmpresas = valor; break;
                case "certificadosenha": CertificadoSenha = valor; break;
                case "inimetre": IniMetre = valor; break;
                case "fontecontingencia": FonteContingencia = valor; break;
                case "dbhost": DbHost = valor; break;
                case "dbporta": DbPorta = valor == "" ? 0 : int.Parse(valor); break;
                case "iniconexaometre": IniConexaoMetre = valor; break;
                case "dbbancoxml": DbBancoXml = NomeBanco(valor, origem); break;
                case "dbusuario": DbUsuario = valor; break;
                case "dbsenha": DbSenha = valor; break;
                case "dbsenhaprotegida": DbSenhaProtegida = valor; break;
                case "dbbanco": DbBanco = NomeBanco(valor, origem); break;
                case "dbsituacao": DbSituacao = valor; break;
                case "mariadbdll": MariaDbDll = valor; break;
                case "pastabancometre": PastaBancoMetre = valor; break;
                case "pastasxmlextras": PastasXmlExtras = valor; break;
                case "validarcertificadoservidor": ValidarCertificadoServidor = Sim(valor); break;
                case "timeoutsegundos": TimeoutSegundos = int.Parse(valor); break;
                case "pastanotasfaltantes": PastaNotasFaltantes = valor; break;
                case "limitenumerosfaltantes": LimiteNumerosFaltantes = int.Parse(valor); break;
                default: throw new Exception(origem + ": opção desconhecida '" + chave + "'");
            }
        }

        // Pasta do Chef em uso, deduzida da pasta dos XMLs (<Chef>\NFCe).
        public string PastaChef
        {
            get
            {
                string p = (PastaRaiz ?? "").TrimEnd('\\');
                return p.EndsWith(@"\NFCe", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 5) : p;
            }
        }

        // As opções que dependem da pasta do Chef (C:\Chef neste computador ou \\SERVIDOR\Chef pela rede).
        public static Dictionary<string, string> OpcoesDaPastaChef(string chef)
        {
            return new Dictionary<string, string>
            {
                { "PastaRaiz", chef + @"\NFCe" },
                { "PastaDestino", chef + @"\NFCe\Duplicidade" },
                { "PastaNotasFaltantes", chef + @"\NFCe\NotasFaltantes" },
                { "CertificadoPfx", chef + @"\Empresa\1\Certificado" },
                { "PastaEmpresas", chef + @"\Empresa" },
                { "IniMetre", chef + @"\NFCe.ini;" + chef + @"\Config.ini" },
                { "IniConexaoMetre", chef + @"\Conexao.ini" },
                { "PastaBancoMetre", chef + @"\Banco de Dados" }
            };
        }

        // Troca só as opções informadas no ini (a que não existir entra no fim); comentários, senha e o resto ficam como estão.
        public static void GravarOpcoes(string arqIni, IDictionary<string, string> opcoes)
        {
            var linhas = File.Exists(arqIni) ? new List<string>(File.ReadAllLines(arqIni, Encoding.UTF8)) : new List<string>();
            var faltam = new Dictionary<string, string>(opcoes, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < linhas.Count; i++)
            {
                string l = linhas[i].Trim();
                int eq = l.IndexOf('=');
                if (l == "" || l.StartsWith("#") || l.StartsWith(";") || eq <= 0) continue;
                string k = l.Substring(0, eq).Trim(), v;
                if (faltam.TryGetValue(k, out v)) { linhas[i] = k + "=" + v; faltam.Remove(k); }
            }
            foreach (var o in faltam) linhas.Add(o.Key + "=" + o.Value);
            string tmp = arqIni + ".tmp";
            File.WriteAllLines(tmp, linhas, new UTF8Encoding(false));
            if (File.Exists(arqIni)) File.Replace(tmp, arqIni, null);
            else File.Move(tmp, arqIni);
        }

        // Completa host e porta do banco com o Conexao.ini do Metre.
        public void ResolverConexaoBanco()
        {
            if (!string.IsNullOrEmpty(DbHost) && DbPorta > 0) return;
            string ip = null, porta = null, secao = "";
            if (!string.IsNullOrEmpty(IniConexaoMetre) && File.Exists(IniConexaoMetre))
                foreach (string bruta in File.ReadAllLines(IniConexaoMetre, Encoding.Default))
                {
                    string l = bruta.Trim();
                    if (l.StartsWith("[") && l.EndsWith("]")) { secao = l.Substring(1, l.Length - 2).Trim(); continue; }
                    int eq = l.IndexOf('=');
                    if (eq <= 0 || !secao.Equals("Configuracao", StringComparison.OrdinalIgnoreCase)) continue;
                    string k = l.Substring(0, eq).Trim().ToLowerInvariant(), v = l.Substring(eq + 1).Trim();
                    if (k == "ip") ip = v;
                    else if (k == "porta") porta = v;
                }
            // \\SERVIDOR\Chef\Conexao.ini costuma dizer localhost: lá é o próprio servidor; daqui, é quem compartilha a pasta.
            if (IniConexaoMetre != null && IniConexaoMetre.StartsWith(@"\\") && (ip == "localhost" || ip == "127.0.0.1" || ip == "::1"))
                ip = IniConexaoMetre.Substring(2).Split('\\')[0];
            int p;
            bool doIni = false;
            if (string.IsNullOrEmpty(DbHost))
            {
                doIni |= !string.IsNullOrEmpty(ip);
                DbHost = string.IsNullOrEmpty(ip) ? "127.0.0.1" : ip;
            }
            if (DbPorta <= 0)
            {
                bool ok = int.TryParse(porta, out p) && p > 0;
                doIni |= ok;
                DbPorta = ok ? p : 3307;
            }
            OrigemConexaoBanco = doIni ? "host/porta de " + IniConexaoMetre : null;
        }

        // Nome de banco entra direto no SQL (chef.nota_saida): só letras, números e _.
        static string NomeBanco(string v, string origem)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(v, "^[A-Za-z0-9_]{1,64}$"))
                throw new Exception(origem + ": nome de banco inválido '" + v + "' (use só letras, números e _)");
            return v;
        }

        static bool Sim(string v)
        {
            v = v.ToLowerInvariant();
            return v == "sim" || v == "s" || v == "true" || v == "1";
        }

        // Aceita dd/mm/aaaa, "hoje", "ontem" ou "-N" (N dias atrás). Vazio = sem data.
        public static DateTime? Data(string v, string origem)
        {
            v = v.Trim().ToLowerInvariant();
            if (v == "") return null;
            if (v == "hoje") return DateTime.Today;
            if (v == "ontem") return DateTime.Today.AddDays(-1);
            int dias;
            if (v.StartsWith("-") && int.TryParse(v.Substring(1), out dias)) return DateTime.Today.AddDays(-dias);
            DateTime d;
            if (DateTime.TryParseExact(v, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            throw new Exception(origem + ": data inválida '" + v + "' (use dd/mm/aaaa, hoje, ontem ou -N)");
        }
    }
}
