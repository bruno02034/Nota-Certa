using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DuplicidadeNfce
{
    // Senha do banco do Metre guardada no duplicidade.ini criptografada pelo Windows (DPAPI, escopo da máquina):
    // ilegível para quem abre o ini e só abre neste computador. Digitada na tela de senha da interface.
    static class SenhaBanco
    {
        public const string Chave = "DbSenhaProtegida";
        static readonly byte[] Entropia = Encoding.UTF8.GetBytes("NotaCerta/DbSenha/v1");

        public static string Proteger(string senha)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(senha), Entropia, DataProtectionScope.LocalMachine));
        }

        public static string Abrir(string protegida)
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protegida), Entropia, DataProtectionScope.LocalMachine));
            }
            catch (Exception ex)
            {
                throw new Exception("a senha do banco guardada no ini não abre neste computador (" + ex.Message.Trim() + "); digite-a de novo", ex);
            }
        }

        // Grava DbSenhaProtegida no ini e esvazia o DbSenha (texto simples). As demais linhas ficam como estão.
        public static void GravarNoIni(string arqIni, string senha)
        {
            var linhas = File.Exists(arqIni) ? new List<string>(File.ReadAllLines(arqIni, Encoding.UTF8)) : new List<string>();
            string nova = Chave + "=" + Proteger(senha);
            int posSenha = -1, posProtegida = -1;
            for (int i = 0; i < linhas.Count; i++)
            {
                string k = NomeDaChave(linhas[i]);
                if (k == "dbsenha") { linhas[i] = "DbSenha="; posSenha = i; }
                else if (k == Chave.ToLowerInvariant()) posProtegida = i;
            }
            if (posProtegida >= 0) linhas[posProtegida] = nova;
            else if (posSenha >= 0) linhas.Insert(posSenha + 1, nova);
            else linhas.Add(nova);
            string tmp = arqIni + ".tmp";
            File.WriteAllLines(tmp, linhas, new UTF8Encoding(false));
            if (File.Exists(arqIni)) File.Replace(tmp, arqIni, null);
            else File.Move(tmp, arqIni);
        }

        static string NomeDaChave(string linha)
        {
            string l = linha.Trim();
            int eq = l.IndexOf('=');
            if (l == "" || l.StartsWith("#") || l.StartsWith(";") || eq <= 0) return null;
            return l.Substring(0, eq).Trim().ToLowerInvariant();
        }
    }
}
