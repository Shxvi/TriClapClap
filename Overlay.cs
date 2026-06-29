using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using triclapclap.Core;
using triclapclap.Audio;
using triclapclap.WebHook;
using triclapclap.Input;

namespace triclapclap
{
    public partial class OverlayForm : Form
    {
        private AppConfig config = null!;
        private readonly HashSet<Keys> listenKeys = new HashSet<Keys>();
        private readonly HashSet<Keys> keysCurrentlyDown = new HashSet<Keys>();

        private Image[] idleFrames = Array.Empty<Image>();
        private Image[] hitFrames = Array.Empty<Image>();
        private readonly string[] hitFileNames = { "PlayArea-Hit-0.png", "PlayArea-Hit-1.png", "PlayArea-Hit-2.png", "PlayArea-Hit-4.png", "PlayArea-Hit-5.png", "PlayArea-Hit-6.png" };

        private int currentFrame = 0;
        private long totalHits = 0;
        private long lastSavedHits = 0;
        private long lastSaveTicks = 0;
        private bool isHeadless = false;

        private const int MaxCpsTrack = 512;
        private readonly long[] clickTicks = new long[MaxCpsTrack];
        private volatile int clickHead = 0;
        private volatile int clickTail = 0;
        private int currentCps = 0;

        private System.Windows.Forms.Timer animationTimer = null!;
        private System.Windows.Forms.Timer uiTimer = null!;
        private FileSystemWatcher? configWatcher;
        private FileSystemWatcher? assetsWatcher;
        private NotifyIcon? trayIcon;

        private Font? cachedFont;
        private Pen? cachedOutlinePen;
        private SolidBrush? cachedTextBrush;
        private float cachedEmSize = 0f;
        private readonly StringFormat cachedStringFormat = new StringFormat();
        private readonly GraphicsPath cachedTextPath = new GraphicsPath();
        private Rectangle lastTextBounds = Rectangle.Empty;

        private string lastRenderedText = string.Empty;
        private long lastRenderedHits = -1;
        private int lastRenderedCps = -1;

        private readonly MciAudioStack audioStack = new MciAudioStack();
        private readonly OverlayServer server = new OverlayServer();
        private readonly RawInputReceiver input = new RawInputReceiver();
        private readonly string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private readonly string hitsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "total_hits.txt");

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        public OverlayForm()
        {
            timeBeginPeriod(1);
            ParseArgs();

            config = AppConfig.Load(configPath);
            InitializeWindow();

            input.OnKeyStateChanged += HandleKeyStateChange;
            _ = this.Handle;
            input.Register(this.Handle);

            LoadTotalHits();
            InitializeTimers();
            LoadAssets();
            ApplyConfig(false);

            SetupWatchers();
            server.Start(config.WsPort);

            FormClosing += (s, e) =>
            {
                timeEndPeriod(1);
                SaveTotalHits();
                configWatcher?.Dispose();
                assetsWatcher?.Dispose();
                trayIcon?.Dispose();
                audioStack.Dispose();
                server.Dispose();
                cachedFont?.Dispose();
                cachedOutlinePen?.Dispose();
                cachedTextBrush?.Dispose();
                cachedTextPath.Dispose();
                cachedStringFormat.Dispose();
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveTotalHits();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (config != null && config.IsClickThrough && !isHeadless)
                {
                    cp.ExStyle |= 0x80000 | 0x20;
                }
                return cp;
            }
        }

        private void InitializeWindow()
        {
            this.Size = new Size(1024, 576);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.DoubleBuffered = true;

            Icon? appIcon = null;
            string localIcoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");

            if (File.Exists(localIcoPath))
            {
                try { appIcon = new Icon(localIcoPath); } catch { }
            }

            if (appIcon == null)
            {
                try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            }

            if (appIcon != null)
            {
                this.Icon = appIcon;
            }

            if (isHeadless)
            {
                trayIcon = new NotifyIcon
                {
                    Icon = this.Icon ?? SystemIcons.Application,
                    Text = "triclapclap",
                    Visible = true
                };
                var menu = new ContextMenuStrip();
                menu.Items.Add("Выход", null, (s, e) => this.Close());
                trayIcon.ContextMenuStrip = menu;
            }
        }

