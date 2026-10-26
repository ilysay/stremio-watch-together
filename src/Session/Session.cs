namespace StremioWT
{
    partial class Session
    {
        readonly string server, name, stremioExe;
        readonly string clientId = Guid.NewGuid().ToString("N").Substring(0, 16);
        readonly Relay relay;
        Cdp cdp;
        volatile bool connected;
        volatile bool adminOn;
        const int PollTimeoutMs = 40000; // le relay répond au plus tard après 25 s
        List<string> members = new List<string>();
        static readonly string[] PageTypes = { "open", "play", "pause", "seek", "need-sync" };

        public string Room { get; }
        public bool IsHost { get; }
        public bool Connected => connected;
        public IReadOnlyList<string> Members => members;
        public bool AdminOn => adminOn;
        public event Action StateChanged;

        async Task<Dictionary<string, object>> PageState()
        {
            try { return cdp == null ? null : await cdp.Eval("window.__wt && window.__wt.state()") as Dictionary<string, object>; }
            catch { return null; }
        }

        public Session(string server, string room, string name, bool isHost, string stremioExe, Relay relay)
        {
            this.server = server; Room = room; this.name = name; IsHost = isHost; this.stremioExe = stremioExe; this.relay = relay;
            if (relay != null) relay.HostId = clientId;
        }

        string room => Room;
        bool isHost => IsHost;

        public void SetAdmin(bool on)
        {
            if (!isHost || adminOn == on) return;
            adminOn = on;
            relay.AdminOn = on;
            Console.WriteLine(on ? "Mode hébergeur admin activé : toi seul contrôles la lecture." : "Mode hébergeur admin coupé : tout le monde peut contrôler la lecture.");
            ToPage(new Dictionary<string, object> { ["type"] = "admin", ["on"] = on, ["isHost"] = true });
            _ = Post(new Dictionary<string, object> { ["type"] = "admin", ["on"] = on });
            StateChanged?.Invoke();
        }

        public static Session Current { get; private set; }

        public async Task Run()
        {
            Current = this;
            await AttachStremio(await StremioApp.EnsureRunning(stremioExe));
            Console.WriteLine("Connecté à Stremio.");
            await ReadMyShell();
            // Stremio ouvert dans un navigateur plutôt que dans son appli : on dit pourquoi.
            if (StremioApp.IsBrowser(stremioExe))
                ShowChat(new ChatMessage { From = "StremioWT", System = true, Text = StremioApp.WebReason() });
            _ = Task.Run(async () =>
            {
                try
                {
                    var history = await ReadHistory();
                    Journal.SyncSeries(history);
                    var fresh = Journal.NewFromHistory(history);
                    Program.Log($"historique Stremio : {fresh.Count} titre(s) vu(s) pas encore dans le journal");
                    if (fresh.Count > 0) HistoryFound?.Invoke(fresh);
                }
                catch (Exception e) { Program.Log("historique Stremio : " + e.Message); }
            });
            _ = Task.Run(WatchDownload);
            if (isHost)
            {
                _ = Task.Run(async () => { for (;;) { await Task.Delay(SyncEvery); if (connected) await SendSync(false); } });
                _ = Task.Run(async () => { for (;;) { await Task.Delay(1000); CheckWaitTimeouts(); } });
            }
            await Listen();
        }

        const int SyncEvery = 5000;

        void ToPage(Dictionary<string, object> msg)
        {
            var c = cdp;
            if (c != null) _ = c.Eval("window.__wt && window.__wt.onNode(" + Program.Json.Serialize(msg) + ")").ContinueWith(t => { var _ = t.Exception; });
        }

        void PushStatus()
        {
            ToPage(new Dictionary<string, object> { ["type"] = "status", ["room"] = room, ["connected"] = connected, ["members"] = members, ["admin"] = adminOn, ["host"] = isHost });
            StateChanged?.Invoke();
        }

        async Task Post(Dictionary<string, object> msg)
        {
            try
            {
                using (var content = new StringContent(Program.Json.Serialize(msg), Encoding.UTF8, "application/json"))
                using (var res = await Program.Http.PostAsync($"{server}/r/{room}/send?id={clientId}", content))
                {
                    if (!res.IsSuccessStatusCode) Program.Log($"envoi {msg["type"]} : HTTP {(int)res.StatusCode}");
                }
            }
            catch (Exception e) { Console.WriteLine("Envoi impossible : " + e.Message); }
        }

        async Task SendSync(bool force)
        {
            try
            {
                if (cdp == null) return;
                var st = await cdp.Eval("window.__wt && window.__wt.state()") as Dictionary<string, object>;
                if (st == null) return;
                st["type"] = "sync";
                st["force"] = force;
                await Post(st);
            }
            catch { }
        }

        void OnRelay(Dictionary<string, object> msg)
        {
            var type = msg.TryGetValue("type", out var t) ? t as string : null;
            if (type == "presence")
            {
                members = (msg["members"] as System.Collections.ArrayList)?.Cast<object>().Select(x => x as string).ToList() ?? new List<string>();
                PushStatus();
                if (isHost) Telemetry.Send("host", members.Count);
                var who = msg["who"] as string;
                bool join = (msg["event"] as string) == "join";
                if (who != name || !join) ToPage(new Dictionary<string, object> { ["type"] = "toast", ["text"] = L.F(join ? "{0} a rejoint le salon" : "{0} est parti", who) });
                Console.WriteLine($"{who} {(join ? "a rejoint" : "a quitté")} le salon.");
                // Un nouveau venu doit connaître le mode admin en cours.
                if (isHost && join && who != name && adminOn) _ = Post(new Dictionary<string, object> { ["type"] = "admin", ["on"] = true });
                // Le nouveau venu doit connaître notre version de Stremio ; celui qui part est oublié.
                if (join && who != name) AnnounceShell();
                if (!join) ForgetShell(who);
                if (!join && IsLoading(who)) OnBuffer(who, false);
                return;
            }
            if (type == "info")
            {
                OnShellInfo(msg.TryGetValue("from", out var inf) ? inf as string : null, msg.TryGetValue("shell", out var ins) ? ins as string : null);
                return;
            }
            if (type == "chat")
            {
                var chat = Chat.Sanitize(msg.TryGetValue("from", out var cf) ? cf as string ?? "?" : "?",
                    msg.TryGetValue("text", out var ct) ? ct : null, msg.TryGetValue("gif", out var cg) ? cg : null);
                if (chat != null) ShowChat(chat);
                return;
            }
            if (type == "rating")
            {
                if (msg.TryGetValue("imdb", out var ri) && ri is string rimdb && msg.TryGetValue("rating", out var rr))
                    AddRoomRating(rimdb, msg.TryGetValue("title", out var rt) ? rt as string ?? rimdb : rimdb,
                        msg.TryGetValue("from", out var rf) ? rf as string ?? "?" : "?", Math.Max(0.5, Math.Min(5, Convert.ToDouble(rr))));
                return;
            }
            if (type == "buffer")
            {
                OnBuffer(msg.TryGetValue("from", out var bf) ? bf as string ?? "?" : "?", msg.TryGetValue("on", out var bo) && bo is bool bb && bb);
                return;
            }
            if (type == "slow")
            {
                // Message venu d'un autre participant : valeurs bornées, et une alerte par personne et par minute au plus.
                double Num(string k, double max) { var d = msg.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v) : 0; return double.IsNaN(d) ? 0 : Math.Max(0, Math.Min(max, d)); }
                var who = msg.TryGetValue("from", out var sf) ? sf as string ?? "?" : "?";
                lock (slowShown)
                {
                    if (slowShown.TryGetValue(who, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
                    slowShown[who] = DateTime.UtcNow;
                }
                ShowSlow(who, Num("speed", 1e9), Num("need", 1e9), (int)Num("peers", 10000));
                return;
            }
            if (type == "wait")
            {
                ShowWait(((msg["names"] as System.Collections.ArrayList) ?? new System.Collections.ArrayList()).Cast<object>().Select(o => o as string));
                return;
            }
            if (type == "go") Console.WriteLine("Tout le monde est prêt : reprise ensemble.");
            if (type == "admin")
            {
                bool on = msg.TryGetValue("on", out var o) && o is bool ob && ob;
                if (isHost || on == adminOn) return;
                adminOn = on;
                Console.WriteLine(on ? "L'hôte a activé le mode admin : seul l'hôte contrôle la lecture." : "L'hôte a coupé le mode admin : tout le monde peut contrôler la lecture.");
                ToPage(new Dictionary<string, object> { ["type"] = "admin", ["on"] = on, ["isHost"] = false });
                PushStatus();
                return;
            }
            if (type == "need-sync") { Program.Log($"demande de synchro de {msg["from"]}"); if (isHost) _ = SendSync(true); return; }
            if (type != "sync" || (msg.TryGetValue("force", out var f) && f is bool fb && fb))
                Program.Log($"reçu {type} de {(msg.TryGetValue("from", out var fr) ? fr : "?")}" + (msg.TryGetValue("time", out var tm) ? $" à {Convert.ToDouble(tm) / 1000:0}s" : ""));
            msg["receivedAt"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            ToPage(msg);
        }

        public DateTime? ConnectedSince { get; private set; }
        int failedJoins, endReported;
        public Task ReportEnd() =>
            ConnectedSince == null || Interlocked.Exchange(ref endReported, 1) == 1 ? Task.CompletedTask : Telemetry.SessionEnded(DateTime.UtcNow - ConnectedSince.Value);

        async Task Listen()
        {
            bool everConnected = false;
            for (;;)
            {
                // Long polling : « quoi de neuf ? » — le relay répond dès qu'il y a un message,
                // ou au bout de 25 s sans rien. Des réponses complètes et courtes passent
                // par tous les tunnels (les flux SSE ouverts étaient bloqués par Cloudflare).
                try
                {
                    var url = $"{server}/r/{room}/poll?id={clientId}&name={Uri.EscapeDataString(name)}&v={Program.ProtocolVersion}";
                    List<Dictionary<string, object>> messages;
                    using (var cts = new CancellationTokenSource(PollTimeoutMs))
                    using (var res = await Program.Http.GetAsync(url, cts.Token))
                    {
                        if (res.StatusCode == HttpStatusCode.Conflict)
                        {
                            var hostVersion = (await res.Content.ReadAsStringAsync()).Trim();
                            await Telemetry.Send("error", detail: "version");
                            Program.Fail(L.F("L'hôte utilise StremioWT {0}, toi la version {1}.\n\nIl faut la même version des deux côtés : demande-lui son StremioWT.exe (ou envoie-lui le tien).", hostVersion, Program.ProtocolVersion) + "\n\n" + Updates.DownloadUrl);
                            return;
                        }
                        if (res.StatusCode == HttpStatusCode.Forbidden && (await res.Content.ReadAsStringAsync()).Contains("plein"))
                        {
                            await Telemetry.Send("error", detail: "room-full");
                            Program.Fail(L.T("Ce salon est plein (20 personnes maximum)."));
                            return;
                        }
                        res.EnsureSuccessStatusCode();
                        messages = Program.Json.Deserialize<List<Dictionary<string, object>>>(await res.Content.ReadAsStringAsync()) ?? new List<Dictionary<string, object>>();
                    }
                    if (!connected)
                    {
                        connected = true;
                        // « Salon rejoint » compté ici, une fois vraiment dans le salon (pas au clic sur Rejoindre).
                        if (ConnectedSince == null && !isHost) _ = Telemetry.Send("join");
                        if (ConnectedSince == null) ConnectedSince = DateTime.UtcNow;
                        AnnounceShell();
                        Console.WriteLine(everConnected ? "Reconnecté au salon." : $"Connecté au salon {room}. Laisse cette fenêtre ouverte pendant le film (tu peux la réduire).");
                        everConnected = true;
                        PushStatus();
                        if (!isHost) _ = Task.Delay(300).ContinueWith(_ => Post(new Dictionary<string, object> { ["type"] = "need-sync" }));
                    }
                    foreach (var msg in messages)
                        try { OnRelay(msg); } catch (Exception e) { Program.Log("message ignoré : " + e.Message); }
                }
                catch (Exception e)
                {
                    Program.Log("salon : " + e.GetType().Name + " " + e.Message);
                    if (connected) Console.WriteLine("Connexion au salon perdue, reconnexion…");
                    else if (!everConnected)
                    {
                        Console.WriteLine("Impossible de joindre le salon (l'hôte est-il lancé ?), nouvel essai…");
                        if (++failedJoins == 6) Telemetry.Error("room-unreachable");
                    }
                    connected = false;
                    PushStatus();
                    await Task.Delay(1500);
                }
            }
        }
    }
}
