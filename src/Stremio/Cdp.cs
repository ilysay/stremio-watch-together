namespace StremioWT
{
    class Cdp
    {
        readonly ClientWebSocket ws = new ClientWebSocket();
        readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        int nextId;
        public event Action<string> BindingCalled;
        public event Action Closed;

        public async Task Connect(string url)
        {
            // Pas de « ping » WebSocket (envoyé par défaut toutes les 30 s) : le moteur de Stremio 4 (Qt) ne le comprend
            // pas et coupait la liaison toutes les 30 s. La page perdait alors les messages envoyés pendant la reconnexion.
            ws.Options.KeepAliveInterval = TimeSpan.Zero;
            await ws.ConnectAsync(new Uri(url), CancellationToken.None);
            _ = Task.Run(ReceiveLoop);
        }

        public void Close()
        {
            try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); } catch { }
        }

        public void Send(string method)
        {
            var bytes = Encoding.UTF8.GetBytes(Program.Json.Serialize(new Dictionary<string, object> { ["id"] = Interlocked.Increment(ref nextId), ["method"] = method, ["params"] = new Dictionary<string, object>() }));
            ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(1000);
        }

        async Task ReceiveLoop()
        {
            var buf = new byte[1 << 16];
            var ms = new MemoryStream();
            try
            {
                for (;;)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    try { Handle(Program.Json.Deserialize<Dictionary<string, object>>(text)); } catch { }
                }
            }
            catch { }
            Closed?.Invoke();
        }

        void Handle(Dictionary<string, object> m)
        {
            if (m.TryGetValue("id", out var idObj) && idObj is int id && pending.TryRemove(id, out var tcs))
            {
                if (m.TryGetValue("error", out var err)) tcs.TrySetException(new Exception(Program.Json.Serialize(err)));
                else tcs.TrySetResult(m.TryGetValue("result", out var res) ? res as Dictionary<string, object> : null);
            }
            else if ((m.TryGetValue("method", out var method) ? method as string : null) == "Runtime.bindingCalled")
            {
                var p = m["params"] as Dictionary<string, object>;
                if ((p["name"] as string) == "__wtSend") BindingCalled?.Invoke(p["payload"] as string);
            }
        }

        public async Task<Dictionary<string, object>> Call(string method, Dictionary<string, object> parameters = null)
        {
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            var bytes = Encoding.UTF8.GetBytes(Program.Json.Serialize(new Dictionary<string, object> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new Dictionary<string, object>() }));
            await sendLock.WaitAsync();
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { sendLock.Release(); }
            if (await Task.WhenAny(tcs.Task, Task.Delay(15000)) != tcs.Task) { pending.TryRemove(id, out _); throw new TimeoutException(method); }
            return await tcs.Task;
        }

        public async Task<object> Eval(string expression)
        {
            var r = await Call("Runtime.evaluate", new Dictionary<string, object> { ["expression"] = expression, ["returnByValue"] = true });
            return (r?["result"] as Dictionary<string, object>)?.TryGetValue("value", out var v) == true ? v : null;
        }
    }
}
