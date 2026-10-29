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
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mysql_free_result(IntPtr r);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mysql_close(IntPtr m);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_error(IntPtr m);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mysql_options(IntPtr m, int opcao, ref uint valor);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern ulong mysql_affected_rows(IntPtr m);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_fetch_lengths(IntPtr r);
        [DllImport("libmariadb.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mysql_fetch_field_direct(IntPtr r, uint i);
        const int MYSQL_OPT_CONNECT_TIMEOUT = 0, MYSQL_OPT_READ_TIMEOUT = 11, MYSQL_OPT_WRITE_TIMEOUT = 12;

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
            // Instalação do MariaDB do Metre (a DLL fica em bin\ ou lib\) e o HeidiSQL. A pasta do exe NÃO entra
            // (uma DLL falsa largada ao lado do NotaCerta.exe rodaria com a senha do banco); para usar uma DLL de
            // outro lugar, informe MariaDbDll no ini.
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
                if (LoadLibrary(t) != IntPtr.Zero) { DllCarregada = t; return; }
                throw new Exception("não foi possível carregar " + t + " (erro " + Marshal.GetLastWin32Error() + "; o exe roda em " +
                    (Environment.Is64BitProcess ? "64" : "32") + " bits e a DLL precisa ser igual)");
            }
            throw new Exception("libmariadb.dll não encontrada (procurado em: " + string.Join("; ", tentativas) + "). Informe MariaDbDll no duplicidade.ini.");
        }

        public static string DllCarregada;

        // Só SELECT, numa sessão marcada como somente leitura: nada é gravado no banco do Metre.
        // 'passo' mostra o andamento (usado pelo --testar-banco para saber onde parou, se parar).
        public static List<string[]> Consultar(Config cfg, IEnumerable<string> sqls, Action<string> passo)
        {
            using (var s = Sessao.Abrir(cfg, passo, true))
            {
                List<string[]> ultimo = null;
                foreach (string sql in sqls)
                {
                    s.Passo("consultando: " + (sql.Length > 70 ? sql.Substring(0, 70) + "..." : sql));
                    ultimo = s.Consultar(sql);
                    s.Passo(ultimo.Count + " linha(s) lida(s)");
                }
                return ultimo ?? new List<string[]>();
            }
        }

        // Uma conexão com o banco do Metre. Somente leitura, salvo no ajuste de chave (AjusteChave), que abre a
        // sua própria sessão de escrita e grava dentro de uma transação.
        public sealed class Sessao : IDisposable
        {
            IntPtr m;
            public readonly Action<string> Passo;

            Sessao(IntPtr m, Action<string> passo) { this.m = m; Passo = passo; }

            public IntPtr Ptr { get { return m; } }

            public static Sessao Abrir(Config cfg, Action<string> passo, bool somenteLeitura)
            {
                if (passo == null) passo = s => { };
                cfg.ResolverConexaoBanco();
                passo("procurando libmariadb.dll");
                CarregarDll(cfg);
                passo("DLL carregada: " + DllCarregada + " (processo " + (Environment.Is64BitProcess ? "64" : "32") + " bits)");
                IntPtr m = mysql_init(IntPtr.Zero);
                if (m == IntPtr.Zero) throw new Exception("mysql_init falhou");
                try
                {
                    // Sem timeout, um MariaDB que não responde deixa a tela parada em "conectando" para sempre.
                    uint conectar = 10, ler = (uint)Math.Max(60, cfg.TimeoutSegundos * 4);
                    mysql_options(m, MYSQL_OPT_CONNECT_TIMEOUT, ref conectar);
                    mysql_options(m, MYSQL_OPT_READ_TIMEOUT, ref ler);
                    mysql_options(m, MYSQL_OPT_WRITE_TIMEOUT, ref ler);
                    passo("conectando em " + cfg.DbHost + ":" + cfg.DbPorta + " como " + cfg.DbUsuario +
                        (cfg.OrigemConexaoBanco != null ? " (" + cfg.OrigemConexaoBanco + ")" : ""));
                    if (mysql_real_connect(m, Z(cfg.DbHost), Z(cfg.DbUsuario), Z(cfg.DbSenha), Z(cfg.DbBanco), (uint)cfg.DbPorta, IntPtr.Zero, 0) == IntPtr.Zero)
                        throw new Exception("conexão com o banco " + cfg.DbHost + ":" + cfg.DbPorta + " falhou: " + Erro(m));
                    passo(somenteLeitura ? "conectado; abrindo sessão somente leitura" : "conectado; sessão de escrita (ajuste de chave)");
                    mysql_set_character_set(m, Z("utf8mb4"));
                    var s = new Sessao(m, passo);
                    if (somenteLeitura) s.Comando("SET SESSION TRANSACTION READ ONLY");
                    return s;
                }
                catch
                {
                    mysql_close(m);
                    throw;
                }
            }

            public List<string[]> Consultar(string sql)
            {
                string[] nomes;
                return Executar(m, sql, true, out nomes).Select(v => v.Select(b => b == null ? null : Encoding.UTF8.GetString(b)).ToArray()).ToList();
            }

            // Colunas e valores brutos (SELECT *), para o backup da linha antes de alterar.
            public List<KeyValuePair<string, byte[]>> LinhaCompleta(string sql)
            {
                string[] nomes;
                var linhas = Executar(m, sql, true, out nomes);
                if (linhas.Count == 0) return null;
                if (linhas.Count > 1) throw new Exception("esperada 1 linha, vieram " + linhas.Count + ": " + sql);
                return nomes.Select((n, i) => new KeyValuePair<string, byte[]>(n, linhas[0][i])).ToList();
            }

            // INSERT/UPDATE/START TRANSACTION...; devolve as linhas afetadas.
            public long Comando(string sql)
            {
                string[] nomes;
                Executar(m, sql, false, out nomes);
                return (long)mysql_affected_rows(m);
            }

            public void Dispose()
            {
                if (m != IntPtr.Zero) mysql_close(m);
                m = IntPtr.Zero;
            }
        }

        static List<byte[][]> Executar(IntPtr m, string sql, bool comResultado, out string[] nomes)
        {
            nomes = new string[0];
            if (mysql_query(m, Z(sql)) != 0) throw new Exception("consulta falhou: " + Erro(m));
            IntPtr r = mysql_store_result(m);
            var linhas = new List<byte[][]>();
            if (r == IntPtr.Zero)
            {
                if (comResultado) throw new Exception("consulta sem resultado: " + Erro(m));
                return linhas;
            }
            try
            {
                int n = (int)mysql_num_fields(r);
                nomes = new string[n];
                // MYSQL_FIELD começa com 'char *name'.
                for (int i = 0; i < n; i++) nomes[i] = TextoC(Marshal.ReadIntPtr(mysql_fetch_field_direct(r, (uint)i)));
                IntPtr row;
                while ((row = mysql_fetch_row(r)) != IntPtr.Zero)
                {
                    // unsigned long[] (32 bits no Windows, também no processo de 64 bits).
                    IntPtr tams = mysql_fetch_lengths(r);
                    var v = new byte[n][];
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr p = Marshal.ReadIntPtr(row, i * IntPtr.Size);
                        if (p == IntPtr.Zero) continue;
                        v[i] = new byte[(uint)Marshal.ReadInt32(tams, i * 4)];
                        Marshal.Copy(p, v[i], 0, v[i].Length);
                    }
                    linhas.Add(v);
                }
            }
            finally { mysql_free_result(r); }
            return linhas;
        }

        static string TextoC(IntPtr p)
        {
            var b = new List<byte>();
            for (int i = 0; ; i++) { byte x = Marshal.ReadByte(p, i); if (x == 0) break; b.Add(x); }
            return Encoding.UTF8.GetString(b.ToArray());
        }

        static string SqlContingencia(Config cfg)
        {
            return "SELECT " + string.Join(", ", Colunas) + " FROM " + cfg.DbBanco + ".nota_saida WHERE situacao = '" +
                cfg.DbSituacao.Replace("\\", "\\\\").Replace("'", "''") + "' ORDER BY data_contigencia";
        }

        public static List<LinhaContingencia> LerBanco(Config cfg, Log log)
        {
            var linhas = Consultar(cfg, new[] { SqlContingencia(cfg) }, s => log.Info("Banco: " + s));
            var lista = linhas.Select(v => Montar(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9])).ToList();
            log.Tela("Contingência lida do banco " + cfg.DbBanco + " (" + cfg.DbHost + ":" + cfg.DbPorta + "): " + lista.Count + " nota(s)");
            return lista;
        }

        // XMLs que o Metre guardou no banco para esta linha de nota_saida (chefxml.xml, só leitura): pelo id_xml da
        // coluna XML (chave primária) e, se houver índice, pelo id_nota_saida. A tabela tem GBs: consulta sem índice
        // (ex.: com OR e subconsulta) varre tudo e deixa o processamento parado por minutos em cada nota.
        public static List<KeyValuePair<string, byte[]>> XmlsDaNota(Config cfg, string idNotaSaida, Log log)
        {
            long id = long.Parse(idNotaSaida);
            var lista = new List<KeyValuePair<string, byte[]>>();
            using (var s = Sessao.Abrir(cfg, x => log.Info("Banco: " + x), true))
            {
                var ids = new List<string>();
                var xml = s.Consultar("SELECT XML FROM " + cfg.DbBanco + ".nota_saida WHERE id_nota_saida = " + id);
                long idXml;
                if (xml.Count == 1 && long.TryParse(xml[0][0], out idXml)) ids.Add(idXml.ToString());
                if (indiceXmlPorNota == null)
                    indiceXmlPorNota = s.Consultar("SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '" + cfg.DbBancoXml +
                        "' AND TABLE_NAME = 'xml' AND COLUMN_NAME = 'id_nota_saida' AND SEQ_IN_INDEX = 1")[0][0] != "0";
                string onde = ids.Count > 0 ? "x.id_xml = " + ids[0] : null;
                if (indiceXmlPorNota == true) onde = (onde != null ? onde + " UNION SELECT x.id_xml, x.XML FROM " + cfg.DbBancoXml + ".xml x WHERE " : "") + "x.id_nota_saida = " + id;
                if (onde == null) return lista;
                string[] nomes;
                foreach (var v in Executar(s.Ptr, "SELECT x.id_xml, x.XML FROM " + cfg.DbBancoXml + ".xml x WHERE " + onde, true, out nomes))
                    if (v[1] != null)
                        lista.Add(new KeyValuePair<string, byte[]>(cfg.DbBancoXml + ".xml id_xml " + Encoding.UTF8.GetString(v[0]), v[1]));
            }
            return lista;
        }

        static bool? indiceXmlPorNota;

        // Linhas de nota_saida que já têm esta chave (a chave autorizada não pode ir para uma segunda linha).
        public static List<string> IdsComChave(Sessao s, Config cfg, string chave)
        {
            return s.Consultar("SELECT id_nota_saida FROM " + cfg.DbBanco + ".nota_saida WHERE chave_acesso = '" + AjusteChave.SoDigitos(chave, 44) + "'")
                .Select(v => v[0]).ToList();
        }

        // --testar-banco: mostra cada passo e um resumo por situação (sem dados de clientes).
        public static void Testar(Config cfg, Action<string> mostrar)
        {
            var resumo = Consultar(cfg, new[] { "SELECT situacao, COUNT(*) FROM " + cfg.DbBanco + ".nota_saida GROUP BY situacao ORDER BY 2 DESC" },
                s => mostrar("  > " + s));
            mostrar("Notas por situação em " + cfg.DbBanco + ".nota_saida:");
            foreach (var v in resumo) mostrar("  " + (v[0] ?? "(vazio)") + ": " + v[1]);
            // O "Ajustar chave no banco" precisa de UPDATE em nota_saida (o hash da senha não é mostrado).
            var grants = Consultar(cfg, new[] { "SHOW GRANTS FOR CURRENT_USER()" }, null).Select(v => v[0] ?? "").ToList();
            mostrar("Permissões de " + cfg.DbUsuario + ":");
            foreach (var g in grants) mostrar("  " + System.Text.RegularExpressions.Regex.Replace(g, @"IDENTIFIED BY PASSWORD '[^']*'", "IDENTIFIED BY PASSWORD '***'"));
            bool update = grants.Any(g =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(g, @"^GRANT (.+?) ON (\S+) TO ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) return false;
                string priv = m.Groups[1].Value.ToUpperInvariant(), alvo = m.Groups[2].Value.Replace("`", "").ToLowerInvariant();
                bool temUpdate = priv.Contains("ALL PRIVILEGES") || System.Text.RegularExpressions.Regex.IsMatch(priv, @"(^|, )UPDATE($|,| \()");
                string b = cfg.DbBanco.ToLowerInvariant();
                return temUpdate && (alvo == "*.*" || alvo == b + ".*" || alvo == b + ".nota_saida");
            });
            mostrar(update ? "Ajuste de chave: OK, o usuário tem UPDATE em " + cfg.DbBanco + ".nota_saida"
                           : "Ajuste de chave: ATENÇÃO, não achei UPDATE em " + cfg.DbBanco + ".nota_saida nas permissões acima (o ajuste vai falhar sem alterar nada)");
            var lista = Consultar(cfg, new[] { SqlContingencia(cfg) }, s => mostrar("  > " + s))
                .Select(v => Montar(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9])).ToList();
            mostrar("Contingência ('" + cfg.DbSituacao + "'): " + lista.Count + " nota(s)");
            foreach (var c in lista.Take(5))
                mostrar("  " + c.Serie + "/" + c.Numero + "  pedido " + (c.Pedido ?? "-") + "  R$ " + Analisador.Moeda(c.Valor) +
                    "  " + (c.DtContingencia.HasValue ? c.DtContingencia.Value.ToString("dd/MM/yyyy HH:mm") : "-"));
            if (lista.Count > 5) mostrar("  ... e mais " + (lista.Count - 5));
        }
    }
}
