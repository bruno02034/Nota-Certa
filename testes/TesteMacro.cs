using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

namespace DuplicidadeNfce
{
    // Macro do Metre contra uma janela que imita a "Rejeição de Nota" (janela_rejeicao.ps1). O duplo clique na aba
    // CONTIGÊNCIA não é testado aqui: precisa do Metre aberto.
    static class TesteMacro
    {
        static int falhas;
        static void Verdade(bool c, string m) { Console.WriteLine((c ? "OK    " : "FALHA ") + m); if (!c) falhas++; }

        // Pergunta "pedido cancelado" do Metre: o macro acha só o "Não" (nunca o "Sim") e só nessa pergunta.
        static void Cancelado(string script, string texto, bool deveAchar)
        {
            var p = Process.Start(new ProcessStartInfo("powershell.exe", "-STA -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"" +
                (texto != null ? " -Texto \"" + texto + "\"" : "")) { UseShellExecute = false, CreateNoWindow = true });
            try
            {
                var lim = DateTime.Now.AddSeconds(30);
                bool aberta = false;
                while (!aberta && DateTime.Now < lim)
                {
                    Thread.Sleep(300);
                    p.Refresh(); aberta = p.MainWindowHandle != IntPtr.Zero;
                }
                Thread.Sleep(500);
                var nao = MacroMetre.BotoesNaoCancelado(p.Id);
                Verdade(MacroMetre.BotoesConfirmar(p.Id).Count == 0, "pergunta com Sim/Não não tem \"Entendi\"/\"Ok\"" + (deveAchar ? "" : " (outra pergunta)"));
                if (!deveAchar) { Verdade(nao.Count == 0, "\"Não\" de outra pergunta fica de fora: " + nao.Count); return; }
                Verdade(nao.Count == 1 && nao[0].Current.Name == "Não", "acha só o \"Não\" do pedido cancelado: " + nao.Count + " botão(ões)");
                if (nao.Count != 1) return;
                Verdade(MacroMetre.TextoDaJanela(nao[0]).Contains("pedido foi cancelado"), "texto da pergunta vai para o log");
                ((InvokePattern)nao[0].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                p.WaitForExit(10000);
                Verdade(p.HasExited && p.ExitCode == 0, "responde \"Não\" (não \"Sim\") e a janela fecha");
            }
            finally { if (!p.HasExited) p.Kill(); }
        }

        [STAThread]
        static int Main(string[] a)
        {
            var p = Process.Start(new ProcessStartInfo("powershell.exe", "-STA -NoProfile -ExecutionPolicy Bypass -File \"" + a[0] + "\"")
                { UseShellExecute = false, CreateNoWindow = true });
            try
            {
                var lim = DateTime.Now.AddSeconds(30);
                var b = MacroMetre.BotoesConfirmar(p.Id);
                while (b.Count == 0 && DateTime.Now < lim) { Thread.Sleep(300); b = MacroMetre.BotoesConfirmar(p.Id); }
                Verdade(b.Count == 1 && b[0].Current.Name == "Entendi !", "acha só o \"Entendi !\" (o \"X\" fica de fora): " + b.Count + " botão(ões)");
                if (b.Count == 1)
                {
                    string t = MacroMetre.TextoDaJanela(b[0]);
                    Verdade(t.Contains("539") && t.Contains("chNFe:31261011222333000181650060000888709880705883"), "texto da janela vai para o log: " + t);
                    // Clique com a posição dentro do evento (o do duplo clique na nota), só se o botão é o que está no ponto.
                    var r = b[0].Current.BoundingRectangle;
                    int x = (int)(r.X + r.Width / 2), y = (int)(r.Y + r.Height / 2);
                    var noPonto = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                    bool ehOBotao = noPonto != null && (Automation.Compare(noPonto, b[0]) || Automation.Compare(TreeWalker.RawViewWalker.GetParent(noPonto), b[0]));
                    Verdade(ehOBotao, "o \"Entendi !\" é o que está na tela no ponto do clique");
                    if (ehOBotao) MacroMetre.Clique(x, y); else ((InvokePattern)b[0].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    lim = DateTime.Now.AddSeconds(10);
                    while (!p.HasExited && DateTime.Now < lim) Thread.Sleep(200);
                    Verdade(p.HasExited, "clique com posição absoluta no \"Entendi !\" fecha a janela");
                }
                Verdade(MacroMetre.EmContingencia("Em Contigência") && MacroMetre.EmContingencia("Em Contingencia 2") && !MacroMetre.EmContingencia("Autorizada"),
                    "status de contingência com e sem acento");
                Verdade(MacroMetre.EhColunaContingencia("CLIENTE\nCONTIGÊNCIA") && MacroMetre.EhColunaContingencia("Cliente Contingencia") &&
                    !MacroMetre.EhColunaContingencia("CLIENTE EMISSÃO"), "cabeçalho da coluna da aba CONTIGÊNCIA em duas linhas e em outras grafias");
            }
            finally { if (!p.HasExited) p.Kill(); }
            string canc = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(a[0]), "janela_cancelado.ps1");
            Cancelado(canc, null, true);
            Cancelado(canc, "Deseja sair do sistema?", false);
            Console.WriteLine(falhas == 0 ? "TODOS OS TESTES PASSARAM" : falhas + " FALHA(S)");
            return falhas;
        }
    }
}
