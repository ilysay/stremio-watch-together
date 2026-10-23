using static StremioWT.Program;

namespace StremioWT
{
    // Chaque participant a une boîte aux lettres. GET /r/SALON/poll attend (25 s max) qu'elle
    // contienne quelque chose puis la vide ; POST /r/SALON/send dépose un message chez les autres.
    class Relay
    {
        class Client
        {
            public string Id, Name;
            public readonly List<string> Queue = new List<string>();
            public TaskCompletionSource<bool> Waiter;
            public bool Polling;
            public DateTime LastSeen = DateTime.UtcNow;
            public readonly Queue<DateTime> ChatTimes = new Queue<DateTime>();
        }

        public volatile bool AdminOn;
        public string HostId;
        static readonly string[] ControlTypes = { "open", "play", "pause", "seek" };
        static readonly string[] HostOnlyTypes = { "admin", "wait", "go" };

        const int MaxClientsPerRoom = 20;
        const int MaxQueue = 200;
        static readonly TimeSpan PollHold = TimeSpan.FromSeconds(25);
        static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(45);
        readonly Dictionary<string, List<Client>> rooms = new Dictionary<string, List<Client>>();
        readonly object gate = new object();

        public int Start(int port, bool lan)
        {
            // Sans --local, le relay n'écoute que sur ce PC : seul cloudflared y accède (pas d'alerte pare-feu).
            var addr = lan ? IPAddress.Any : IPAddress.Loopback;
            TcpListener l;
            try { l = new TcpListener(addr, port); l.Start(); }
            catch (SocketException) { l = new TcpListener(addr, 0); l.Start(); }
            NoInherit(l.Server);
            _ = Task.Run(async () => { for (;;) { var c = await l.AcceptTcpClientAsync(); NoInherit(c.Client); _ = Task.Run(() => Handle(c)); } });
            _ = Task.Run(async () => { for (;;) { await Task.Delay(5000); Reap(); } });
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }

        // Sans ça, les programmes lancés ensuite (Stremio, cloudflared) héritent du port d'écoute
        // et le gardent ouvert après la fermeture de StremioWT : la session suivante tombe dessus et reste bloquée.
        [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
        static void NoInherit(Socket s) { if (Program.IsWindows) try { SetHandleInformation(s.Handle, 1 /* HANDLE_FLAG_INHERIT */, 0); } catch { } }

        // Vérifie que c'est bien ce relay qui répond sur ce port (et pas un reste d'une ancienne session).
        public static async Task<bool> Answers(int port)
        {
            try
            {
                using (var cts = new CancellationTokenSource(3000))
                    return (await Program.Http.GetStringAsync($"http://127.0.0.1:{port}/").WithCancel(cts.Token)).Contains("Stremio Watch Together relay");
            }
            catch { return false; }
        }

        static async Task<string> ReadHead(Stream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 16384)
            {
                if (await s.ReadAsync(one, 0, 1) == 0) return null;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\r' && sb[sb.Length - 3] == '\n' && sb[sb.Length - 4] == '\r') return sb.ToString();
            }
            return null;
        }

        static async Task Respond(Stream s, string status, string body = "", string contentType = "text/plain; charset=utf-8")
        {
            var b = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length);
            await s.WriteAsync(b, 0, b.Length);
        }