        private void HandleKeyStateChange(Keys key, bool isPressed)
        {
            if (config.ListenToAllKeys || listenKeys.Contains(key))
            {
                if (isPressed && !keysCurrentlyDown.Contains(key))
                {
                    keysCurrentlyDown.Add(key);

                    long currentHits = Interlocked.Increment(ref totalHits);
                    if (config.IsSoundLocalEnabled) audioStack.Play(currentHits);

                    long nowTicks = Environment.TickCount64;
                    int currentHead = clickHead;
                    clickTicks[currentHead] = nowTicks;
                    Interlocked.Exchange(ref clickHead, (currentHead + 1) % MaxCpsTrack);

                    this.BeginInvoke(new Action(RegisterHitUi));
                }
                else if (!isPressed)
                {
                    keysCurrentlyDown.Remove(key);
                }
            }
        }

        private void RegisterHitUi()
        {
            if (hitFrames.Length > 0)
            {
                currentFrame = 0;
                animationTimer.Stop();
                animationTimer.Start();
            }
            RequestRedraw(true);
            server.Broadcast(totalHits, currentCps, hitFrames.Length > 0 ? hitFileNames[currentFrame] : "PlayArea-0.png", true);
        }

        protected override void WndProc(ref Message m)
        {
            if (input.ProcessMessage(ref m)) return;

            base.WndProc(ref m);

            if (m.Msg == 0x0084 && (int)m.Result == 1 && !isHeadless && !config.IsClickThrough)
            {
                m.Result = (IntPtr)2;
            }
        }

