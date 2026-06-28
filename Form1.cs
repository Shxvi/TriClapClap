using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
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
            public bool IsSoundEnabled { get; set; } = true;
            public bool IsOutlineEnabled { get; set; } = true;
            public string OutlineColor { get; set; } = "#000000";
            public int OutlineSize { get; set; } = 2;
            public bool IsTextAboveAssets { get; set; } = false;
            public int WsPort { get; set; } = 8080;
        }

        private AppConfig config = null!;
        private HashSet<Keys> listenKeys = new HashSet<Keys>();
        private readonly HashSet<Keys> keysCurrentlyDown = new HashSet<Keys>();

        private SoundPlayer? slapSound;
        private MemoryStream? currentSoundStream;

        private Image[] idleFrames = Array.Empty<Image>();
        private Image[] hitFrames = Array.Empty<Image>();

        private int currentFrame = 0;
        private long totalHits = 0;
        private ConcurrentQueue<DateTime> clickTimestamps = new ConcurrentQueue<DateTime>();

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        private System.Windows.Forms.Timer animationTimer = null!;
        private System.Windows.Forms.Timer uiTimer = null!;
        private FileSystemWatcher? configWatcher;
        private FileSystemWatcher? assetsWatcher;

        private HttpListener? httpListener;
        private readonly ConcurrentDictionary<WebSocket, byte> activeSockets = new ConcurrentDictionary<WebSocket, byte>();
        private int activeWsPort = 0;

        private readonly string[] hitFileNames = { "PlayArea-Hit-0.png", "PlayArea-Hit-1.png", "PlayArea-Hit-2.png", "PlayArea-Hit-4.png", "PlayArea-Hit-5.png", "PlayArea-Hit-6.png" };

        public Form1()
        {
            LoadConfig();
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
                UnhookWindowsHookEx(_hookID);
                configWatcher?.Dispose();
                assetsWatcher?.Dispose();
                StopWebSocketServer();
                currentSoundStream?.Dispose();
            };
        }

        private void LoadConfig()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(configPath))
            {
                try { config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig(); }
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
                this.Invalidate();
            }));
        }

        private void ApplyConfigToUi()
        {
            if (animationTimer != null) animationTimer.Interval = config.AnimationDelayMs;
            this.BackColor = ColorTranslator.FromHtml(config.ChromaKeyColor);
            this.Invalidate();
        }

        private void InitializeOverlayWindow()
        {
            this.Size = new Size(1024, 576);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = false;
            this.DoubleBuffered = true;
            this.BackColor = ColorTranslator.FromHtml(config.ChromaKeyColor);
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
                var img = LoadImageWithoutLock(Path.Combine(assetsDir, name));
                if (img != null) idleList.Add(img);
            }
            idleFrames = idleList.ToArray();

            var hitList = new List<Image>();
            foreach (var name in hitFileNames)
            {
                var img = LoadImageWithoutLock(Path.Combine(assetsDir, name));
                if (img != null) hitList.Add(img);
            }
            hitFrames = hitList.ToArray();

            string soundPath = Path.Combine(assetsDir, "slap.wav");
            if (File.Exists(soundPath))
            {
                try
                {
                    byte[] soundBytes = File.ReadAllBytes(soundPath);
                    currentSoundStream?.Dispose();
                    currentSoundStream = new MemoryStream(soundBytes);
                    slapSound = new SoundPlayer(currentSoundStream);
                    slapSound.Load();
                }
                catch
                {
                    slapSound = null;
                }
            }
        }

        private Image? LoadImageWithoutLock(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (var bmp = new Bitmap(stream))
                    {
                        return new Bitmap(bmp);
                    }
                }
            }
            catch
            {
                return null;
            }
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

        private void BroadcastData()
        {
            if (activeSockets.IsEmpty) return;

            var packet = new { totalHits = totalHits, cps = clickTimestamps.Count, frame = GetCurrentFrameName() };
            byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
            var segment = new ArraySegment<byte>(jsonBytes);

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
            var packet = new { totalHits = totalHits, cps = clickTimestamps.Count, frame = GetCurrentFrameName() };
            byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
            return ws.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, CancellationToken.None);
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
                            this.BeginInvoke(new Action(() => RegisterHit()));
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
            clickTimestamps.Enqueue(DateTime.Now);

            if (config.IsSoundEnabled)
            {
                try { slapSound?.Play(); } catch { }
            }

            if (hitFrames.Length > 0)
            {
                currentFrame = 0;
                animationTimer.Stop();
                animationTimer.Start();
            }

            this.Invalidate();
            BroadcastData();
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            currentFrame++;
            if (currentFrame >= hitFrames.Length)
            {
                animationTimer.Stop();
            }
            this.Invalidate();
            BroadcastData();
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            bool changed = false;
            while (clickTimestamps.TryPeek(out DateTime stamp) && (now - stamp).TotalSeconds > 1.0)
            {
                clickTimestamps.TryDequeue(out _);
                changed = true;
            }

            if (changed || activeSockets.Count > 0)
            {
                this.Invalidate();
                BroadcastData();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

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

            if (currentImage != null) g.DrawImage(currentImage, 0, 0, this.Width, this.Height);
        }

        private void DrawTextOverlay(Graphics g)
        {
            string text = $"hits {totalHits}\ncps {clickTimestamps.Count}";
            using (Font font = new Font(config.FontName, config.FontSize, FontStyle.Bold))
            {
                Color foreColor = ColorTranslator.FromHtml(config.FontColor);
                Color outlineColor = ColorTranslator.FromHtml(config.OutlineColor);
                Rectangle rect = new Rectangle(config.TextX, config.TextY, this.Width - config.TextX, this.Height - config.TextY);
                TextFormatFlags flags = TextFormatFlags.Default;

                if (config.IsOutlineEnabled && config.OutlineSize > 0)
                {
                    for (int x = -config.OutlineSize; x <= config.OutlineSize; x++)
                    {
                        for (int y = -config.OutlineSize; y <= config.OutlineSize; y++)
                        {
                            if (Math.Abs(x) + Math.Abs(y) > 0)
                            {
                                Rectangle outlineRect = new Rectangle(rect.X + x, rect.Y + y, rect.Width, rect.Height);
                                TextRenderer.DrawText(g, text, font, outlineRect, outlineColor, flags);
                            }
                        }
                    }
                }
                TextRenderer.DrawText(g, text, font, rect, foreColor, flags);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}