        async Task Handle(TcpClient tcp)
        {
            tcp.NoDelay = true;
            var s = tcp.GetStream();
            try
            {
                var head = await ReadHead(s);
                if (head == null) return;
                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0], target = parts[1];
                var headers = lines.Skip(1).Where(x => x.Contains(':')).GroupBy(x => x.Substring(0, x.IndexOf(':')).Trim().ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.First().Substring(g.First().IndexOf(':') + 1).Trim(), StringComparer.Ordinal);
                var qi = target.IndexOf('?');
                var path = qi >= 0 ? target.Substring(0, qi) : target;
                var query = (qi >= 0 ? target.Substring(qi + 1) : "").Split('&').Where(x => x.Contains('='))
                    .GroupBy(x => x.Substring(0, x.IndexOf('='))).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First().Substring(g.First().IndexOf('=') + 1).Replace('+', ' ')));
                var m = Regex.Match(path, @"^/r/([^/]+)/(poll|send)$");
                if (!m.Success) { await Respond(s, path == "/" ? "200 OK" : "404 Not Found", "Stremio Watch Together relay\n"); return; }
                var room = m.Groups[1].Value.ToUpperInvariant();
                query.TryGetValue("id", out var id);
                if (!Regex.IsMatch(room, "^[A-Z0-9]{3,16}$") || string.IsNullOrEmpty(id) || id.Length > 64) { await Respond(s, "400 Bad Request", "bad room or id"); return; }

                if (m.Groups[2].Value == "poll" && method == "GET")
                {
                    // Même version obligatoire (les anciennes versions n'envoient rien : refusées aussi).
                    query.TryGetValue("v", out var clientVersion);
                    if (clientVersion != Program.ProtocolVersion) { await Respond(s, "409 Conflict", Program.ProtocolVersion); return; }
                    query.TryGetValue("name", out var name);
                    name = string.IsNullOrWhiteSpace(name) ? "Invité" : (name.Length > 32 ? name.Substring(0, 32) : name);
                    Client c;
                    bool joined = false, full = false;
                    TaskCompletionSource<bool> waiter = null;
                    lock (gate)
                    {
                        if (!rooms.TryGetValue(room, out var list)) rooms[room] = list = new List<Client>();
                        c = list.FirstOrDefault(x => x.Id == id);
                        if (c == null)
                        {
                            if (list.Count >= MaxClientsPerRoom) full = true;
                            else { c = new Client { Id = id, Name = name }; list.Add(c); joined = true; }
                        }
                        if (c != null)
                        {
                            c.LastSeen = DateTime.UtcNow;
                            c.Waiter?.TrySetResult(true);
                            if (c.Queue.Count == 0 && !joined)
                            {
                                waiter = c.Waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                c.Polling = true;
                            }
                        }
                    }
                    if (full) { await Respond(s, "403 Forbidden", "salon plein"); return; }
                    if (joined) { Program.Log($"relay : {name} entre dans le salon {room}"); Broadcast(room, Presence(room, "join", name), null); }
                    if (waiter != null) await Task.WhenAny(waiter.Task, Task.Delay(PollHold));
                    string body;
                    lock (gate)
                    {
                        if (waiter != null && c.Waiter != waiter) body = "[]";
                        else
                        {
                            body = "[" + string.Join(",", c.Queue) + "]";
                            c.Queue.Clear();
                            c.Waiter = null;
                            c.Polling = false;
                        }
                        c.LastSeen = DateTime.UtcNow;
                    }
                    await Respond(s, "200 OK", body, "application/json; charset=utf-8");
                    return;
                }

                if (m.Groups[2].Value == "send" && method == "POST")
                {
                    headers.TryGetValue("content-length", out var lenStr);
                    int.TryParse(lenStr, out var len);
                    if (len <= 0 || len > 8192) { await Respond(s, "400 Bad Request", "bad length"); return; }
                    var buf = new byte[len];
                    for (int read = 0; read < len;)
                    {
                        int r = await s.ReadAsync(buf, read, len - read);
                        if (r == 0) return;
                        read += r;
                    }
                    Client sender;
                    lock (gate)
                    {
                        sender = rooms.TryGetValue(room, out var list) ? list.FirstOrDefault(c => c.Id == id) : null;
                        if (sender != null) sender.LastSeen = DateTime.UtcNow;
                    }
                    if (sender == null) { await Respond(s, "403 Forbidden", "not in room"); return; }
                    Dictionary<string, object> msg;
                    try { msg = Program.Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(buf)); }
                    catch { await Respond(s, "400 Bad Request", "bad json"); return; }
                    if (msg == null) { await Respond(s, "400 Bad Request", "bad msg"); return; }
                    var msgType = msg.TryGetValue("type", out var mt) ? mt as string : null;
                    if (id != HostId && (HostOnlyTypes.Contains(msgType) || (AdminOn && ControlTypes.Contains(msgType))))
                    {
                        Program.Log($"relay : {msgType} de {sender.Name} ignoré (mode hébergeur admin)");
                        await Respond(s, "204 No Content");
                        return;
                    }
                    if (msgType == "chat")
                    {
                        bool flood;
                        lock (gate)
                        {
                            var nowUtc = DateTime.UtcNow;
                            while (sender.ChatTimes.Count > 0 && (nowUtc - sender.ChatTimes.Peek()).TotalSeconds > 10) sender.ChatTimes.Dequeue();
                            flood = sender.ChatTimes.Count >= 5;
                            if (!flood) sender.ChatTimes.Enqueue(nowUtc);
                        }
                        if (flood) { Program.Log($"relay : chat de {sender.Name} ignoré (trop de messages)"); await Respond(s, "204 No Content"); return; }
                    }
                    msg["from"] = sender.Name;
                    Broadcast(room, msg, id);
                    await Respond(s, "204 No Content");
                    return;
                }
                await Respond(s, "405 Method Not Allowed");
            }
            catch { }
            finally { try { tcp.Close(); } catch { } }
        }

        Dictionary<string, object> Presence(string room, string ev, string who)
        {
            List<string> names;
            lock (gate) names = rooms.TryGetValue(room, out var list) ? list.Select(c => c.Name).ToList() : new List<string>();
            return new Dictionary<string, object> { ["type"] = "presence", ["event"] = ev, ["who"] = who, ["members"] = names };
        }

        void Broadcast(string room, Dictionary<string, object> msg, string exceptId)
        {
            var json = Program.Json.Serialize(msg);
            lock (gate)
            {
                if (!rooms.TryGetValue(room, out var list)) return;
                foreach (var c in list)
                {
                    if (c.Id == exceptId) continue;
                    if (c.Queue.Count < MaxQueue) c.Queue.Add(json);
                    c.Waiter?.TrySetResult(true);
                }
            }
        }

        void Reap()
        {
            var gone = new List<(string room, string name)>();
            lock (gate)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in rooms.ToList())
                {
                    foreach (var c in kv.Value.Where(c => !c.Polling && now - c.LastSeen > GoneAfter).ToList())
                    {
                        kv.Value.Remove(c);
                        gone.Add((kv.Key, c.Name));
                    }
                    if (kv.Value.Count == 0) rooms.Remove(kv.Key);
                }
            }
            foreach (var (room, name) in gone)
            {
                Program.Log($"relay : {name} retiré du salon {room} (plus de nouvelles)");
                Broadcast(room, Presence(room, "leave", name), null);
            }
        }
    }
}
