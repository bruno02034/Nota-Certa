using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuplicidadeNfce
{
    // Senha possível do certificado. 'Origem' (arquivo [seção] chave) pode ir para o log; 'Valor' nunca.
    class FonteSenha
    {
        public string Origem;
        public string Valor;
    }

    // Lê o caminho do .pfx e a senha do certificado nos INIs do sistema emissor (C:\Chef\NFCe.ini, C:\Chef\Config.ini).
    // Os nomes exatos das chaves ainda não foram confirmados: procura por nome (senha/password) e testa o valor
    // como está gravado. Nenhuma tentativa de decodificar é feita.
    static class MetreIni
    {
        public static List<FonteSenha> Senhas = new List<FonteSenha>();
        public static List<string> Pfxs = new List<string>();
        public static List<string> Lidos = new List<string>();

        // Monta listas novas e só então publica (a interface lê o selo enquanto lista as notas).
        public static void Carregar(Config cfg)
        {
            var senhas = new List<FonteSenha>();
            var pfxs = new List<string>();
            var lidos = new List<string>();
            Ler(cfg, senhas, pfxs, lidos);
            Senhas = senhas;
            Pfxs = pfxs;
            Lidos = lidos;
        }

        static void Ler(Config cfg, List<FonteSenha> Senhas, List<string> Pfxs, List<string> Lidos)
        {
            if (!string.IsNullOrEmpty(cfg.CertificadoSenha))
                Senhas.Add(new FonteSenha { Origem = "duplicidade.ini CertificadoSenha", Valor = cfg.CertificadoSenha });

            foreach (string arq in (cfg.IniMetre ?? "").Split(';').Select(s => s.Trim()).Where(s => s != ""))
            {
                if (!File.Exists(arq)) continue;
                Lidos.Add(arq);
                string baseDir = Path.GetDirectoryName(Path.GetFullPath(arq));
                var senhasArq = new List<FonteSenha>();
                foreach (var e in LerIni(arq))
                {
                    string chave = e.Chave.ToUpperInvariant();
                    string v = e.Valor.Trim().Trim('"');
                    if (v == "") continue;
                    string ext = Path.GetExtension(v).ToLowerInvariant();
                    if (ext == ".pfx" || ext == ".p12")
                    {
                        string p = Path.IsPathRooted(v) ? v : Path.Combine(baseDir, v);
                        if (!Pfxs.Contains(p, StringComparer.OrdinalIgnoreCase)) Pfxs.Add(p);
                    }
                    else if (chave.Contains("SENHA") || chave.Contains("PASSWORD") || chave.Contains("PWD") || chave == "PASS")
                        senhasArq.Add(new FonteSenha { Origem = arq + " [" + e.Secao + "] " + e.Chave, Valor = v });
                }
                // Chaves que falam de certificado primeiro.
                Senhas.AddRange(senhasArq.OrderBy(s => s.Origem.ToUpperInvariant().Contains("CERT") ? 0 : 1));
            }
        }

        class EntradaIni { public string Secao, Chave, Valor; }

        static IEnumerable<EntradaIni> LerIni(string arq)
        {
            string secao = "";
            // INIs de sistemas Delphi costumam estar em ANSI (Windows-1252).
            foreach (string bruta in File.ReadAllLines(arq, Encoding.Default))
            {
                string l = bruta.Trim();
                if (l == "" || l.StartsWith(";") || l.StartsWith("#")) continue;
                if (l.StartsWith("[") && l.EndsWith("]")) { secao = l.Substring(1, l.Length - 2); continue; }
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                yield return new EntradaIni { Secao = secao, Chave = l.Substring(0, eq).Trim(), Valor = l.Substring(eq + 1) };
            }
        }
    }
}
