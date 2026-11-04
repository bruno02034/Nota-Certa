using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Point = System.Windows.Point;
using Condition = System.Windows.Automation.Condition;

namespace DuplicidadeNfce
{
    // Macro da aba CONTIGÊNCIA da janela de NFC-e do Metre: duplo clique em cada nota (o Metre transmite e, na 539,
    // grava o REJEICAO) e Ok na janela de erro. O Metre é JavaFX e expõe a tela pela UI Automation do Windows; as abas
    // não aparecem, mas a tabela da Contingência é a única com a coluna "CLIENTE CONTIGÊNCIA" (a da Normal tem
    // "CLIENTE EMISSÃO"). Antes de cada clique tudo é conferido de novo; qualquer divergência para o macro.
    class MacroMetre
    {
        public const string ColunaContingencia = "CLIENTE CONTIGÊNCIA";
        public int SegundosJanela = 90;      // espera da janela com Ok depois do duplo clique (a transmissão demora)
        public Func<Process> AcharProcesso = AcharMetre;

        readonly Log log;
        readonly Action<string> andamento;
        POINT cursorNosso;

        public MacroMetre(Log log, Action<string> andamento) { this.log = log; this.andamento = andamento; }

        public class LinhaMetre
        {
            public string Serie, Numero, Pedido, Cliente, Status;
            public AutomationElement Elemento, Celula;
            public string Chave { get { return Serie + "/" + Numero + " pedido " + Pedido + " " + Cliente; } }
            public override string ToString() { return Serie + "/" + Numero + " (pedido " + Pedido + ")"; }
        }

        public class Resultado
        {
            public List<string> Feitas = new List<string>();
            public string Parada;           // null = terminou a lista
        }

        public static Process AcharMetre()
        {
            return Process.GetProcessesByName("javaw").Concat(Process.GetProcessesByName("java"))
                .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.StartsWith("Metre", StringComparison.OrdinalIgnoreCase));
        }

