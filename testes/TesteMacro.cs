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
                Verdade(MacroMetre.Normalizar("CLIENTE\nCONTIGÊNCIA") == MacroMetre.ColunaContingencia, "cabeçalho da coluna em duas linhas casa com a da aba CONTIGÊNCIA");
            }
            finally { if (!p.HasExited) p.Kill(); }
            Console.WriteLine(falhas == 0 ? "TODOS OS TESTES PASSARAM" : falhas + " FALHA(S)");
            return falhas;
        }
    }
}