        private void ParseArgs()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-h" || arg == "--headless")
                {
                    isHeadless = true;
                    this.ShowInTaskbar = false;
                    this.WindowState = FormWindowState.Minimized;
                }
            }
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(!isHeadless && value);

        private void LoadTotalHits()
        {
            if (File.Exists(hitsPath) && long.TryParse(File.ReadAllText(hitsPath), out long h))
            {
                totalHits = lastSavedHits = h;
            }
        }

        private void SaveTotalHits()
        {
            try { File.WriteAllText(hitsPath, totalHits.ToString()); } catch { }
        }

        private void InitializeTimers()
        {
            animationTimer = new System.Windows.Forms.Timer();
            animationTimer.Tick += (s, e) =>
            {
                currentFrame++;
                if (currentFrame >= hitFrames.Length) animationTimer.Stop();
                RequestRedraw(true);
                server.Broadcast(totalHits, currentCps, currentFrame < hitFrames.Length ? hitFileNames[currentFrame] : "PlayArea-0.png", false);
            };

            uiTimer = new System.Windows.Forms.Timer { Interval = 50 };
            uiTimer.Tick += (s, e) =>
            {
                long now = Environment.TickCount64;
                int localHead = clickHead;
                while (clickTail != localHead && (now - clickTicks[clickTail]) > 1000)
                {
                    clickTail = (clickTail + 1) % MaxCpsTrack;
                }

                int count = (localHead >= clickTail) ? (localHead - clickTail) : (MaxCpsTrack - clickTail + localHead);
                if (count != currentCps)
                {
                    currentCps = count;
                    RequestRedraw(false);
                    server.Broadcast(totalHits, currentCps, currentFrame < hitFrames.Length ? hitFileNames[currentFrame] : "PlayArea-0.png", false);
                }

                if (totalHits != lastSavedHits && (now - lastSaveTicks) > 5000)
                {
                    SaveTotalHits();
                    lastSavedHits = totalHits;
                    lastSaveTicks = now;
                }
            };
            uiTimer.Start();
        }

        private void ApplyConfig(bool restartServer)
        {
            listenKeys.Clear();
            keysCurrentlyDown.Clear();
            if (config.TargetKeys != null)
            {
                foreach (var kStr in config.TargetKeys.Split(','))
                {
                    if (Enum.TryParse(kStr.Trim(), true, out Keys key)) listenKeys.Add(key);
                }
            }

            animationTimer.Interval = config.AnimationDelayMs;
            if (!isHeadless) this.BackColor = ColorTranslator.FromHtml(config.ChromaKeyColor);

            cachedFont?.Dispose();
            cachedOutlinePen?.Dispose();
            cachedTextBrush?.Dispose();

            cachedFont = new Font(config.FontName, config.FontSize, FontStyle.Bold);
            cachedOutlinePen = new Pen(ColorTranslator.FromHtml(config.OutlineColor), config.OutlineSize * 2) { LineJoin = LineJoin.Round };
            cachedTextBrush = new SolidBrush(ColorTranslator.FromHtml(config.FontColor));

            using (Graphics g = this.CreateGraphics()) cachedEmSize = g.DpiY * cachedFont.Size / 72;

            lastRenderedText = string.Empty;
            if (restartServer) server.Start(config.WsPort);
            RequestRedraw(true);
        }

        private void SetupWatchers()
        {
            configWatcher = new FileSystemWatcher { Path = AppDomain.CurrentDomain.BaseDirectory, Filter = "config.json", NotifyFilter = NotifyFilters.LastWrite };
            configWatcher.Changed += (s, e) => { Thread.Sleep(100); this.BeginInvoke(new Action(() => { config = AppConfig.Load(configPath); ApplyConfig(true); })); };
            configWatcher.EnableRaisingEvents = true;

            string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            if (!Directory.Exists(assetsDir)) Directory.CreateDirectory(assetsDir);
            assetsWatcher = new FileSystemWatcher { Path = assetsDir, Filter = "*.*", NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            assetsWatcher.Changed += (s, e) => { Thread.Sleep(200); this.BeginInvoke(new Action(() => { LoadAssets(); RequestRedraw(true); })); };
            assetsWatcher.EnableRaisingEvents = true;
        }

        private void LoadAssets()
        {
            string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            foreach (var img in idleFrames) img?.Dispose();
            foreach (var img in hitFrames) img?.Dispose();

            var idleList = new List<Image>();
            foreach (var name in new[] { "PlayArea-0.png", "PlayArea-1.png" })
            {
                var img = LoadScaled(Path.Combine(assetsDir, name));
                if (img != null) idleList.Add(img);
            }
            idleFrames = idleList.ToArray();

            var hitList = new List<Image>();
            foreach (var name in hitFileNames)
            {
                var img = LoadScaled(Path.Combine(assetsDir, name));
                if (img != null) hitList.Add(img);
            }
            hitFrames = hitList.ToArray();

            audioStack.Load(Path.Combine(assetsDir, "slap.wav"));
        }

        private Image? LoadScaled(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var orig = new Bitmap(fs);
                var scaled = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(orig, 0, 0, this.Width, this.Height);
                return scaled;
            }
            catch { return null; }
        }

        private void RequestRedraw(bool isFrameUpdate)
        {
            if (isHeadless) return;
            if (cachedFont == null || cachedOutlinePen == null || cachedTextBrush == null) return;

            if (totalHits != lastRenderedHits || currentCps != lastRenderedCps || lastRenderedText == string.Empty)
            {
                lastRenderedText = string.Format(config.TextFormat, totalHits, currentCps);
                lastRenderedHits = totalHits;
                lastRenderedCps = currentCps;

                cachedTextPath.Reset();
                cachedTextPath.AddString(lastRenderedText, cachedFont.FontFamily, (int)cachedFont.Style, cachedEmSize, new Point(config.TextX, config.TextY), cachedStringFormat);
            }

            if (isFrameUpdate)
            {
                this.Invalidate();
            }
            else
            {
                Rectangle newBounds = Rectangle.Ceiling(cachedTextPath.GetBounds());
                newBounds.Inflate(config.OutlineSize + 5, config.OutlineSize + 5);
                this.Invalidate(Rectangle.Union(lastTextBounds, newBounds));
                lastTextBounds = newBounds;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (isHeadless) return;
            base.OnPaint(e);

            Action drawAsset = () =>
            {
                Image? img = (animationTimer.Enabled && currentFrame < hitFrames.Length) ? hitFrames[currentFrame] : (idleFrames.Length > 0 ? idleFrames[0] : null);
                if (img != null) e.Graphics.DrawImage(img, 0, 0);
            };

            Action drawText = () =>
            {
                if (cachedOutlinePen == null || cachedTextBrush == null || string.IsNullOrEmpty(lastRenderedText)) return;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                if (config.IsOutlineEnabled && config.OutlineSize > 0) e.Graphics.DrawPath(cachedOutlinePen, cachedTextPath);
                e.Graphics.FillPath(cachedTextBrush, cachedTextPath);
            };

            if (config.IsTextAboveAssets) { drawAsset(); drawText(); }
            else { drawText(); drawAsset(); }
        }

        private void InitializeComponent()
        {

        }
    }
}