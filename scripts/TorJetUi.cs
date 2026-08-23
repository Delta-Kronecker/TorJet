// TorJetUi.cs - the WinForms front end (compiled together with start-tor.cs).
// TorJet Core License v1.0 (see LICENSE). Using this Core in another program
// requires the mandatory attribution of https://github.com/Delta-Kronecker/TorJet.
// One borderless, fully owner-drawn surface: no native controls are placed on
// the window, so the dark theme renders exactly the same everywhere. The
// console machinery of Program is reused behind the scenes; its output goes
// into an internal ring buffer that nothing displays yet.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace StartTor
{
    internal static partial class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private static void HideOwnConsoleWindow()
        {
            try
            {
                IntPtr h = GetConsoleWindow();
                if (h != IntPtr.Zero) ShowWindow(h, 0);
            }
            catch { }
        }

        // Console output produced by the reused machinery lands in a capped
        // in-memory buffer. Nothing renders it today; future builds can.
        private static readonly object uiLogLock = new object();
        private static readonly List<string> uiLogBuffer = new List<string>();
        private const int UiLogCap = 600;

        private static void AppendUiLogLine(string line)
        {
            if (line == null) return;
            lock (uiLogLock)
            {
                uiLogBuffer.Add(line);
                if (uiLogBuffer.Count > UiLogCap)
                    uiLogBuffer.RemoveRange(0, uiLogBuffer.Count - UiLogCap);
            }
        }

        private static void RunGui()
        {
            try
            {
                UiTextWriter ui = new UiTextWriter(AppendUiLogLine);
                Console.SetOut(ui);
                Console.SetError(ui);
            }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "torjet-ui-crash.txt"),
                        DateTime.Now + "\r\n" + e.Exception);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "torjet-ui-crash.txt"),
                        DateTime.Now + "\r\n" + e.ExceptionObject);
                }
                catch { }
            };
            Application.Run(new MainForm());
            try { Cleanup(); } catch { }
            Environment.Exit(0);
        }

        internal sealed class UiTextWriter : TextWriter
        {
            private readonly Action<string> sink;
            private readonly StringBuilder pending = new StringBuilder();
            public UiTextWriter(Action<string> sink) { this.sink = sink; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void Write(char value)
            {
                lock (pending)
                {
                    if (value == '\n')
                    {
                        string s = pending.ToString().TrimEnd();
                        pending.Length = 0;
                        if (s.Length > 0) sink(s);
                    }
                    else pending.Append(value);
                }
            }
            public override void Write(string value)
            {
                if (value == null) return;
                lock (pending)
                {
                    pending.Append(value);
                    DrainLines();
                }
            }
            public override void WriteLine(string value)
            {
                lock (pending)
                {
                    pending.Append(value == null ? "" : value);
                    string s = pending.ToString().TrimEnd();
                    pending.Length = 0;
                    if (s.Length > 0) sink(s);
                }
            }
            private void DrainLines()
            {
                int nl = FindNl();
                while (nl >= 0)
                {
                    string s = pending.ToString(0, nl).TrimEnd();
                    pending.Remove(0, nl + 1);
                    if (s.Length > 0) sink(s);
                    nl = FindNl();
                }
            }
            private int FindNl()
            {
                for (int i = 0; i < pending.Length; i++)
                    if (pending[i] == '\n') return i;
                return -1;
            }
        }

        // ---- palette / typography -------------------------------------------
        internal static class Theme
        {
            internal static readonly Color Bg = Color.FromArgb(23, 25, 31);
            internal static readonly Color Surface = Color.FromArgb(31, 34, 43);
            internal static readonly Color SurfaceAlt = Color.FromArgb(41, 45, 57);
            internal static readonly Color Border = Color.FromArgb(55, 60, 74);
            internal static readonly Color Text = Color.FromArgb(235, 238, 245);
            internal static readonly Color Muted = Color.FromArgb(148, 156, 174);
            internal static readonly Color Accent = Color.FromArgb(125, 70, 179);   // tor purple
            internal static readonly Color AccentSoft = Color.FromArgb(96, 58, 138);
            internal static readonly Color Green = Color.FromArgb(62, 190, 133);
            internal static readonly Color Red = Color.FromArgb(228, 92, 103);
            internal static readonly Color Amber = Color.FromArgb(220, 166, 80);
            internal const string FontName = "Segoe UI";
            internal static Font Big() { return new Font(FontName, 16.5f, FontStyle.Bold); }
            internal static Font H2() { return new Font(FontName, 9.75f, FontStyle.Bold); }
            internal static Font Body() { return new Font(FontName, 9.25f, FontStyle.Regular); }
            internal static Font Small() { return new Font(FontName, 8f, FontStyle.Regular); }
            internal static Font Caption() { return new Font(FontName, 7.25f, FontStyle.Bold); }
            internal static GraphicsPath RoundRect(Rectangle r, int radius)
            {
                int maxR = Math.Max(1, Math.Min(r.Width, r.Height) / 2);
                if (radius > maxR) radius = maxR;
                int d = radius * 2;
                GraphicsPath p = new GraphicsPath();
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
            internal static void Pill(Graphics g, Rectangle r, Color fill, Color border)
            {
                using (GraphicsPath path = RoundRect(r, r.Height / 2))
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (Pen pen = new Pen(border)) g.DrawPath(pen, path);
                }
            }
        }

        // ---- main form -------------------------------------------------------
        private sealed class MainForm : Form
        {
            private enum RunState { Idle, Connecting, Connected, Restarting }
            private enum Page { Main, Settings }

            private RunState state = RunState.Idle;
            private Page page = Page.Main;
            private readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();

            private int bootPct;
            private string bootTag = "";
            private string errorMsg = "";
            private DateTime errorMsgUntil = DateTime.MinValue;
            private int restartAttempts;
            private volatile bool sessionBusy;
            private bool tunWasOnBeforeRestart;

            private int hoverId = -1;
            private bool anyHover;

            private int editRow = -1;
            private string editBuf = "";
            private bool caretOn;
            private DateTime lastCaretFlip = DateTime.MinValue;

            private Rectangle rcClose, rcMin, rcModePrev, rcModeNext, rcModeVal,
                              rcProxy, rcTun, rcSettings, rcPower, rcBack;
            private readonly Rectangle[] rcRowVal = new Rectangle[9];
            private readonly Rectangle[] rcRowPrev = new Rectangle[9];
            private readonly Rectangle[] rcRowNext = new Rectangle[9];
            private readonly Rectangle[] rcRowBody = new Rectangle[9];

            private static readonly string[] SettingLabels =
            {
                "Strategy level", "Conflux sets", "Conflux legs", "Linked-set cap",
                "Keep-alive", "Set select", "Skip slow sets (RTT)", "Best % of sets",
                "Weak legs (top %)"
            };

            public MainForm()
            {
                Text = "TorJet";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(380, 442);
                BackColor = Theme.Bg;
                ForeColor = Theme.Text;
                Font = Theme.Body();
                DoubleBuffered = true;
                KeyPreview = true;

                string forced = Environment.GetEnvironmentVariable("TORJET_UI_PAGE");
                if (forced == "settings") page = Page.Settings;

                Resize += delegate { LayoutPass(); };
                Paint += OnPaintAll;
                MouseMove += OnMouseMoveAll;
                MouseDown += OnMouseDownAll;
                MouseLeave += delegate { anyHover = false; hoverId = -1; Invalidate(); };
                KeyDown += OnKeyDownAll;

                uiTimer.Interval = 250;
                uiTimer.Tick += UiTick;
                uiTimer.Start();
                LayoutPass();
                LogLine("TorJet " + TorJetVersion.App);
            }

            // ---- geometry --------------------------------------------------
            private void LayoutPass()
            {
                int w = ClientSize.Width;
                rcClose = new Rectangle(w - 40, 0, 40, 34);
                rcMin = new Rectangle(w - 78, 0, 38, 34);

                int cx = w / 2;
                rcPower = new Rectangle(cx - 52, 88, 104, 104);

                rcModePrev = new Rectangle(w - 158, 232, 30, 30);
                rcModeNext = new Rectangle(w - 24, 232, 30, 30);
                rcModeVal = new Rectangle(rcModePrev.Right, 232, rcModeNext.Left - rcModePrev.Right, 30);

                int half = (w - 24 * 2 - 10) / 2;
                rcProxy = new Rectangle(24, 276, half, 42);
                rcTun = new Rectangle(24 + half + 10, 276, half, 42);

                rcSettings = new Rectangle(24, 332, w - 48, 38);

                int ry = 66;
                for (int i = 0; i < 9; i++)
                {
                    rcRowBody[i] = new Rectangle(18, ry, w - 36, 36);
                    int valW = 152;
                    rcRowVal[i] = new Rectangle(w - 18 - valW, ry + 3, valW, 30);
                    rcRowPrev[i] = new Rectangle(rcRowVal[i].Left, ry + 3, 26, 30);
                    rcRowNext[i] = new Rectangle(rcRowVal[i].Right - 26, ry + 3, 26, 30);
                    ry += 38;
                }
                rcBack = new Rectangle(12, 4, 84, 28);
                Invalidate();
            }

            // ---- painting ---------------------------------------------------
            private Color StateColor()
            {
                switch (state)
                {
                    case RunState.Connected: return Theme.Green;
                    case RunState.Connecting: return Theme.Amber;
                    case RunState.Restarting: return Theme.Red;
                    default: return Theme.Muted;
                }
            }

            private string StateText()
            {
                switch (state)
                {
                    case RunState.Connected: return "CONNECTED";
                    case RunState.Connecting:
                        return bootPct > 0 ? "BOOTSTRAP " + bootPct + "%" : "CONNECTING";
                    case RunState.Restarting:
                        return "RESTART " + restartAttempts + "/3";
                    default: return "OFFLINE";
                }
            }

            private void OnPaintAll(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Bg);
                try
                {
                    PaintTitlebar(g);
                    if (page == Page.Main) PaintMain(g);
                    else PaintSettings(g);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "torjet-ui-paint.txt"),
                            DateTime.Now + "\r\n" + ex);
                    }
                    catch { }
                    lastPaintError = ex.Message;
                }
                if (lastPaintError.Length > 0)
                    TextRenderer.DrawText(g, "ui error: " + lastPaintError, Theme.Small(),
                        new Rectangle(8, ClientSize.Height - 26, ClientSize.Width - 16, 22),
                        Theme.Red, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
            }

            private string lastPaintError = "";

            private void PaintTitlebar(Graphics g)
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface))
                    g.FillRectangle(b, 0, 0, ClientSize.Width, 34);
                using (Pen pen = new Pen(Theme.Border))
                    g.DrawLine(pen, 0, 34, ClientSize.Width, 34);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(StateColor()))
                    g.FillEllipse(b, 14, 13, 9, 9);

                TextRenderer.DrawText(g, "TorJet", Theme.H2(),
                    new Rectangle(32, 0, 140, 34), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                bool hovClose = hoverId == 1;
                bool hovMin = hoverId == 2;
                if (hovClose) using (SolidBrush b = new SolidBrush(Color.FromArgb(40, Theme.Red))) g.FillRectangle(b, rcClose);
                if (hovMin) using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(b, rcMin);

                int midY = 17;
                using (Pen p = new Pen(hovMin ? Theme.Text : Theme.Muted, 1.6f))
                    g.DrawLine(p, rcMin.Left + 13, midY, rcMin.Right - 13, midY);
                using (Pen p = new Pen(hovClose ? Theme.Text : Theme.Muted, 1.6f))
                {
                    g.DrawLine(p, rcClose.Left + 14, midY - 4, rcClose.Right - 14, midY + 4);
                    g.DrawLine(p, rcClose.Left + 14, midY + 4, rcClose.Right - 14, midY - 4);
                }
            }

            private void PaintMain(Graphics g)
            {
                TextRenderer.DrawText(g, StateText(), Theme.Big(),
                    new Rectangle(0, 44, ClientSize.Width, 34), StateColor(),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                Rectangle ring = rcPower;
                using (Pen p = new Pen(Theme.SurfaceAlt, 4f))
                    g.DrawEllipse(p, ring);
                if (state == RunState.Connecting && bootPct > 0)
                {
                    float sweep = 360f * Math.Min(100, bootPct) / 100f;
                    if (sweep >= 1f)
                        using (Pen p = new Pen(Theme.Accent, 4f))
                            g.DrawArc(p, ring, -90, sweep);
                }
                else
                {
                    Color ringCol = state == RunState.Connected ? Theme.Green :
                                    state == RunState.Restarting ? Theme.Red : Theme.Border;
                    using (Pen p = new Pen(ringCol, 4f))
                        g.DrawEllipse(p, ring);
                }

                Color glyph = state == RunState.Idle ? Theme.Text : StateColor();
                int d = rcPower.Width - 44;
                Rectangle arc = new Rectangle(rcPower.Left + 22, rcPower.Top + 22, d, d);
                using (Pen p = new Pen(glyph, 4f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawArc(p, arc, -60, 300);
                    g.DrawLine(p, cx(), rcPower.Top + 16, cx(), rcPower.Top + 44);
                }
                if (state == RunState.Connecting && bootPct > 0)
                    TextRenderer.DrawText(g, bootPct + "%", Theme.Small(),
                        new Rectangle(rcPower.Left, rcPower.Bottom - 6, rcPower.Width, 16),
                        Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);

                string cap = ErrorOr(state == RunState.Idle ? "CONNECT" :
                                     state == RunState.Connected ? "DISCONNECT" : "CANCEL");
                TextRenderer.DrawText(g, cap, Theme.Body(),
                    new Rectangle(0, rcPower.Bottom + 8, ClientSize.Width, 22),
                    HasError() ? Theme.Red : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);

                TextRenderer.DrawText(g, "MODE", Theme.Caption(),
                    new Rectangle(24, rcModeVal.Y, 90, 30), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                int modeIdx = comboModeIndexSafe();
                Theme.Pill(g, rcModeVal, Theme.Surface, Theme.Border);
                TextRenderer.DrawText(g, PrettyMode(ModeNames[Math.Max(0, modeIdx)]), Theme.Body(),
                    rcModeVal, Theme.Text, TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                DrawChevron(g, rcModePrev, false, hoverId == 10);
                DrawChevron(g, rcModeNext, true, hoverId == 11);

                PaintTogglePill(g, rcProxy, "PROXY", ProxyIsOurs(), hoverId == 20);
                PaintTogglePill(g, rcTun, "TUN", TunActive(), hoverId == 21);

                bool hovSet = hoverId == 30;
                Theme.Pill(g, rcSettings, hovSet ? Theme.SurfaceAlt : Theme.Surface, Theme.Border);
                TextRenderer.DrawText(g, "SETTINGS", Theme.H2(),
                    new Rectangle(rcSettings.Left + 18, rcSettings.Y, rcSettings.Width - 36, rcSettings.Height),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                DrawChevron(g, new Rectangle(rcSettings.Right - 34,
                    rcSettings.Y + (rcSettings.Height - 24) / 2, 24, 24),
                    true, hovSet);
            }

            private int cx() { return rcPower.Left + rcPower.Width / 2; }

            private void PaintTogglePill(Graphics g, Rectangle r, string name, bool on, bool hovered)
            {
                Theme.Pill(g, r, hovered ? Theme.SurfaceAlt : Theme.Surface,
                           on ? Theme.Accent : Theme.Border);
                TextRenderer.DrawText(g, name, Theme.H2(),
                    new Rectangle(r.Left + 16, r.Y, r.Width - 40, r.Height),
                    on ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(on ? Theme.Green : Theme.Border))
                    g.FillEllipse(b, r.Right - 22, r.Y + r.Height / 2 - 5, 10, 10);
            }

            private void DrawChevron(Graphics g, Rectangle r, bool pointRight, bool hovered)
            {
                Color c = hovered ? Theme.Text : Theme.Muted;
                using (Pen p = new Pen(c, 1.8f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    int mx = r.Left + r.Width / 2;
                    int my = r.Top + r.Height / 2;
                    if (!pointRight)
                    {
                        g.DrawLine(p, mx + 3, my - 5, mx - 3, my);
                        g.DrawLine(p, mx - 3, my, mx + 3, my + 5);
                    }
                    else
                    {
                        g.DrawLine(p, mx - 3, my - 5, mx + 3, my);
                        g.DrawLine(p, mx + 3, my, mx - 3, my + 5);
                    }
                }
            }

            private void PaintSettings(Graphics g)
            {
                bool hovBack = hoverId == 40;
                TextRenderer.DrawText(g, hovBack ? "‹ BACK" : "‹ Back", Theme.Body(),
                    rcBack, hovBack ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "SETTINGS", Theme.H2(),
                    new Rectangle(0, 4, ClientSize.Width, 28), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                for (int i = 0; i < 9; i++)
                {
                    Rectangle body = rcRowBody[i];
                    bool selected = editRow == i;
                    Theme.Pill(g, body, Theme.Surface, selected ? Theme.AccentSoft : Theme.Border);
                    TextRenderer.DrawText(g, (i + 1) + "  " + SettingLabels[i], Theme.Body(),
                        new Rectangle(body.Left + 14, body.Y, body.Width - 184, body.Height),
                        Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                    PaintSettingValue(g, i);
                }
                TextRenderer.DrawText(g, "applies on next connect", Theme.Small(),
                    new Rectangle(0, rcRowBody[8].Bottom + 8, ClientSize.Width, 18),
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            }

            private void PaintSettingValue(Graphics g, int i)
            {
                Rectangle v = rcRowVal[i];
                if (i == 4)
                {
                    bool on = keepAliveEnabled;
                    Theme.Pill(g, new Rectangle(v.Right - 52, v.Y + 2, 52, 26),
                               on ? Theme.Accent : Theme.SurfaceAlt, on ? Theme.Accent : Theme.Border);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(Color.White))
                        g.FillEllipse(b, on ? v.Right - 52 + 29 : v.Right - 52 + 3, v.Y + 5, 20, 20);
                    TextRenderer.DrawText(g, on ? "ON" : "OFF", Theme.Caption(),
                        new Rectangle(v.Left, v.Y, v.Width - 60, v.Height), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    return;
                }
                string text = SettingDisplay(i);
                bool editing = editRow == i;
                Theme.Pill(g, v, Theme.SurfaceAlt, editing ? Theme.Accent : Theme.Border);
                Rectangle inner = Rectangle.Inflate(v, -6, 0);
                if (editing)
                {
                    string shown = editBuf.Length == 0 ? "_" : editBuf + (caretOn ? "|" : "");
                    TextRenderer.DrawText(g, shown, Theme.Body(), inner, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    DrawChevron(g, rcRowPrev[i], false, hoverId == 101 + i * 3);
                    DrawChevron(g, rcRowNext[i], true, hoverId == 102 + i * 3);
                    Rectangle mid = Rectangle.FromLTRB(rcRowPrev[i].Right, v.Y, rcRowNext[i].Left, v.Bottom);
                    TextRenderer.DrawText(g, text, Theme.Body(), mid, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                }
            }

            // ---- settings model ---------------------------------------------
            private static readonly int[] RttSteps =
            {
                0, 50, 100, 150, 200, 250, 300, 400, 500, 650, 800, 1000, 1250,
                1500, 2000, 3000, 5000, 8000, 10000
            };

            private string SettingDisplay(int i)
            {
                switch (i)
                {
                    case 0: return StrategyNames[Math.Max(0, comboStrategyIndexSafe())];
                    case 1: return confluxSets == 0 ? "consensus" : confluxSets.ToString();
                    case 2: return confluxLegs == 0 ? "consensus" : confluxLegs.ToString();
                    case 3: return confluxLinkedSets == 0 ? "consensus" : confluxLinkedSets.ToString();
                    case 5: return SetSelectionNames[Math.Max(0, Math.Min(3, confluxSelection))];
                    case 6: return confluxRttMax == 0 ? "off" : confluxRttMax + " ms";
                    case 7: return confluxRttPct == 0 ? "off" : confluxRttPct + "%";
                    case 8: return watchRttPct == 0 ? "off" : watchRttPct + "%";
                    default: return "";
                }
            }

            private void CycleSetting(int i, int dir)
            {
                switch (i)
                {
                    case 0:
                    {
                        int idx = Math.Max(0, comboStrategyIndexSafe());
                        idx = Wrap(idx + dir, StrategyNames.Length);
                        WriteStrategyFile(idx);
                        ApplyConfluxPresetForStrategy(idx);
                        break;
                    }
                    case 1: confluxSets = ClampCycle(confluxSets + dir, 0, 32); WriteConfluxSetting(ConfluxSetsFile, confluxSets); break;
                    case 2: confluxLegs = ClampCycle(confluxLegs + dir, 0, 16); WriteConfluxSetting(ConfluxLegsFile, confluxLegs); break;
                    case 3: confluxLinkedSets = ClampCycle(confluxLinkedSets + dir, 0, 32); WriteConfluxSetting(ConfluxLinkedSetsFile, confluxLinkedSets); break;
                    case 5:
                        confluxSelection = Wrap(confluxSelection + dir, SetSelectionNames.Length);
                        WriteConfluxSetting(ConfluxSelectionFile, confluxSelection);
                        break;
                    case 6: confluxRttMax = StepRtt(confluxRttMax, dir); WriteConfluxSetting(ConfluxRttMaxFile, confluxRttMax); break;
                    case 7: confluxRttPct = StepClamp(confluxRttPct, dir * 5, 0, 100); WriteConfluxSetting(ConfluxRttPctFile, confluxRttPct); break;
                    case 8: watchRttPct = StepClamp(watchRttPct, dir * 5, 0, 100); WriteConfluxSetting(WatchRttPctFile, watchRttPct); break;
                }
                Invalidate();
            }

            private static int Wrap(int v, int n) { return ((v % n) + n) % n; }
            private static int ClampCycle(int v, int lo, int hi)
            {
                if (v < lo) return hi;
                if (v > hi) return lo;
                return v;
            }
            private static int StepClamp(int cur, int delta, int lo, int hi)
            {
                int v = cur + delta;
                if (v < lo) v = lo;
                if (v > hi) v = hi;
                return v;
            }
            private static int StepRtt(int cur, int dir)
            {
                int idx = 0;
                for (int i = 0; i < RttSteps.Length; i++) if (RttSteps[i] == cur) { idx = i; break; }
                idx = idx + dir;
                if (idx < 0) idx = 0;
                if (idx >= RttSteps.Length) idx = RttSteps.Length - 1;
                return RttSteps[idx];
            }

            private bool RowIsNumeric(int i)
            {
                return i == 1 || i == 2 || i == 3 || i == 6 || i == 7 || i == 8;
            }

            private void CommitEdit()
            {
                if (editRow < 0) return;
                int i = editRow;
                int v;
                if (int.TryParse(editBuf, out v))
                {
                    switch (i)
                    {
                        case 1: confluxSets = Math.Max(0, Math.Min(32, v)); WriteConfluxSetting(ConfluxSetsFile, confluxSets); break;
                        case 2: confluxLegs = Math.Max(0, Math.Min(16, v)); WriteConfluxSetting(ConfluxLegsFile, confluxLegs); break;
                        case 3: confluxLinkedSets = Math.Max(0, Math.Min(32, v)); WriteConfluxSetting(ConfluxLinkedSetsFile, confluxLinkedSets); break;
                        case 6: confluxRttMax = Math.Max(0, Math.Min(10000, v)); WriteConfluxSetting(ConfluxRttMaxFile, confluxRttMax); break;
                        case 7: confluxRttPct = Math.Max(0, Math.Min(100, v)); WriteConfluxSetting(ConfluxRttPctFile, confluxRttPct); break;
                        case 8: watchRttPct = Math.Max(0, Math.Min(100, v)); WriteConfluxSetting(WatchRttPctFile, watchRttPct); break;
                    }
                }
                editRow = -1;
                editBuf = "";
                Invalidate();
            }

            // ---- input ------------------------------------------------------
            private void OnMouseMoveAll(object s, MouseEventArgs e)
            {
                int h = HitTest(e.Location);
                if (h != hoverId || !anyHover)
                {
                    hoverId = h;
                    anyHover = true;
                    Invalidate();
                }
                Cursor = h != -1 ? Cursors.Hand : Cursors.Default;
            }

            private void OnMouseDownAll(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                Point p = e.Location;
                int h = HitTest(p);
                if (h == -1)
                {
                    if (p.Y <= 34)
                    {
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    }
                    else if (editRow >= 0) CommitEdit();
                    return;
                }
                switch (h)
                {
                    case 1: CloseApp(); break;
                    case 2: WindowState = FormWindowState.Minimized; break;
                    case 10: CycleMode(-1); break;
                    case 11: CycleMode(1); break;
                    case 20: ApplyProxyToggle(!ProxyIsOurs()); break;
                    case 21: ApplyTunToggle(!TunActive()); break;
                    case 30: page = Page.Settings; CancelEdit(); LayoutPass(); Invalidate(); break;
                    case 40: page = Page.Main; CancelEdit(); LayoutPass(); Invalidate(); break;
                    default:
                        if (page != Page.Settings || h < 100) break;
                        int baseIdx = (h - 100) / 3;
                        int part = (h - 100) % 3;
                        if (baseIdx == 4)
                        {
                            keepAliveEnabled = !keepAliveEnabled;
                            WriteKeepAliveFile(keepAliveEnabled);
                            if (!keepAliveEnabled) StopKeepAlive();
                            else if (state == RunState.Connected) StartKeepAlive();
                            Invalidate();
                        }
                        else if (part == 1) CycleSetting(baseIdx, -1);
                        else if (part == 2) CycleSetting(baseIdx, 1);
                        else if (RowIsNumeric(baseIdx))
                        {
                            CommitEdit();
                            editRow = baseIdx;
                            editBuf = RawNumeric(baseIdx);
                            Invalidate();
                        }
                        break;
                }
            }

            private string RawNumeric(int i)
            {
                switch (i)
                {
                    case 1: return confluxSets.ToString();
                    case 2: return confluxLegs.ToString();
                    case 3: return confluxLinkedSets.ToString();
                    case 6: return confluxRttMax.ToString();
                    case 7: return confluxRttPct.ToString();
                    case 8: return watchRttPct.ToString();
                    default: return "";
                }
            }

            private void OnKeyDownAll(object s, KeyEventArgs e)
            {
                if (editRow >= 0)
                {
                    if (e.KeyCode == Keys.Escape) { CancelEdit(); e.Handled = true; return; }
                    if (e.KeyCode == Keys.Enter) { CommitEdit(); e.Handled = true; return; }
                    if (e.KeyCode == Keys.Back)
                    {
                        if (editBuf.Length > 0) editBuf = editBuf.Substring(0, editBuf.Length - 1);
                        Invalidate(); e.Handled = true; return;
                    }
                    if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
                    {
                        if (editBuf.Length < 5) editBuf += (char)('0' + (e.KeyCode - Keys.D0));
                        Invalidate(); e.Handled = true; return;
                    }
                    if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
                    {
                        if (editBuf.Length < 5) editBuf += (char)('0' + (e.KeyCode - Keys.NumPad0));
                        Invalidate(); e.Handled = true; return;
                    }
                    return;
                }
                if (e.KeyCode == Keys.Escape && page == Page.Settings)
                {
                    page = Page.Main; LayoutPass(); Invalidate();
                }
            }

            private void CancelEdit()
            {
                editRow = -1;
                editBuf = "";
            }

            private int HitTest(Point p)
            {
                if (rcClose.Contains(p)) return 1;
                if (rcMin.Contains(p)) return 2;
                if (page == Page.Main)
                {
                    if (rcModePrev.Contains(p)) return 10;
                    if (rcModeNext.Contains(p)) return 11;
                    if (rcProxy.Contains(p)) return 20;
                    if (rcTun.Contains(p)) return 21;
                    if (rcSettings.Contains(p)) return 30;
                }
                else
                {
                    if (rcBack.Contains(p)) return 40;
                    for (int i = 0; i < 9; i++)
                    {
                        if (i == 4)
                        {
                            if (rcRowVal[i].Contains(p)) return 104;
                            continue;
                        }
                        if (rcRowPrev[i].Contains(p)) return 100 + i * 3 + 1;
                        if (rcRowNext[i].Contains(p)) return 100 + i * 3 + 2;
                        if (RowIsNumeric(i) && rcRowVal[i].Contains(p)) return 100 + i * 3;
                    }
                }
                return -1;
            }

            // ---- mode helpers -----------------------------------------------
            private int comboModeIndexSafe()
            {
                int m = ReadLastMode();
                return m >= 0 ? m : ParseMode("obfs4");
            }

            private int comboStrategyIndexSafe()
            {
                int st = ReadLastStrategy();
                return st >= 0 ? st : DefaultStrategy();
            }

            private void CycleMode(int dir)
            {
                int m = Wrap(comboModeIndexSafe() + dir, ModeNames.Length);
                WriteModeFile(m);
                Invalidate();
            }

            private string PrettyMode(string m)
            {
                switch (m)
                {
                    case "vanilla": return "Vanilla";
                    case "obfs4": return "Obfs4";
                    case "webtunnel": return "WebTunnel";
                    case "snowflake": return "Snowflake";
                    default: return "Direct";
                }
            }

            // ---- session wiring ----------------------------------------------
            private delegate void SimpleAction();

            private void UiInvokeDelegate(SimpleAction d)
            {
                try { BeginInvoke(d); } catch { }
            }

            private void ApplyProxyToggle(bool want)
            {
                bool ours = ProxyIsOurs();
                if (want && !ours)
                {
                    SetSystemProxy(true);
                    FlashMessage("system proxy on");
                }
                else if (!want && ours)
                {
                    SetSystemProxy(false);
                    FlashMessage("system proxy off");
                }
                Invalidate();
            }

            private void ApplyTunToggle(bool want)
            {
                if (sessionBusy) return;
                RunBg(delegate
                {
                    bool active = TunActive();
                    if (want && !active) EnableTunAndWait();
                    else if (!want && active) DisableTunAndWait();
                    UiInvokeDelegate(delegate { Invalidate(); });
                });
            }

            private void EnableTunAndWait()
            {
                try
                {
                    Environment.SetEnvironmentVariable("TUN_DATA_DIR", DataDir, EnvironmentVariableTarget.Process);
                    var psi = new ProcessStartInfo
                    {
                        FileName = TunHelperExe,
                        Arguments = "on",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
                catch
                {
                    FlashMessage("TUN needs admin approval");
                    return;
                }
                for (int i = 0; i < 25; i++)
                {
                    Thread.Sleep(1000);
                    string r = ReadTunResult();
                    if (r.StartsWith("on:") || r.StartsWith("error:"))
                    {
                        if (r.StartsWith("error:")) FlashMessage("TUN failed");
                        DnsCacheStart();
                        return;
                    }
                }
                FlashMessage("TUN timed out");
            }

            private void DisableTunAndWait()
            {
                DnsCacheStop();
                try { File.WriteAllText(TunStopFile, "stop", new UTF8Encoding(false)); } catch { }
                for (int i = 0; i < 20 && TunActive(); i++) Thread.Sleep(500);
                if (TunActive())
                {
                    SpawnElevated("off", true);
                }
            }

            private void FlashMessage(string msg)
            {
                errorMsg = msg;
                errorMsgUntil = DateTime.UtcNow.AddSeconds(6);
                Invalidate();
            }

            private bool HasError()
            {
                return errorMsg.Length > 0 && DateTime.UtcNow < errorMsgUntil;
            }

            private string ErrorOr(string fallback)
            {
                return HasError() ? errorMsg : fallback;
            }

            private void LogLine(string line)
            {
                AppendUiLogLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line);
            }

            private void RunBg(ThreadStart work)
            {
                Thread t = new Thread(work) { IsBackground = true };
                t.Start();
            }

            // ---- connect / disconnect ------------------------------------------
            private void OnConnectButton()
            {
                if (state == RunState.Idle) Connect();
                else Disconnect("stopped by user");
            }

            private void Connect()
            {
                if (sessionBusy) return;
                sessionBusy = true;
                restartAttempts = 0;
                bootPct = 0;
                SetState(RunState.Connecting);
                int mode = comboModeIndexSafe();
                int strategy = comboStrategyIndexSafe();
                WriteModeFile(mode);
                WriteStrategyFile(strategy);
                RunBg(delegate { SessionWorker(mode, strategy); });
            }

            private void SessionWorker(int mode, int strategy)
            {
                StopPreviousRun();
                for (int i = 0; i < 30 && PreviousRunActive(); i++) Thread.Sleep(500);
                string err;
                bool aborted;
                Process proc = StartTorAndWait(mode, strategy, false, delegate(int pct, string tag)
                {
                    bootPct = pct;
                    bootTag = tag ?? "";
                    UiInvokeDelegate(delegate { Invalidate(); });
                }, out err, out aborted);
                if (proc == null)
                {
                    sessionBusy = false;
                    UiInvokeDelegate(delegate
                    {
                        bootPct = 0;
                        SetState(RunState.Idle);
                        FlashMessage(FirstLine(err));
                        LogLine("[x] " + FirstLine(err));
                    });
                    return;
                }
                circuitWatchStop = false;
                circuitWatchWarmup = true;
                if (circuitWatchEnabled)
                {
                    Thread watcher = new Thread(CircuitWatchLoop) { IsBackground = true };
                    watcher.Start();
                }
                if (keepAliveEnabled) StartKeepAlive();
                StartWatchdog();
                Thread warmupEnd = new Thread(delegate()
                {
                    Thread.Sleep(TimeSpan.FromSeconds(warmupRelaxSeconds));
                    circuitWatchWarmup = false;
                }) { IsBackground = true };
                warmupEnd.Start();

                sessionBusy = false;
                UiInvokeDelegate(delegate
                {
                    bootPct = 100;
                    SetState(RunState.Connected);
                });
            }

            private void Disconnect(string why)
            {
                if (sessionBusy && state == RunState.Connecting)
                {
                    try { if (torProc != null) torProc.Kill(); } catch { }
                }
                watchdogStop = true;
                circuitWatchStop = true;
                StopKeepAlive();
                DnsCacheStop();
                try { if (torProc != null) torProc.Kill(); } catch { }
                torProc = null;
                LogLine("tor stopped (" + why + ")");
                RunBg(delegate
                {
                    Cleanup();
                    cleaned = false;
                    UiInvokeDelegate(delegate
                    {
                        bootPct = 0;
                        SetState(RunState.Idle);
                        sessionBusy = false;
                    });
                });
            }

            private void SetState(RunState s)
            {
                state = s;
                Invalidate();
            }

            private static string FirstLine(string s)
            {
                if (string.IsNullOrEmpty(s)) return "(no details)";
                int nl = s.IndexOf('\n');
                return nl > 0 ? s.Substring(0, nl).TrimEnd() : s.TrimEnd();
            }

            // ---- tick: death / watchdog / caret ---------------------------------
            private void UiTick(object s, EventArgs e)
            {
                if (editRow >= 0 && (DateTime.UtcNow - lastCaretFlip).TotalMilliseconds >= 450)
                {
                    caretOn = !caretOn;
                    lastCaretFlip = DateTime.UtcNow;
                    Invalidate();
                }

                if (HasError() && DateTime.UtcNow >= errorMsgUntil)
                {
                    errorMsg = "";
                    Invalidate();
                }

                if (state == RunState.Connected || state == RunState.Restarting)
                {
                    bool alive = false;
                    try { torProc.Refresh(); alive = !torProc.HasExited; } catch { }
                    if (!alive)
                    {
                        if (watchdogTriggered && restartAttempts < 3)
                        {
                            restartAttempts++;
                            SetState(RunState.Restarting);
                            watchdogStop = true;
                            circuitWatchStop = true;
                            StopKeepAlive();
                            DnsCacheStop();
                            tunWasOnBeforeRestart = TunActive();
                            LogLine("watchdog: restarting tor (" + restartAttempts + "/3)");
                            int mode = comboModeIndexSafe(), strat = comboStrategyIndexSafe();
                            bootPct = 0;
                            RunBg(delegate
                            {
                                try { torProc.WaitForExit(5000); } catch { }
                                for (int i = 0; i < 30 && PreviousRunActive(); i++) Thread.Sleep(500);
                                try { if (File.Exists(LockFile)) File.Delete(LockFile); } catch { }
                                cleaned = false;
                                SessionWorker(mode, strat);
                                if (tunWasOnBeforeRestart && !TunActive())
                                {
                                    EnableTunAndWait();
                                    tunWasOnBeforeRestart = TunActive();
                                }
                                UiInvokeDelegate(delegate { SetState(RunState.Connected); });
                            });
                        }
                        else if (!watchdogTriggered)
                        {
                            int code = -1;
                            try { code = torProc.ExitCode; } catch { }
                            Disconnect("tor exited (" + code + ")");
                        }
                    }
                }
            }

            private void CloseApp()
            {
                if (state != RunState.Idle)
                {
                    watchdogStop = true;
                    circuitWatchStop = true;
                    StopKeepAlive();
                    DnsCacheStop();
                    try { if (torProc != null) torProc.Kill(); } catch { }
                    Cleanup();
                }
                Close();
            }
        }
    }
}
