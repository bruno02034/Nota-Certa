using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuplicidadeNfce
{
    // Caracterização das funções puras (CSV, chaves, acentos, INIs, XML, históricos): imprime o resultado de entradas
    // fixas. testar_unidades.ps1 compara com testes\unidades.esperado.txt, gerado pela versão anterior à refatoração.
    static class TesteUnidades
    {
        static readonly StringBuilder saida = new StringBuilder();
        static int falhas;

        static void P(string nome, object valor) { saida.AppendLine(nome + " => " + (valor == null ? "(null)" : valor.ToString().Replace("\r", "\\r").Replace("\n", "\\n"))); }
        static void Verdade(bool c, string m) { Console.WriteLine((c ? "OK    " : "FALHA ") + m); if (!c) falhas++; }
        static string L(IEnumerable<string> l) { return "[" + string.Join("|", l.Select(x => x ?? "(null)")) + "]"; }

        // A conferência do DV das Notas Faltantes antes da refatoração (EntradaNumeros.ChaveValida), para comparar.
        static bool DvReferencia(string k)
        {
            if (k == null || !System.Text.RegularExpressions.Regex.IsMatch(k, @"^\d{44}$")) return false;
            int soma = 0, peso = 2;
            for (int i = 42; i >= 0; i--) { soma += (k[i] - '0') * peso; peso = peso == 9 ? 2 : peso + 1; }
            int dv = 11 - soma % 11;
            if (dv >= 10) dv = 0;
            return k[43] - '0' == dv;
        }

        static int Main(string[] args)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "unidades_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try { Rodar(tmp); }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            File.WriteAllText(args[0], saida.ToString(), new UTF8Encoding(false));
            return falhas == 0 ? 0 : 1;
        }

        static void Rodar(string tmp)
        {
            // CSV
            foreach (var s in new[] { null, "", "abc", "=1+1", "+5", "-3,50", "-x", "@a", "\tx", "a;b", "a\"b", "linha\nnova", "=\"123\"", "=\"12a\"", "'=x" })
            {
                string c = Csv.Campo(s);
                P("Campo(" + (s ?? "null") + ")", c);
                P("Dividir(Campo)", L(Csv.Dividir(c)));
            }
            foreach (var s in new[] { null, "", "123", "12a", "31261012345678000199650010000001231000001234" })
            {
                P("Formula(" + (s ?? "null") + ")", Csv.Formula(s));
                P("SemFormula(Formula)", Csv.SemFormula(Csv.Formula(s)));
            }
            P("Dividir(x)", L(Csv.Dividir("a;\"b;c\";\"d\"\"e\";;'=f;'g")));

            // Chave: DV
            var rnd = new Random(7);
            int iguais = 0, validas = 0;
            for (int i = 0; i < 3000; i++)
            {
                var sb = new StringBuilder();
                for (int j = 0; j < 44; j++) sb.Append((char)('0' + rnd.Next(10)));
                string k = sb.ToString();
                bool a = AjusteChave.DvValido(k), b = DvReferencia(k);
                if (a == b) iguais++;
                if (a) validas++;
            }
            P("DV: iguais de 3000", iguais);
            P("DV: válidas de 3000", validas);
            foreach (var k in new[] { null, "", "123", "3126101234567800019965001000000123100000123X", new string('0', 44) })
                P("DvValido(" + (k ?? "null") + ")", AjusteChave.DvValido(k) + "/" + DvReferencia(k));
            P("MesmaNumeracao", AjusteChave.MesmaNumeracao(new string('1', 44), new string('1', 34) + new string('2', 10)));

            // Acentos e situações
            foreach (var s in new[] { null, "", " Em Contigência ", "EM CONTINGÊNCIA 2", "Em contigencia2", "Contingencia 2", "Autorizada", "Ç Ã é" })
            {
                P("SemAcento(" + (s ?? "null") + ")", AjusteChave.SemAcento(s));
                P("EhContingencia2", Banco.EhContingencia2(s));
                P("EmContingencia", MacroMetre.EmContingencia(s));
            }
            foreach (var s in new[] { "CLIENTE CONTIGÊNCIA", "Cliente  Contingência", "CLIENTE CONTINGENCIA", "CLIENTE EMISSÃO", null })
                P("EhColunaContingencia(" + (s ?? "null") + ")", MacroMetre.EhColunaContingencia(s));

            // Números, datas, moeda
            foreach (var s in new[] { null, "", "48.00", "48,00", "1,234.5", "abc", "-1.5" })
                P("NotaXml.Decimal(" + (s ?? "null") + ")", NotaXml.Decimal(s));
            foreach (var s in new[] { null, "", "48.00", "48,00", "x" })
                P("ValorDoRegistro(" + (s ?? "null") + ")", AjusteChave.ValorDoRegistro(new RegistroHistorico { Tamanho = s }));
            foreach (var s in new[] { null, "", "2026-10-19T17:05:40-03:00", "2026-10-19T23:59:00+00:00", "lixo" })
            {
                P("DataEmissao(" + (s ?? "null") + ")", MotorFaltantes.DataEmissao(s));
                P("DataCurta", LinhaGrid.DataCurta(s));
            }
            P("Moeda", Analisador.Moeda(1234.5m) + " " + Analisador.Moeda(null));
            P("FormatarCnpj", MotorFaltantes.FormatarCnpj("12345678000199") + " " + MotorFaltantes.FormatarCnpj("1") + " " + MotorFaltantes.FormatarCnpj(null));

            // Entrada de números
            var e = EntradaNumeros.Ler("1201, 1202;1203 1205 - 1207\n1202 x 9-3 31261012345678000199650010000001231000001234", 5000);
            P("Entrada.Numeros", string.Join(",", e.Numeros));
            P("Entrada.Avisos", e.Avisos());

            // INI: Config, GravarOpcoes, SenhaBanco.GravarNoIni
            string ini = Path.Combine(tmp, "d.ini");
            File.WriteAllText(ini, "# comentário\n[secao]\nPastaRaiz=C:\\X\\NFCe\nDbSenha=segredo\nDataInicial=19/10/2026\n", new UTF8Encoding(false));
            var cfg = Config.Carregar(ini);
            P("Config", cfg.PastaRaiz + " " + cfg.PastaChef + " " + cfg.DataInicial.Value.ToString("dd/MM/yyyy") + " texto=" + cfg.SenhaEmTextoNoIni);
            Config.GravarOpcoes(ini, new Dictionary<string, string> { { "pastaraiz", "D:\\Y\\NFCe" }, { "Nova", "1" } });
            P("GravarOpcoes", File.ReadAllText(ini));
            SenhaBanco.GravarNoIni(ini, "nova senha");
            string conteudo = File.ReadAllText(ini);
            P("GravarNoIni", string.Join("\n", conteudo.Split('\n').Select(l => l.StartsWith(SenhaBanco.Chave + "=") ? SenhaBanco.Chave + "=<protegida>" : l)));
            string prot = conteudo.Split('\n').First(l => l.StartsWith(SenhaBanco.Chave + "=")).Substring(SenhaBanco.Chave.Length + 1).Trim();
            Verdade(SenhaBanco.Abrir(prot) == "nova senha", "senha do banco protegida abre de volta");
            Verdade(SenhaBanco.AbrirCert(SenhaBanco.ProtegerCert("cert")) == "cert", "senha do certificado protegida abre de volta");
            try { SenhaBanco.Abrir(SenhaBanco.ProtegerCert("x")); Verdade(false, "entropias diferentes"); }
            catch (Exception ex) { Verdade(ex.Message.StartsWith("a senha do banco guardada no ini não abre"), "entropias diferentes (banco x certificado)"); }
            foreach (var o in new[] { "DbBanco=chef;", "DbPorta=x", "Inexistente=1", "DataFinal=32/13/2026" })
            {
                File.WriteAllText(ini, o + "\n", new UTF8Encoding(false));
                try { Config.Carregar(ini); P("Config(" + o + ")", "ok"); }
                catch (Exception ex) { P("Config(" + o + ")", ex.Message.Replace(ini, "<ini>")); }
            }

            // Conexao.ini e INIs do Metre
            string conexao = Path.Combine(tmp, "Conexao.ini");
            File.WriteAllText(conexao, "[Outra]\nip=1.1.1.1\n[ Configuracao ]\nip = 10.0.0.5\nporta=3308\n", Encoding.Default);
            var c2 = new Config { IniConexaoMetre = conexao };
            c2.ResolverConexaoBanco();
            P("ResolverConexaoBanco", c2.DbHost + ":" + c2.DbPorta + " " + (c2.OrigemConexaoBanco ?? "").Replace(tmp, "<tmp>"));
            var c3 = new Config { IniConexaoMetre = Path.Combine(tmp, "nao.ini") };
            c3.ResolverConexaoBanco();
            P("ResolverConexaoBanco(sem ini)", c3.DbHost + ":" + c3.DbPorta + " " + (c3.OrigemConexaoBanco ?? "(null)"));
            string nfceIni = Path.Combine(tmp, "NFCe.ini");
            File.WriteAllText(nfceIni, "[Geral]\nSenha=s1\nCertificado=cert.pfx\n[Certificado]\nSenhaCertificado=\"s2\"\nPWD=\nPass=s3\n", Encoding.Default);
            var c4 = new Config { IniMetre = nfceIni + ";" + Path.Combine(tmp, "x.ini"), CertificadoSenha = "s0", CertificadoSenhaSalva = "s3" };
            MetreIni.Carregar(c4);
            P("MetreIni.Senhas", L(MetreIni.Senhas.Select(s => s.Origem.Replace(tmp, "<tmp>") + "=" + s.Valor)));
            P("MetreIni.Pfxs", L(MetreIni.Pfxs.Select(s => s.Replace(tmp, "<tmp>"))));
            P("MetreIni.Lidos", L(MetreIni.Lidos.Select(s => s.Replace(tmp, "<tmp>"))));

            // XML da nota (NotaXml) com protocolo e sobra
            string chave = "31261012345678000199650010000001231000001234";
            string nfe = "<NFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"><infNFe Id=\"NFe" + chave + "\" versao=\"4.00\"><ide><serie>1</serie><nNF>123</nNF>" +
                "<dhEmi>2026-10-19T17:05:40-03:00</dhEmi><tpEmis>9</tpEmis></ide><emit><xNome>EMPRESA</xNome></emit>" +
                "<det nItem=\"1\"><prod><cProd>376</cProd><xProd>PROD</xProd><NCM>1</NCM><CFOP>5102</CFOP><uCom>UN</uCom><qCom>3.0000</qCom><vUnCom>13.90</vUnCom><vProd>41.70</vProd><vDesc>63.43</vDesc><xPed>555</xPed></prod></det>" +
                "<total><ICMSTot><vProd>100.00</vProd><vDesc>70.00</vDesc><vNF>30.00</vNF></ICMSTot></total></infNFe>" +
                "<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><SignedInfo><Reference><DigestValue>ABC=</DigestValue></Reference></SignedInfo></Signature></NFe>";
            string prot1 = "<protNFe versao=\"4.00\"><infProt><chNFe>" + chave + "</chNFe><dhRecbto>2026-10-19T17:06:00-03:00</dhRecbto><nProt>1</nProt>" +
                "<digVal>ABC=</digVal><cStat>483</cStat><xMotivo>Rejeição: desconto [nItem: 1]</xMotivo></infProt></protNFe>";
            string xml = "\uFEFF<nfeProc xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"4.00\">" + nfe + prot1 + "</nfeProc>" +
                "o><infProt><chNFe>X</chNFe><cStat>539</cStat><xMotivo>dup [chNFe:" + chave + "] [nRec:9]</xMotivo></infProt></nfeProc>";
            var n = NotaXml.LerConteudo("banco", Encoding.UTF8.GetBytes(xml));
            P("Nota", string.Join(" ", new object[] { n.Erro, n.Chave, n.Numero, n.Serie, n.DhEmi, n.TpEmis, n.Pedido, n.NomeEmitente, n.VNF, n.DigestDeclarado, n.DigestCalculado, n.Cnpj, n.TemSobra, n.Sha256 }
                .Select(o => o == null ? "(null)" : o.ToString())));
            P("Nota.Prot", n.Prot == null ? null : string.Join(" ", n.Prot.CStat, n.Prot.XMotivo, n.Prot.ChNFe, n.Prot.NProt, n.Prot.DigVal, n.Prot.DhRecbto));
            P("Nota.ItemRejeitado", n.ItemRejeitado);
            P("Nota.Sobra", L(n.ProtsNaSobra.Select(p => p.ChNFe + "/" + p.CStat + "/" + p.XMotivo)));
            P("Nota(lixo)", NotaXml.LerConteudo("x", new byte[] { 1, 2 }).Erro);
            P("Nota(DTD)", NotaXml.LerConteudo("x", Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY e \"x\">]><a>&e;</a>")).Erro != null);
            string arq = Path.Combine(tmp, "NOTA_123_REJEICAO.xml");
            File.WriteAllText(arq, "<NFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"/>");
            var na = NotaXml.Ler(arq);
            P("Nota(arquivo)", na.Nome + " " + na.Tipo + " " + na.Erro + " " + na.Tamanho);
            foreach (var nome in new[] { "NOTA_1_REJEICAO", "NOTA_1_FALHA_SCHEMA", "NOTA_CONTIGENCIA_ENVIA_1", "NOTA_1_Temp_ANTES", "NOTA_1_ASSINADA", "NOTA_1_NFCE", "outro" })
                P("Classificar(" + nome + ")", NotaXml.Classificar(nome));

            // Históricos (duplicidade e faltantes)
            var h = new Historico(Path.Combine(tmp, "dup"));
            h.Registrar(new RegistroHistorico { DataHora = "01/01/2026 10:00:00", Serie = "1", Numero = "2", ChaveAutorizada = chave, Arquivo = "=a;b", CaminhoOrigem = "o", CaminhoDestino = "d", Tamanho = "10", Sha256 = "h", Status = Status.Copiado });
            h.Registrar(new RegistroHistorico { DataHora = "01/01/2026 10:01:00", Serie = "1", Numero = "2", ChaveAutorizada = chave, Arquivo = "x", Status = Status.ChaveAjustada });
            P("historico.csv", File.ReadAllText(h.Caminho));
            var h2 = new Historico(Path.Combine(tmp, "dup"));
            P("Historico relido", h2.Todos.Count + " " + (h2.BuscarCopiado(chave) != null ? h2.BuscarCopiado(chave).Arquivo : null) + " " + (h2.BuscarAjuste(chave) != null));
            var hf = new HistoricoFaltantes(Path.Combine(tmp, "falt"));
            hf.Registrar(new HistoricoFaltantes.Registro { DataHora = "d", Cnpj = "12345678000199", Serie = "2", Numero = "5", Chave = chave, Arquivo = "a", CaminhoOrigem = "o", CaminhoDestino = "c", Tamanho = "1", Sha256 = "s", Status = CopiaFaltante.Copiado });
            P("faltantes historico.csv", File.ReadAllText(hf.Caminho));
            var hf2 = new HistoricoFaltantes(Path.Combine(tmp, "falt"));
            P("HistoricoFaltantes relido", hf2.Todos.Count + " " + (hf2.BuscarCopiado("12345678000199", 2, 5) != null));

            // Relatório CSV
            string rel = Path.Combine(tmp, "rel.csv");
            Relatorio.GravarCsv(rel, new List<LinhaRelatorio> { new LinhaRelatorio { Numero = "2", Serie = "1", DhEmiSistema = "2026-10-19T17:05:40-03:00", ValorSistema = 1.5m, ChaveSistema = chave, Status = Status.Pendente, Motivo = "-x" } });
            P("relatorio.csv", File.ReadAllText(rel));
            P("Dentro", Motor.Dentro(@"C:\a\b\c.xml", @"C:\A\b\") + " " + Motor.Dentro(@"C:\ab", @"C:\a"));
        }
    }
}
