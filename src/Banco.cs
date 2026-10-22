using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DuplicidadeNfce
{
    // Uma linha da aba Contingência do Metre (chef.nota_saida com situacao = 'Em contigencia').
    class LinhaContingencia
    {
        public string Id, Numero, Serie, Pedido, Chave, Motivo;
        public decimal? Valor;
        public DateTime? DtEmissao, DtContingencia, DtTentativa;
    }

    // Lê a lista de contingência do banco do Metre (somente leitura) ou de um CSV exportado pelo HeidiSQL.
    static class Banco
    {
        static readonly string[] Colunas =
            { "id_nota_saida", "numero", "serie", "num_pedido", "chave_acesso", "valor_total_nota",
              "dt_emissao", "data_contigencia", "dt_tentativa_emissao", "motivo_nao_transmissao" };

        public static List<LinhaContingencia> Ler(Config cfg, Log log)
        {
            string f = (cfg.FonteContingencia ?? "arquivos").Trim();
            if (f.StartsWith("csv:", StringComparison.OrdinalIgnoreCase)) return LerCsv(f.Substring(4).Trim(), log);
            if (f.Equals("banco", StringComparison.OrdinalIgnoreCase)) return LerBanco(cfg, log);
            return null;
        }

        // ---------- CSV exportado do HeidiSQL ("Exportar registros da lista", Excel CSV, com nomes das colunas) ----------
        public static List<LinhaContingencia> LerCsv(string arquivo, Log log)
        {
            if (!File.Exists(arquivo)) throw new Exception("CSV da contingência não existe: " + arquivo);
            var linhas = File.ReadAllLines(arquivo, Encoding.UTF8).Where(l => l.Trim() != "").ToList();
            if (linhas.Count == 0) throw new Exception("CSV da contingência vazio: " + arquivo);
            var cab = Csv.Dividir(linhas[0]).Select(c => c.Trim().ToLowerInvariant()).ToList();
            var lista = new List<LinhaContingencia>();
            foreach (string l in linhas.Skip(1))
            {
                var c = Csv.Dividir(l);
                Func<string, string> v = nome =>
                {
                    int i = cab.IndexOf(nome);
                    if (i < 0 || i >= c.Count) return null;
                    string s = c[i];
                    return s == "\\N" || s == "" ? null : s;
                };
                lista.Add(Montar(v("id_nota_saida"), v("numero"), v("serie"), v("num_pedido"), v("chave_acesso"), v("valor_total_nota"),
                    v("dt_emissao"), v("data_contigencia"), v("dt_tentativa_emissao"), v("motivo_nao_transmissao")));
            }
            log.Tela("Contingência lida do CSV " + arquivo + ": " + lista.Count + " nota(s)");
            return lista;
        }

        static LinhaContingencia Montar(string id, string numero, string serie, string pedido, string chave, string valor,
            string dtEmi, string dtCont, string dtTent, string motivo)
        {
            return new LinhaContingencia
            {
                Id = id, Numero = numero, Serie = serie, Pedido = pedido, Chave = chave, Motivo = motivo,
                Valor = Decimal(valor), DtEmissao = Data(dtEmi), DtContingencia = Data(dtCont), DtTentativa = Data(dtTent)
            };
        }

        static decimal? Decimal(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            decimal d;
            // Banco devolve "48.00"; o CSV do HeidiSQL em português, "48,00".
            if (decimal.TryParse(s.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        static DateTime? Data(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTime d;
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        // ---------- Banco MariaDB do Metre, pela libmariadb.dll que vem com o HeidiSQL ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string arquivo);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_init(IntPtr m);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_real_connect(IntPtr m, byte[] host, byte[] user, byte[] senha, byte[] db, uint porta, IntPtr socket, uint flags);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mysql_set_character_set(IntPtr m, byte[] cs);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mysql_query(IntPtr m, byte[] sql);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_store_result(IntPtr m);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern uint mysql_num_fields(IntPtr r);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_fetch_row(IntPtr r);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_fetch_lengths(IntPtr r);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mysql_free_result(IntPtr r);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mysql_close(IntPtr m);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_error(IntPtr m);

        static byte[] Z(string s) { return s == null ? null : Encoding.UTF8.GetBytes(s + "\0"); }

        static string Erro(IntPtr m)
        {
            IntPtr p = mysql_error(m);
            if (p == IntPtr.Zero) return "erro desconhecido";
            var b = new List<byte>();
            for (int i = 0; ; i++) { byte x = Marshal.ReadByte(p, i); if (x == 0) break; b.Add(x); }
            return Encoding.UTF8.GetString(b.ToArray());
        }

        static void CarregarDll(Config cfg)
        {
            var tentativas = new List<string>();
            if (!string.IsNullOrEmpty(cfg.MariaDbDll)) tentativas.Add(cfg.MariaDbDll);
            tentativas.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libmariadb.dll"));
            // Instalação do MariaDB do Metre (a DLL fica em bin\ ou lib\) e o HeidiSQL.
            foreach (string pasta in new[] { cfg.PastaBancoMetre, @"C:\Program Files\HeidiSQL", @"C:\Program Files (x86)\HeidiSQL" })
            {
                if (string.IsNullOrEmpty(pasta) || !Directory.Exists(pasta)) continue;
                tentativas.Add(Path.Combine(pasta, "libmariadb.dll"));
                try { tentativas.AddRange(Directory.GetFiles(pasta, "libmariadb.dll", SearchOption.AllDirectories)); }
                catch (Exception ex) { tentativas.Add("(sem acesso a " + pasta + ": " + ex.Message + ")"); }
            }
            foreach (string t in tentativas.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(t)) continue;
                if (LoadLibrary(t) != IntPtr.Zero) return;
                throw new Exception("não foi possível carregar " + t + " (erro " + Marshal.GetLastWin32Error() + "; o exe roda em " +
                    (Environment.Is64BitProcess ? "64" : "32") + " bits e a DLL precisa ser igual)");
            }
            throw new Exception("libmariadb.dll não encontrada (procurado em: " + string.Join("; ", tentativas) + "). Informe MariaDbDll no duplicidade.ini.");
        }

        // Só SELECT, numa sessão marcada como somente leitura: nada é gravado no banco do Metre.
        public static List<LinhaContingencia> LerBanco(Config cfg, Log log)
        {
            CarregarDll(cfg);
            IntPtr m = mysql_init(IntPtr.Zero);
            if (m == IntPtr.Zero) throw new Exception("mysql_init falhou");
            try
            {
                if (mysql_real_connect(m, Z(cfg.DbHost), Z(cfg.DbUsuario), Z(cfg.DbSenha), Z(cfg.DbBanco), (uint)cfg.DbPorta, IntPtr.Zero, 0) == IntPtr.Zero)
                    throw new Exception("conexão com o banco " + cfg.DbHost + ":" + cfg.DbPorta + " falhou: " + Erro(m));
                mysql_set_character_set(m, Z("utf8mb4"));
                if (mysql_query(m, Z("SET SESSION TRANSACTION READ ONLY")) != 0)
                    throw new Exception("não foi possível abrir a sessão somente leitura: " + Erro(m));
                IntPtr r0 = mysql_store_result(m);
                if (r0 != IntPtr.Zero) mysql_free_result(r0);

                string sql = "SELECT " + string.Join(", ", Colunas) + " FROM " + cfg.DbBanco + ".nota_saida WHERE situacao = '" +
                    cfg.DbSituacao.Replace("'", "''") + "' ORDER BY data_contigencia";
                if (mysql_query(m, Z(sql)) != 0) throw new Exception("consulta da contingência falhou: " + Erro(m));
                IntPtr r = mysql_store_result(m);
                if (r == IntPtr.Zero) throw new Exception("consulta da contingência sem resultado: " + Erro(m));
                var lista = new List<LinhaContingencia>();
                try
                {
                    int n = (int)mysql_num_fields(r);
                    IntPtr row;
                    while ((row = mysql_fetch_row(r)) != IntPtr.Zero)
                    {
                        IntPtr lens = mysql_fetch_lengths(r);
                        var v = new string[n];
                        for (int i = 0; i < n; i++)
                        {
                            IntPtr p = Marshal.ReadIntPtr(row, i * IntPtr.Size);
                            if (p == IntPtr.Zero) continue;
                            int len = Marshal.ReadInt32(lens, i * 4);   // unsigned long = 32 bits no Windows
                            var b = new byte[len];
                            Marshal.Copy(p, b, 0, len);
                            v[i] = Encoding.UTF8.GetString(b);
                        }
                        lista.Add(Montar(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]));
                    }
                }
                finally { mysql_free_result(r); }
                log.Tela("Contingência lida do banco " + cfg.DbBanco + " (" + cfg.DbHost + ":" + cfg.DbPorta + "): " + lista.Count + " nota(s)");
                return lista;
            }
            finally { mysql_close(m); }
        }
    }
}
