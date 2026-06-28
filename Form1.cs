using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace triclapclap
{
    public partial class Form1 : Form
    {
        private class AppConfig
        {
            public string TargetKeys { get; set; } = "Z,X";
            public int AnimationDelayMs { get; set; } = 40;
            public string ChromaKeyColor { get; set; } = "#00FF00";
            public string FontColor { get; set; } = "#FFFFFF";
            public string FontName { get; set; } = "Arial";
            public int FontSize { get; set; } = 28;
            public int TextX { get; set; } = 20;
            public int TextY { get; set; } = 20;
            public bool IsSoundLocalEnabled { get; set; } = true;
            public bool IsSoundStreamEnabled { get; set; } = true;
            public bool IsOutlineEnabled { get; set; } = true;
            public string OutlineColor { get; set; } = "#000000";
            public int OutlineSize { get; set; } = 2;
            public bool IsTextAboveAssets { get; set; } = false;
            public int WsPort { get; set; } = 8080;
            public string TextFormat { get; set; } = "hits {0}\ncps {1}";
        }

        private AppConfig config = null!;
        private readonly HashSet<Keys> listenKeys = new HashSet<Keys>();
        private readonly HashSet<Keys> keysCurrentlyDown = new HashSet<Keys>();

        private Image[] idleFrames = Array.Empty<Image>();
        private Image[] hitFrames = Array.Empty<Image>();

        private int currentFrame = 0;
        private long totalHits = 0;
        private long lastSavedHits = 0;
        private long lastSaveTicks = 0;
        private bool isHeadless = false;

        private const int MaxCpsTrack = 512;
        private readonly long[] clickTicks = new long[MaxCpsTrack];
        private int clickHead = 0;
        private int clickTail = 0;
        private int currentCps = 0;
        private readonly object tickLock = new object();

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        private System.Windows.Forms.Timer animationTimer = null!;
        private System.Windows.Forms.Timer uiTimer = null!;
        private FileSystemWatcher? configWatcher;
        private FileSystemWatcher? assetsWatcher;

        private HttpListener? httpListener;
        private readonly ConcurrentDictionary<WebSocket, byte> activeSockets = new ConcurrentDictionary<WebSocket, byte>();
        private int activeWsPort = 0;

        private NotifyIcon? trayIcon;

        private readonly string[] hitFileNames = { "PlayArea-Hit-0.png", "PlayArea-Hit-1.png", "PlayArea-Hit-2.png", "PlayArea-Hit-4.png", "PlayArea-Hit-5.png", "PlayArea-Hit-6.png" };

        private Font? cachedFont;
        private Pen? cachedOutlinePen;
        private SolidBrush? cachedTextBrush;
        private readonly StringFormat cachedStringFormat;
        private readonly GraphicsPath cachedTextPath;

        private string lastRenderedText = string.Empty;
        private long lastRenderedHits = -1;
        private int lastRenderedCps = -1;

        private readonly byte[] jsonBuffer = new byte[1024];
        private readonly MemoryStream jsonStream;

        private readonly Action registerHitDelegate;

        private const int AudioChannelCount = 8;
        private bool isSoundLoaded = false;
        private string[] mciPlayCommands = null!;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

        public Form1()
        {
            ParseCommandLineArgs();

            _ = this.Handle;

            registerHitDelegate = RegisterHit;
            cachedTextPath = new GraphicsPath();
            cachedStringFormat = new StringFormat();
            jsonStream = new MemoryStream(jsonBuffer);

            LoadConfig();
            LoadTotalHits();
            InitializeOverlayWindow();
            InitializeComponentManual();
            LoadAssets();
            SetupConfigWatcher();
            SetupAssetsWatcher();
            StartWebSocketServer();

            _proc = HookCallback;
            _hookID = SetHook(_proc);

            FormClosing += (s, e) =>
            {
                SaveTotalHits();
                UnhookWindowsHookEx(_hookID);
                configWatcher?.Dispose();
                assetsWatcher?.Dispose();
                StopWebSocketServer();
                trayIcon?.Dispose();

                for (int i = 0; i < AudioChannelCount; i++)
                {
                    mciSendString($"close slapch{i}", null, 0, IntPtr.Zero);
                }

                cachedFont?.Dispose();
                cachedOutlinePen?.Dispose();
                cachedTextBrush?.Dispose();
                cachedTextPath?.Dispose();
                cachedStringFormat.Dispose();
                jsonStream.Dispose();
            };

            AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveTotalHits();
        }

        private void ParseCommandLineArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (arg == "-h" || arg == "--headless")
                {
                    isHeadless = true;
                    this.ShowInTaskbar = false;
                    this.WindowState = FormWindowState.Minimized;
                }
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(isHeadless ? false : value);
        }

        private void LoadTotalHits()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "total_hits.txt");
            if (File.Exists(path) && long.TryParse(File.ReadAllText(path), out long savedHits))
            {
                totalHits = savedHits;
                lastSavedHits = savedHits;
            }
        }

        private void SaveTotalHits()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "total_hits.txt");
                File.WriteAllText(path, totalHits.ToString());
            }
            catch { }
        }

        private void LoadConfig()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
                    if (config.TextFormat != null)
                    {
                        config.TextFormat = config.TextFormat.Replace("\\n", "\n");
                    }
                }
                catch { return; }
            }
            else
            {
                config = new AppConfig();
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }

            listenKeys.Clear();
            keysCurrentlyDown.Clear();
            foreach (var keyStr in config.TargetKeys.Split(','))
            {
                if (Enum.TryParse(keyStr.Trim(), true, out Keys key))
                    listenKeys.Add(key);
            }

            UpdateGdiResources();
        }

        private void UpdateGdiResources()
        {
            cachedFont?.Dispose();
            cachedOutlinePen?.Dispose();
            cachedTextBrush?.Dispose();

            cachedFont = new Font(config.FontName, config.FontSize, FontStyle.Bold);
            cachedOutlinePen = new Pen(ColorTranslator.FromHtml(config.OutlineColor), config.OutlineSize * 2)
            {
                LineJoin = LineJoin.Round
            };
            cachedTextBrush = new SolidBrush(ColorTranslator.FromHtml(config.FontColor));

            lastRenderedText = string.Empty;
            lastRenderedHits = -1;
            lastRenderedCps = -1;
        }

        private void SetupConfigWatcher()
        {
            configWatcher = new FileSystemWatcher
            {
                Path = AppDomain.CurrentDomain.BaseDirectory,
                Filter = "config.json",
                NotifyFilter = NotifyFilters.LastWrite
            };
            configWatcher.Changed += OnConfigChanged;
            configWatcher.EnableRaisingEvents = true;
        }

        private void SetupAssetsWatcher()
        {
            string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            if (!Directory.Exists(assetsDir)) Directory.CreateDirectory(assetsDir);

            assetsWatcher = new FileSystemWatcher
            {
                Path = assetsDir,
                Filter = "*.*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            assetsWatcher.Changed += (s, e) => OnAssetsFolderChanged();
            assetsWatcher.Created += (s, e) => OnAssetsFolderChanged();
            assetsWatcher.EnableRaisingEvents = true;
        }

        private void OnConfigChanged(object sender, FileSystemEventArgs e)
        {
            System.Threading.Thread.Sleep(100);
            this.BeginInvoke(new Action(() =>
            {
                LoadConfig();
                ApplyConfigToUi();

                if (activeWsPort != config.WsPort)
                {
                    StopWebSocketServer();
                    StartWebSocketServer();
                }
            }));
        }

        private void OnAssetsFolderChanged()
        {
            System.Threading.Thread.Sleep(200);
            this.BeginInvoke(new Action(() =>
            {
                LoadAssets();
                if (!isHeadless) this.Invalidate();
            }));
        }

        private void ApplyConfigToUi()
        {
            if (animationTimer != null) animationTimer.Interval = config.AnimationDelayMs;
            if (!isHeadless)
            {
                this.BackColor = ColorTranslator.FromHtml(config.ChromaKeyColor);
                this.Invalidate();
            }
        }

        private void InitializeOverlayWindow()
        {
            this.Size = new Size(1024, 576);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = false;
            this.DoubleBuffered = true;
            this.BackColor = ColorTranslator.FromHtml(config.ChromaKeyColor);

            if (isHeadless)
            {
                trayIcon = new NotifyIcon
                {
                    Icon = SystemIcons.Application,
                    Text = "triclapclap (Headless)",
                    Visible = true
                };

                ContextMenuStrip trayMenu = new ContextMenuStrip();
                trayMenu.Items.Add("Exit", null, (s, e) => this.Close());
                trayIcon.ContextMenuStrip = trayMenu;
            }
        }

        private void InitializeComponentManual()
        {
            animationTimer = new System.Windows.Forms.Timer { Interval = config.AnimationDelayMs };
            animationTimer.Tick += AnimationTimer_Tick;

            uiTimer = new System.Windows.Forms.Timer { Interval = 50 };
            uiTimer.Tick += UiTimer_Tick;
            uiTimer.Start();
        }

        private void LoadAssets()
        {
            string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            if (!Directory.Exists(assetsDir)) Directory.CreateDirectory(assetsDir);

            foreach (var img in idleFrames) img.Dispose();
            foreach (var img in hitFrames) img.Dispose();

            var idleList = new List<Image>();
            foreach (var name in new[] { "PlayArea-0.png", "PlayArea-1.png" })
            {
                var img = LoadAndScaleImage(Path.Combine(assetsDir, name), this.Width, this.Height);
                if (img != null) idleList.Add(img);
            }
            idleFrames = idleList.ToArray();

            var hitList = new List<Image>();
            foreach (var name in hitFileNames)
            {
                var img = LoadAndScaleImage(Path.Combine(assetsDir, name), this.Width, this.Height);
                if (img != null) hitList.Add(img);
            }
            hitFrames = hitList.ToArray();

            for (int i = 0; i < AudioChannelCount; i++)
            {
                mciSendString($"close slapch{i}", null, 0, IntPtr.Zero);
            }
            isSoundLoaded = false;

            string soundPath = Path.Combine(assetsDir, "slap.wav");
            if (File.Exists(soundPath))
            {
                try
                {
                    mciPlayCommands = new string[AudioChannelCount];
                    for (int i = 0; i < AudioChannelCount; i++)
                    {
                        mciSendString($"open \"{soundPath}\" type waveaudio alias slapch{i}", null, 0, IntPtr.Zero);
                        mciPlayCommands[i] = $"play slapch{i} from 0";
                    }
                    isSoundLoaded = true;
                }
                catch { isSoundLoaded = false; }
            }
        }

        private Image? LoadAndScaleImage(string filePath, int targetWidth, int targetHeight)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var originalBmp = new Bitmap(stream))
                {
                    Bitmap scaledBmp = new Bitmap(targetWidth, targetHeight);
                    using (Graphics g = Graphics.FromImage(scaledBmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(originalBmp, 0, 0, targetWidth, targetHeight);
                    }
                    return scaledBmp;
                }
            }
            catch { return null; }
        }

        private void StartWebSocketServer()
        {
            try
            {
                activeWsPort = config.WsPort;
                httpListener = new HttpListener();
                httpListener.Prefixes.Add($"http://localhost:{activeWsPort}/");
                httpListener.Start();
                httpListener.BeginGetContext(OnWebSocketContextReceived, null);
            }
            catch { }
        }

        private void StopWebSocketServer()
        {
            try
            {
                httpListener?.Stop();
                httpListener?.Close();
                foreach (var ws in activeSockets.Keys) ws.Dispose();
                activeSockets.Clear();
            }
            catch { }
        }

        private async void OnWebSocketContextReceived(IAsyncResult ar)
        {
            if (httpListener == null || !httpListener.IsListening) return;
            try
            {
                HttpListenerContext context = httpListener.EndGetContext(ar);
                httpListener.BeginGetContext(OnWebSocketContextReceived, null);

                if (context.Request.IsWebSocketRequest)
                {
                    HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                    WebSocket ws = wsContext.WebSocket;
                    activeSockets.TryAdd(ws, 0);
                    _ = HandleSocketLifetime(ws);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch { }
        }

        private async Task HandleSocketLifetime(WebSocket ws)
        {
            byte[] buffer = new byte[1024];
            try
            {
                await SendStateToSocket(ws);
                while (ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                }
            }
            catch { }
            finally
            {
                activeSockets.TryRemove(ws, out _);
                ws.Dispose();
            }
        }

        private string GetCurrentFrameName()
        {
            if (animationTimer != null && animationTimer.Enabled && currentFrame < hitFileNames.Length)
            {
                return hitFileNames[currentFrame];
            }
            return "PlayArea-0.png";
        }

        private int SerializeStateToBuffer(bool playSoundEvent)
        {
            jsonStream.Position = 0;
            jsonStream.SetLength(0);

            using (var writer = new Utf8JsonWriter(jsonStream, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("totalHits", totalHits);
                writer.WriteNumber("cps", currentCps);
                writer.WriteString("frame", GetCurrentFrameName());
                writer.WriteBoolean("playSound", playSoundEvent && config.IsSoundStreamEnabled);
                writer.WriteEndObject();
            }
            return (int)jsonStream.Position;
        }

        private void BroadcastData(bool playSoundEvent)
        {
            if (activeSockets.IsEmpty) return;

            int bytesWritten = SerializeStateToBuffer(playSoundEvent);
            var segment = new ArraySegment<byte>(jsonBuffer, 0, bytesWritten);

            foreach (var ws in activeSockets.Keys)
            {
                if (ws.State == WebSocketState.Open)
                {
                    _ = ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }

        private Task SendStateToSocket(WebSocket ws)
        {
            int bytesWritten = SerializeStateToBuffer(false);
            return ws.SendAsync(new ArraySegment<byte>(jsonBuffer, 0, bytesWritten), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                string moduleName = curModule?.ModuleName ?? "app.exe";
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(moduleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;

                if (listenKeys.Contains(key))
                {
                    if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                    {
                        if (!keysCurrentlyDown.Contains(key))
                        {
                            keysCurrentlyDown.Add(key);
                            this.BeginInvoke(registerHitDelegate);
                        }
                    }
                    else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                    {
                        keysCurrentlyDown.Remove(key);
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void RegisterHit()
        {
            totalHits++;

            long nowTicks = Environment.TickCount64;
            lock (tickLock)
            {
                clickTicks[clickHead] = nowTicks;
                clickHead = (clickHead + 1) % MaxCpsTrack;
            }

            if (config.IsSoundLocalEnabled && isSoundLoaded)
            {
                int channel = (int)(totalHits % AudioChannelCount);
                mciSendString(mciPlayCommands[channel], null, 0, IntPtr.Zero);
            }

            if (hitFrames.Length > 0)
            {
                currentFrame = 0;
                animationTimer.Stop();
                animationTimer.Start();
            }

            if (!isHeadless) this.Invalidate();
            BroadcastData(playSoundEvent: true);
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            currentFrame++;
            if (currentFrame >= hitFrames.Length)
            {
                animationTimer.Stop();
            }
            if (!isHeadless) this.Invalidate();
            BroadcastData(playSoundEvent: false);
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            long now = Environment.TickCount64;
            bool changed = false;

            lock (tickLock)
            {
                while (clickTail != clickHead && (now - clickTicks[clickTail]) > 1000)
                {
                    clickTail = (clickTail + 1) % MaxCpsTrack;
                }

                int count = (clickHead >= clickTail)
                    ? (clickHead - clickTail)
                    : (MaxCpsTrack - clickTail + clickHead);

                if (count != currentCps)
                {
                    currentCps = count;
                    changed = true;
                }
            }

            if (changed)
            {
                if (!isHeadless) this.Invalidate();
                BroadcastData(playSoundEvent: false);
            }

            if (totalHits != lastSavedHits && (now - lastSaveTicks) > 5000)
            {
                SaveTotalHits();
                lastSavedHits = totalHits;
                lastSaveTicks = now;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (isHeadless) return;

            base.OnPaint(e);

            if (config.IsTextAboveAssets)
            {
                DrawBackgroundAsset(e.Graphics);
                DrawTextOverlay(e.Graphics);
            }
            else
            {
                DrawTextOverlay(e.Graphics);
                DrawBackgroundAsset(e.Graphics);
            }
        }

        private void DrawBackgroundAsset(Graphics g)
        {
            Image? currentImage = null;
            if (animationTimer.Enabled && currentFrame < hitFrames.Length) currentImage = hitFrames[currentFrame];
            else if (idleFrames.Length > 0) currentImage = idleFrames[0];

            if (currentImage != null) g.DrawImage(currentImage, 0, 0);
        }

        private void DrawTextOverlay(Graphics g)
        {
            if (cachedFont == null || cachedOutlinePen == null || cachedTextBrush == null)
                return;

            if (totalHits != lastRenderedHits || currentCps != lastRenderedCps || lastRenderedText == string.Empty)
            {
                lastRenderedText = string.Format(config.TextFormat, totalHits, currentCps);
                lastRenderedHits = totalHits;
                lastRenderedCps = currentCps;

                cachedTextPath.Reset();
                float emSize = g.DpiY * cachedFont.Size / 72;
                Point pt = new Point(config.TextX, config.TextY);

                cachedTextPath.AddString(lastRenderedText, cachedFont.FontFamily, (int)cachedFont.Style, emSize, pt, cachedStringFormat);
            }

            if (config.IsOutlineEnabled && config.OutlineSize > 0)
            {
                g.DrawPath(cachedOutlinePen, cachedTextPath);
            }
            g.FillPath(cachedTextBrush, cachedTextPath);
        }
    }
}
