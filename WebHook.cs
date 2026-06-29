using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace triclapclap.WebHook
{
    public class OverlayServer : IDisposable
    {
        private struct WsPayload
        {
            public byte[] Buffer;
            public int Length;
        }

        private HttpListener? listener;
        private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> sockets = new ConcurrentDictionary<WebSocket, SemaphoreSlim>();
        private readonly BlockingCollection<WsPayload> queue = new BlockingCollection<WsPayload>(new ConcurrentQueue<WsPayload>());
        private Thread? networkThread;
        private readonly MemoryStream ms = new MemoryStream(256);
        private readonly Utf8JsonWriter writer;
        private bool isDisposed = false;

        public OverlayServer()
        {
            writer = new Utf8JsonWriter(ms, new JsonWriterOptions { SkipValidation = true });
        }

        public void Start(int port)
        {
            Stop();
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                listener.BeginGetContext(OnContextReceived, null);

                networkThread = new Thread(BroadcastLoop) { IsBackground = true, Priority = ThreadPriority.Normal };
                networkThread.Start();
            }
            catch { }
        }

        public void Broadcast(long hits, int cps, string frameName, bool playSound)
        {
            if (sockets.IsEmpty || isDisposed) return;

            ms.Position = 0;
            writer.Reset();
            writer.WriteStartObject();
            writer.WriteNumber("totalHits", hits);
            writer.WriteNumber("cps", cps);
            writer.WriteString("frame", frameName);
            writer.WriteBoolean("playSound", playSound);
            writer.WriteEndObject();
            writer.Flush();

            int length = (int)ms.Position;
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(ms.GetBuffer(), 0, rented, 0, length);
            queue.Add(new WsPayload { Buffer = rented, Length = length });
        }

        private async void BroadcastLoop()
        {
            try
            {
                foreach (var payload in queue.GetConsumingEnumerable())
                {
                    if (!sockets.IsEmpty)
                    {
                        var segment = new ArraySegment<byte>(payload.Buffer, 0, payload.Length);
                        foreach (var kvp in sockets)
                        {
                            WebSocket ws = kvp.Key;
                            SemaphoreSlim sem = kvp.Value;

                            if (ws.State == WebSocketState.Open && await sem.WaitAsync(0))
                            {
                                try { await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None); }
                                catch { }
                                finally { sem.Release(); }
                            }
                        }
                    }
                    ArrayPool<byte>.Shared.Return(payload.Buffer);
                }
            }
            catch { }
        }

        private async void OnContextReceived(IAsyncResult ar)
        {
            if (listener == null || !listener.IsListening) return;
            try
            {
                var context = listener.EndGetContext(ar);
                listener.BeginGetContext(OnContextReceived, null);

                if (context.Request.IsWebSocketRequest)
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    WebSocket ws = wsContext.WebSocket;
                    sockets.TryAdd(ws, new SemaphoreSlim(1, 1));
                    _ = HandleClient(ws);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch { }
        }

        private async Task HandleClient(WebSocket ws)
        {
            byte[] buffer = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                }
            }
            catch { }
            finally
            {
                if (sockets.TryRemove(ws, out var sem)) sem.Dispose();
                ws.Dispose();
            }
        }

        public void Stop()
        {
            listener?.Stop();
            listener?.Close();
            listener = null;

            foreach (var ws in sockets.Keys) ws.Dispose();
            foreach (var sem in sockets.Values) sem.Dispose();
            sockets.Clear();
        }

        public void Dispose()
        {
            isDisposed = true;
            queue.CompleteAdding();
            Stop();
            writer.Dispose();
            ms.Dispose();
            queue.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}