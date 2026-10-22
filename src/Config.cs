using System;
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
        public string DbHost = "127.0.0.1";
        public int DbPorta = 3307;
        public string DbUsuario = "metre";
        public string DbSenha = "";
        public string DbBanco = "chef";
        public string DbSituacao = "Em contigencia";
        public string MariaDbDll = "";
        public bool ValidarCertificadoServidor = true;
        public int TimeoutSegundos = 30;

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
                case "dbporta": DbPorta = int.Parse(valor); break;
                case "dbusuario": DbUsuario = valor; break;
                case "dbsenha": DbSenha = valor; break;
                case "dbbanco": DbBanco = valor; break;
                case "dbsituacao": DbSituacao = valor; break;
                case "mariadbdll": MariaDbDll = valor; break;
                case "validarcertificadoservidor": ValidarCertificadoServidor = Sim(valor); break;
                case "timeoutsegundos": TimeoutSegundos = int.Parse(valor); break;
                default: throw new Exception(origem + ": opção desconhecida '" + chave + "'");
            }
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