        // Tabela da aba Contingência que está de fato na tela (o que está desenhado no centro dela é a própria tabela);
        // null se a janela de NFC-e não estiver aberta nessa aba.
        public static AutomationElement TabelaContingencia(Process p)
        {
            var raiz = AutomationElement.FromHandle(p.MainWindowHandle);
            if (ProcessoDe(GetForegroundWindow()) != p.Id) { Frente(p.MainWindowHandle); Thread.Sleep(400); }
            var tabelas = raiz.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Table));
            foreach (AutomationElement t in tabelas)
            {
                bool contingencia = false;
                foreach (AutomationElement c in t.FindAll(TreeScope.Children, Condition.TrueCondition))
                    if (Normalizar(c.Current.Name) == ColunaContingencia) { contingencia = true; break; }
                if (!contingencia) continue;
                var r = t.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 50 || r.Height < 50) continue;
                var janela = JanelaDe(t);
                if (janela != IntPtr.Zero && GetForegroundWindow() != janela) { Frente(janela); Thread.Sleep(400); }
                if (Dentro(new Point(r.X + r.Width / 2, r.Y + r.Height / 2), t)) return t;
            }
            return null;
        }

        public static List<LinhaMetre> Linhas(AutomationElement tabela)
        {
            var linhas = new List<LinhaMetre>();
            foreach (AutomationElement row in tabela.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom)))
            {
                var cel = row.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToList();
                if (cel.Count < 5) continue;
                var sn = Normalizar(cel[1].Current.Name).Split(' ');
                if (sn.Length != 2) continue;   // cabeçalho ou linha vazia
                linhas.Add(new LinhaMetre
                {
                    Serie = sn[0], Numero = sn[1], Pedido = Normalizar(cel[2].Current.Name),
                    Cliente = Normalizar(string.Join(" ", cel[3].FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                        .Select(e => e.Current.Name).Where(s => !string.IsNullOrEmpty(s)).Distinct())),
                    Status = Normalizar(cel[4].Current.Name), Elemento = row, Celula = cel[2]
                });
            }
            return linhas;
        }

        // O Metre grava "Em Contigência" (sem o n); aceita também a grafia certa.
        public static bool EmContingencia(string status) { return Regex.IsMatch(SemAcento(status), @"\bem conti?n?gencia", RegexOptions.IgnoreCase); }

        // Lista o que o macro vai fazer, sem clicar; null + motivo se não der para começar.
        public List<LinhaMetre> Previa(out string motivo)
        {
            motivo = null;
            var p = AcharProcesso();
            if (p == null) { motivo = "o Metre não está aberto nesta máquina"; return null; }
            var t = TabelaContingencia(p);
            if (t == null) { motivo = "abra no Metre a janela de NFC-e na aba CONTIGÊNCIA (não achei a tabela dessa aba na tela)"; return null; }
            return Linhas(t).Where(l => EmContingencia(l.Status)).ToList();
        }

        public Resultado Executar()
        {
            var res = new Resultado();
            var feitas = new HashSet<string>();
            log.Info("Macro da contingência do Metre - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
            try
            {
                while (true)
                {
                    string motivo;
                    var p = AcharProcesso();
                    if (p == null) { res.Parada = "o Metre foi fechado"; break; }
                    var tabela = TabelaContingencia(p);
                    if (tabela == null) { res.Parada = "a aba CONTIGÊNCIA do Metre não está mais na tela"; break; }
                    var prox = Linhas(tabela).FirstOrDefault(l => EmContingencia(l.Status) && !feitas.Contains(l.Chave));
                    if (prox == null)
                    {
                        if (Rolar(tabela)) continue;
                        break;
                    }
                    feitas.Add(prox.Chave);
                    andamento("Metre: nota " + prox + " (" + (res.Feitas.Count + 1) + ")...");
                    string msg = UmaNota(p, tabela, prox, out motivo);
                    if (motivo != null) { res.Parada = "nota " + prox + ": " + motivo; break; }
                    res.Feitas.Add(prox + ": " + msg);
                    log.Info("  " + prox + ": " + msg);
                    Thread.Sleep(800);
                }
            }
            catch (Exception ex) { res.Parada = "erro: " + ex.Message; }
            log.Info(res.Feitas.Count + " nota(s) passada(s)" + (res.Parada != null ? "; parou: " + res.Parada : "; fim da lista"));
            return res;
        }

        // Um duplo clique e o Ok. Devolve o texto da janela; motivo != null = parar sem continuar.
        string UmaNota(Process p, AutomationElement tabela, LinhaMetre l, out string motivo)
        {
            motivo = null;
            if (!EmContingencia(l.Status)) { motivo = "status " + l.Status + ", não é contingência"; return null; }
            try { var si = l.Elemento.GetCurrentPattern(ScrollItemPattern.Pattern) as ScrollItemPattern; if (si != null) si.ScrollIntoView(); }
            catch (InvalidOperationException) { }
            var r = l.Celula.Current.BoundingRectangle;
            var tr = tabela.Current.BoundingRectangle;
            var pt = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            if (r.IsEmpty || !tr.Contains(pt)) { motivo = "a linha não está visível na tabela"; return null; }
            var janela = JanelaDe(l.Elemento);
            if (janela == IntPtr.Zero) { motivo = "não achei a janela da tabela"; return null; }
            if (GetForegroundWindow() != janela) { Frente(janela); Thread.Sleep(300); }
            if (Cancelado(true)) { motivo = "cancelado (Esc ou mouse)"; return null; }
            int fg = ProcessoDe(GetForegroundWindow());
            if (fg != p.Id) { Frente(p.MainWindowHandle); Thread.Sleep(300); Frente(janela); Thread.Sleep(300); fg = ProcessoDe(GetForegroundWindow()); }
            if (fg != p.Id) { motivo = "o Metre não ficou em primeiro plano (na frente: " + NomeProcesso(fg) + ")"; return null; }
            // O que está desenhado no ponto do clique tem de ser essa linha, dessa tabela.
            var noPonto = AutomationElement.FromPoint(pt);
            if (!Ancestral(noPonto, l.Elemento) || !Ancestral(noPonto, tabela)) { motivo = "o ponto do clique não é a linha da nota na aba CONTIGÊNCIA"; return null; }

            if (BotoesConfirmar(p.Id).Count > 0) { motivo = "já há uma janela do Metre esperando \"Entendi\"/\"Ok\"; feche-a antes"; return null; }
            DuploClique((int)pt.X, (int)pt.Y);

            var limite = DateTime.Now.AddSeconds(SegundosJanela);
            while (DateTime.Now < limite)
            {
                Thread.Sleep(300);
                if (Cancelado(false)) { motivo = "cancelado (Esc ou mouse) esperando a janela do Metre"; return null; }
                var botoes = BotoesConfirmar(p.Id);
                if (botoes.Count == 0) continue;
                if (botoes.Count > 1) { motivo = "o Metre mostrou mais de uma janela de confirmação; responda no Metre"; return null; }
                string texto = TextoDaJanela(botoes[0]);
                Clicar(botoes[0]);
                var fim = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < fim && BotoesConfirmar(p.Id).Count > 0) Thread.Sleep(200);
                if (BotoesConfirmar(p.Id).Count > 0) { motivo = "a janela do Metre não fechou com \"" + Normalizar(botoes[0].Current.Name) + "\" (" + texto + ")"; return null; }
                return texto;
            }
            motivo = "o Metre não mostrou janela com \"Entendi\"/\"Ok\" em " + SegundosJanela + " s depois do duplo clique";
            return null;
        }

        // Botão da janela de resultado do Metre ("Rejeição de Nota" com "Entendi !", visto em 22/10; ou "Ok"), em qualquer
        // janela do processo: pode ser janela própria ou sobreposta à de NFC-e.
        static readonly Regex Confirmar = new Regex(@"^(entendi\b.*|ok)$", RegexOptions.IgnoreCase);
        internal static List<AutomationElement> BotoesConfirmar(int pid)
        {
            var l = new List<AutomationElement>();
            foreach (var h in Janelas(pid))
            {
                try
                {
                    foreach (AutomationElement b in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                        if (Confirmar.IsMatch(Normalizar(b.Current.Name)) && !b.Current.IsOffscreen) l.Add(b);
                }
                catch (ElementNotAvailableException) { }
            }
            return l;
        }

        // Texto da janela do botão: sobe até o primeiro ancestral com texto além do botão (código, rejeição, chave).
        internal static string TextoDaJanela(AutomationElement botao)
        {
            var w = TreeWalker.RawViewWalker;
            var e = w.GetParent(botao);
            for (int i = 0; e != null && i < 8; i++, e = w.GetParent(e))
            {
                var textos = e.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>().Select(x => Normalizar(x.Current.Name)).Where(s => s.Length > 0 && !Confirmar.IsMatch(s)).Distinct().ToList();
                if (textos.Count >= 2) return string.Join(" | ", textos);
            }
            return "\"" + Normalizar(botao.Current.Name) + "\" clicado (texto da janela não lido)";
        }

        // Rola a tabela uma página para baixo (o JavaFX só cria as linhas visíveis); false = já está no fim.
        static bool Rolar(AutomationElement tabela)
        {
            ScrollPattern sp;
            try { sp = tabela.GetCurrentPattern(ScrollPattern.Pattern) as ScrollPattern; } catch (InvalidOperationException) { return false; }
            if (sp == null || !sp.Current.VerticallyScrollable || sp.Current.VerticalScrollPercent >= 99.5) return false;
            double antes = sp.Current.VerticalScrollPercent;
            sp.ScrollVertical(ScrollAmount.LargeIncrement);
            Thread.Sleep(500);
            return sp.Current.VerticalScrollPercent > antes;
        }

        void Clicar(AutomationElement b)
        {
            object inv;
            if (b.TryGetCurrentPattern(InvokePattern.Pattern, out inv)) { ((InvokePattern)inv).Invoke(); return; }
            var r = b.Current.BoundingRectangle;
            Mover((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            mouse_event(2, 0, 0, 0, IntPtr.Zero); mouse_event(4, 0, 0, 0, IntPtr.Zero);
        }

        void DuploClique(int x, int y)
        {
            Mover(x, y);
            for (int i = 0; i < 2; i++) { mouse_event(2, 0, 0, 0, IntPtr.Zero); mouse_event(4, 0, 0, 0, IntPtr.Zero); Thread.Sleep(60); }
        }

        void Mover(int x, int y) { SetCursorPos(x, y); GetCursorPos(out cursorNosso); }

        // Esc ou mouse mexido por quem está na frente da máquina. inicio = antes do primeiro clique, o cursor ainda é do usuário.
        bool primeiro = true;
        bool Cancelado(bool inicio)
        {
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) return true;
            POINT c; GetCursorPos(out c);
            if (inicio && primeiro) { primeiro = false; cursorNosso = c; return false; }
            return Math.Abs(c.X - cursorNosso.X) > 5 || Math.Abs(c.Y - cursorNosso.Y) > 5;
        }

        static bool Ancestral(AutomationElement e, AutomationElement alvo)
        {
            var w = TreeWalker.RawViewWalker;
            for (int i = 0; e != null && i < 30; i++, e = w.GetParent(e))
                if (Automation.Compare(e, alvo)) return true;
            return false;
        }

        static bool Dentro(Point pt, AutomationElement alvo)
        {
            try { return Ancestral(AutomationElement.FromPoint(pt), alvo); } catch (ElementNotAvailableException) { return false; }
        }

        static IntPtr JanelaDe(AutomationElement e)
        {
            var w = TreeWalker.RawViewWalker;
            for (int i = 0; e != null && i < 60; i++, e = w.GetParent(e))
                if (e.Current.NativeWindowHandle != 0) return new IntPtr(e.Current.NativeWindowHandle);
            return IntPtr.Zero;
        }

        static List<IntPtr> Janelas(int pid)
        {
            var l = new List<IntPtr>();
            EnumWindows((h, x) => { if (IsWindowVisible(h) && ProcessoDe(h) == pid) l.Add(h); return true; }, IntPtr.Zero);
            return l;
        }

        // O Windows só deixa trocar a janela da frente depois de uma tecla; um Alt solto libera (sem abrir menu do Metre,
        // que ainda não está na frente).
        static void Frente(IntPtr h)
        {
            keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2, IntPtr.Zero);
            SetForegroundWindow(h);
        }

        static string NomeProcesso(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; } catch (ArgumentException) { return "processo " + pid; }
        }

        static int ProcessoDe(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return (int)pid; }

        public static string Normalizar(string s)
        {
            return s == null ? "" : Regex.Replace(s, @"\s+", " ").Trim();
        }

        static string SemAcento(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in (s ?? "").Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern void mouse_event(int f, int x, int y, int d, IntPtr e);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int f, IntPtr e);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    }
}
