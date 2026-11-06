using System;
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

        public static string Proteger(string senha) { return Proteger(senha, Entropia); }

        public static string Abrir(string protegida)
        {
            try
            {
                return Abrir(protegida, Entropia);
            }
            catch (Exception ex)
            {
                throw new Exception("a senha do banco guardada no ini não abre neste computador (" + ex.Message.Trim() + "); digite-a de novo", ex);
            }
        }

        // Senha do certificado digitada em Configurações (quando a dos INIs do Metre não abre o .pfx): mesmo esquema,
        // outra entropia. Só complementa: as senhas dos INIs do Metre continuam sendo tentadas primeiro.
        public const string ChaveCert = "CertificadoSenhaProtegida";
        static readonly byte[] EntropiaCert = Encoding.UTF8.GetBytes("NotaCerta/CertSenha/v1");

        public static string ProtegerCert(string senha) { return Proteger(senha, EntropiaCert); }

        public static string AbrirCert(string protegida) { return Abrir(protegida, EntropiaCert); }

        static string Proteger(string senha, byte[] entropia)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(senha), entropia, DataProtectionScope.LocalMachine));
        }

        static string Abrir(string protegida, byte[] entropia)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protegida), entropia, DataProtectionScope.LocalMachine));
        }

        // Grava DbSenhaProtegida no ini e esvazia o DbSenha (texto simples). As demais linhas ficam como estão.
        public static void GravarNoIni(string arqIni, string senha)
        {
            var linhas = Config.LinhasDoIni(arqIni);
            string nova = Chave + "=" + Proteger(senha);
            int posSenha = -1, posProtegida = -1;
            for (int i = 0; i < linhas.Count; i++)
            {
                string k = (Config.ChaveDaLinha(linhas[i]) ?? "").ToLowerInvariant();
                if (k == "dbsenha") { linhas[i] = "DbSenha="; posSenha = i; }
                else if (k == Chave.ToLowerInvariant()) posProtegida = i;
            }
            if (posProtegida >= 0) linhas[posProtegida] = nova;
            else if (posSenha >= 0) linhas.Insert(posSenha + 1, nova);
            else linhas.Add(nova);
            Config.GravarIni(arqIni, linhas);
        }
    }
}
