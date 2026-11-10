using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public int SegundosJanela = 90;      // espera da janela com Ok depois do duplo clique (a transmissão demora)
        public Func<Process> AcharProcesso = AcharMetre;

        readonly Log log;
        readonly Action<string> andamento;

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
            public List<string> Canceladas = new List<string>();   // pedido cancelado: Metre pediu para excluir; respondido Não
            public string Parada;           // null = terminou a lista
        }

        // O Metre abre a janela de NFC-e como outra janela do mesmo processo java: procura em todas as janelas visíveis
        // (o MainWindowHandle pode ser a de NFC-e, sem "Metre" no título; visto em 06/11).
        public static Process AcharMetre()
        {
            foreach (var p in Process.GetProcessesByName("javaw").Concat(Process.GetProcessesByName("java")))
                foreach (var h in Janelas(p.Id))
                    if (Titulo(h).StartsWith("Metre", StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // Cabeçalho da coluna da aba Contingência, em qualquer grafia ("CONTIGÊNCIA" do Metre, "CONTINGÊNCIA", sem acento).
        public static bool EhColunaContingencia(string nome)
        {
            return Regex.IsMatch(SemAcento(Normalizar(nome)), @"^CLIENTE CONTI?N?GENCIA$", RegexOptions.IgnoreCase);
        }

        // Leituras sempre ao vivo (Current): o JavaFX do Metre devolve vazio no CacheRequest (cabeçalhos sem nome, 10/11).
        static List<AutomationElement> Filhos(AutomationElement e)
        {
            return e.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToList();
        }

        // Tabela da aba Contingência que está de fato na tela (o que está desenhado no centro dela é a própria tabela),
        // em qualquer janela do Metre; null se a janela de NFC-e não estiver aberta nessa aba.
        public static AutomationElement TabelaContingencia(Process p)
        {
            string vistas;
            return TabelaContingencia(p, out vistas);
        }

        public static AutomationElement TabelaContingencia(Process p, out string vistas)
        {
            var cabecalhos = new List<string>();
            vistas = "";
            foreach (var h in Janelas(p.Id))
            {
                AutomationElementCollection tabelas;
                try { tabelas = AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Table)); }
                catch (ElementNotAvailableException) { continue; }
                foreach (AutomationElement t in tabelas)
                {
                    List<string> nomes;
                    try { nomes = Filhos(t).Select(c => Normalizar(c.Current.Name)).Where(s => s.Length > 0).ToList(); }
                    catch (ElementNotAvailableException) { continue; }
                    if (!nomes.Any(EhColunaContingencia)) { cabecalhos.Add("[" + string.Join(", ", nomes.Take(6)) + "]"); continue; }
                    var r = t.Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width < 50 || r.Height < 50) { cabecalhos.Add("[aba Contingência fora da tela]"); continue; }
                    if (NaTela(t)) return t;
                    cabecalhos.Add("[aba Contingência coberta por outra janela]");
                }
            }
            vistas = cabecalhos.Count == 0 ? "nenhuma tabela nas janelas do Metre" : "tabelas vistas: " + string.Join(" ", cabecalhos.Take(5));
            return null;
        }

        // A tabela continua na tela e na frente (o centro dela é ela mesma); traz a janela dela para a frente se precisar.
        static bool NaTela(AutomationElement t)
        {
            try
            {
                var r = t.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 50 || r.Height < 50) return false;
                var janela = JanelaDe(t);
                if (janela != IntPtr.Zero && GetForegroundWindow() != janela) { Frente(janela); Thread.Sleep(300); }
                return Dentro(new Point(r.X + r.Width / 2, r.Y + r.Height / 2), t);
            }
            catch (ElementNotAvailableException) { return false; }
        }

        public static List<LinhaMetre> Linhas(AutomationElement tabela)
        {
            var linhas = new List<LinhaMetre>();
            foreach (AutomationElement row in tabela.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom)))
            {
                var cel = Filhos(row);
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
            string vistas;
            var t = TabelaContingencia(p, out vistas);
            if (t == null) { motivo = "abra no Metre a janela de NFC-e na aba CONTIGÊNCIA (não achei a tabela dessa aba na tela; " + vistas + ")"; return null; }
            return Linhas(t).Where(l => EmContingencia(l.Status)).ToList();
        }

        public Resultado Executar()
        {
            var res = new Resultado();
            var feitas = new HashSet<string>();
            log.Info("Macro da contingência do Metre - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
            Cancelado();   // descarta um Esc apertado antes de começar
            try
            {
                Process p = null;
                AutomationElement tabela = null;
                while (true)
                {
                    string motivo;
                    if (p == null || p.HasExited) p = AcharProcesso();
                    if (p == null) { res.Parada = "o Metre foi fechado"; break; }
                    // A mesma tabela entre uma nota e outra, enquanto ela continuar na tela (procurar de novo é o lento).
                    if (tabela == null || !NaTela(tabela)) tabela = TabelaContingencia(p);
                    if (tabela == null) { res.Parada = "a aba CONTIGÊNCIA do Metre não está mais na tela"; break; }
                    var prox = Linhas(tabela).FirstOrDefault(l => EmContingencia(l.Status) && !feitas.Contains(l.Chave));
                    if (prox == null)
                    {
                        if (Rolar(tabela)) continue;
                        break;
                    }
                    feitas.Add(prox.Chave);
                    andamento("Metre: nota " + prox + " (" + (res.Feitas.Count + 1) + ")...");
                    var relogio = Stopwatch.StartNew();
                    string msg = UmaNota(p, tabela, prox, out motivo);
                    log.Info("  (" + relogio.ElapsedMilliseconds + " ms)");
                    if (motivo != null) { res.Parada = "nota " + prox + ": " + motivo; break; }
                    res.Feitas.Add(prox + ": " + msg);
                    if (msg.StartsWith(PrefixoCancelado)) res.Canceladas.Add(prox.ToString());
                    log.Info("  " + prox + ": " + msg);
                    Thread.Sleep(300);   // o Metre redesenha a tabela depois da janela
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
            if (Cancelado()) { motivo = "cancelado (Esc)"; return null; }
            int fg = ProcessoDe(GetForegroundWindow());
            if (fg != p.Id) { Frente(p.MainWindowHandle); Thread.Sleep(300); Frente(janela); Thread.Sleep(300); fg = ProcessoDe(GetForegroundWindow()); }
            if (fg != p.Id) { motivo = "o Metre não ficou em primeiro plano (na frente: " + NomeProcesso(fg) + ")"; return null; }
            // O que está desenhado no ponto do clique tem de ser essa linha, dessa tabela.
            var noPonto = AutomationElement.FromPoint(pt);
            if (!Ancestral(noPonto, l.Elemento) || !Ancestral(noPonto, tabela)) { motivo = "o ponto do clique não é a linha da nota na aba CONTIGÊNCIA"; return null; }

            List<AutomationElement> nao;
            if (Botoes(p.Id, out nao).Count > 0 || nao.Count > 0) { motivo = "já há uma janela do Metre esperando resposta; feche-a antes"; return null; }
            DuploClique((int)pt.X, (int)pt.Y);

            var limite = DateTime.Now.AddSeconds(SegundosJanela);
            while (DateTime.Now < limite)
            {
                Thread.Sleep(150);
                if (Cancelado()) { motivo = "cancelado (Esc) esperando a janela do Metre"; return null; }
                var botoes = Botoes(p.Id, out nao);
                // "Este pedido foi cancelado, deseja excluir esta nota e inutilizar a numeração?": responde Não (Sim inutiliza na SEFAZ).
                bool cancelado = botoes.Count == 0 && nao.Count > 0;
                if (cancelado) botoes = nao;
                if (botoes.Count == 0) continue;
                if (botoes.Count > 1) { motivo = "o Metre mostrou mais de uma janela esperando resposta; responda no Metre"; return null; }
                string texto = TextoDaJanela(botoes[0]);
                Clicar(botoes[0]);
                var fim = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < fim && AindaAberta(p.Id, cancelado)) Thread.Sleep(100);
                if (AindaAberta(p.Id, cancelado)) { motivo = "a janela do Metre não fechou com \"" + Normalizar(botoes[0].Current.Name) + "\" (" + texto + ")"; return null; }
                return cancelado ? PrefixoCancelado + texto : texto;
            }
            motivo = "o Metre não mostrou janela com \"Entendi\"/\"Ok\" em " + SegundosJanela + " s depois do duplo clique";
            return null;
        }

        static bool AindaAberta(int pid, bool cancelado)
        {
            List<AutomationElement> nao;
            var c = Botoes(pid, out nao);
            return (cancelado ? nao : c).Count > 0;
        }

        public const string PrefixoCancelado = "PEDIDO CANCELADO (respondido Não; excluir a nota): ";

        // Botões que o macro responde, numa só varredura das janelas do processo (janela própria ou sobreposta à de NFC-e):
        // "Entendi !"/"Ok" da janela de resultado ("Rejeição de Nota", vista em 22/10) e, em nao, o "Não" da pergunta de
        // pedido cancelado (vista em 04/11), só se o texto da janela falar de pedido cancelado.
        static readonly Regex Confirmar = new Regex(@"^(entendi\b.*|ok)$", RegexOptions.IgnoreCase);
        internal static List<AutomationElement> Botoes(int pid, out List<AutomationElement> nao)
        {
            var l = new List<AutomationElement>();
            nao = new List<AutomationElement>();
            foreach (var h in Janelas(pid))
            {
                try
                {
                    foreach (AutomationElement b in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                    {
                        string nome = Normalizar(b.Current.Name);
                        if (Confirmar.IsMatch(nome)) { if (!b.Current.IsOffscreen) l.Add(b); }
                        else if (SemAcento(nome).ToLowerInvariant() == "nao" && !b.Current.IsOffscreen &&
                            Regex.IsMatch(SemAcento(TextoDaJanela(b)), @"pedido foi cancelad", RegexOptions.IgnoreCase)) nao.Add(b);
                    }
                }
                catch (ElementNotAvailableException) { }
            }
            return l;
        }
        internal static List<AutomationElement> BotoesConfirmar(int pid) { List<AutomationElement> n; return Botoes(pid, out n); }
        internal static List<AutomationElement> BotoesNaoCancelado(int pid) { List<AutomationElement> n; Botoes(pid, out n); return n; }

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
            Clique((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        }

        void DuploClique(int x, int y)
        {
            Clique(x, y); Thread.Sleep(60); Clique(x, y);
        }

        // A posição vai dentro de cada evento do clique: o acesso remoto (TeamViewer, AnyDesk) move o cursor do servidor
        // junto com o do técnico, e um SetCursorPos seguido de clique poderia cair em outro lugar.
        internal static void Clique(int x, int y)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            int nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1)), ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
            const int Mover = 0x0001, Desce = 0x0002, Sobe = 0x0004, Absoluto = 0x8000, Virtual = 0x4000;
            mouse_event(Mover | Absoluto | Virtual, nx, ny, 0, IntPtr.Zero);
            mouse_event(Mover | Desce | Absoluto | Virtual, nx, ny, 0, IntPtr.Zero);
            mouse_event(Mover | Sobe | Absoluto | Virtual, nx, ny, 0, IntPtr.Zero);
        }

        // Só o Esc para: o mouse não serve de sinal, porque o acesso remoto mexe no cursor do servidor sozinho
        // (TeamViewer, 04/11: o macro parou na primeira nota com a janela "Rejeição de Nota" já na tela).
        static bool Cancelado() { return (GetAsyncKeyState(0x1B) & 0x8001) != 0; }

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

        static string Titulo(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static int ProcessoDe(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return (int)pid; }

        public static string Normalizar(string s)
        {
            return s == null ? "" : Regex.Replace(s, @"\s+", " ").Trim();
        }

        // Também em minúsculas e sem espaços nas pontas: aqui só se compara sem maiúsculas.
        static string SemAcento(string s) { return AjusteChave.SemAcento(s); }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
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
