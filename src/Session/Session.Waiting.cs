namespace StremioWT
{
    partial class Session
    {
        const int MaxWaitMs = 30000;
        // Quelqu'un qui recharge sans arrêt (connexion trop lente) mettrait tout le monde en pause toutes les
        // quelques secondes : au-delà de RepeatLimit chargements en une minute, on ne l'attend plus pendant RepeatPause.
        const int RepeatLimit = 4;
        static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1), RepeatPause = TimeSpan.FromMinutes(2);
        readonly Dictionary<string, List<DateTime>> bufferStarts = new Dictionary<string, List<DateTime>>();
        readonly Dictionary<string, DateTime> tooSlowUntil = new Dictionary<string, DateTime>();
        readonly object waitGate = new object();
        readonly SemaphoreSlim waitLock = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, DateTime> bufferingSince = new Dictionary<string, DateTime>();
        readonly HashSet<string> dontWait = new HashSet<string>();
        readonly HashSet<string> timedOut = new HashSet<string>();
        string[] waitNames = new string[0];
        bool waitingNow, pausedBeforeWait;

        public bool IsLoading(string who) { lock (waitGate) return bufferingSince.ContainsKey(who); }
        public bool IsNotWaited(string who) { lock (waitGate) return dontWait.Contains(who); }

        public void ToggleDontWait(string who)
        {
            if (!isHost || who == name) return;
            bool nowIgnored;
            lock (waitGate) { nowIgnored = dontWait.Add(who); if (!nowIgnored) dontWait.Remove(who); }
            Console.WriteLine(nowIgnored ? $"On n'attend plus {who} quand il charge." : $"On attend de nouveau {who} quand il charge.");
            _ = UpdateWait();
            StateChanged?.Invoke();
        }

        void OnBuffer(string who, bool on)
        {
            bool tooSlow = false;
            lock (waitGate)
            {
                if (on && !bufferingSince.ContainsKey(who))
                {
                    bufferingSince[who] = DateTime.UtcNow;
                    if (!bufferStarts.TryGetValue(who, out var starts)) bufferStarts[who] = starts = new List<DateTime>();
                    starts.Add(DateTime.UtcNow);
                    starts.RemoveAll(t => DateTime.UtcNow - t > RepeatWindow);
                    if (starts.Count >= RepeatLimit && !IsTooSlow(who)) { tooSlowUntil[who] = DateTime.UtcNow + RepeatPause; starts.Clear(); tooSlow = true; }
                }
                else if (!on) { bufferingSince.Remove(who); timedOut.Remove(who); }
            }
            if (tooSlow && isHost)
            {
                Console.WriteLine($"{who} recharge sans arrêt : on ne l'attend plus pendant 2 min.");
                ToPage(new Dictionary<string, object> { ["type"] = "toast", ["text"] = L.F("{0} charge depuis trop longtemps, on continue sans lui", who) });
            }
            Program.Log($"{who} {(on ? "charge" : "a fini de charger")}");
            if (isHost) _ = UpdateWait();
            StateChanged?.Invoke();
        }

        // Hôte : met tout le monde en pause tant qu'un participant attendu charge, puis relance tout le monde ensemble.
        async Task UpdateWait()
        {
            await waitLock.WaitAsync();
            try
            {
                string[] active;
                lock (waitGate) active = bufferingSince.Keys.Where(n => !dontWait.Contains(n) && !timedOut.Contains(n) && !IsTooSlow(n)).OrderBy(n => n).ToArray();
                if (active.Length > 0)
                {
                    if (!waitingNow)
                    {
                        var st = await PageState();
                        pausedBeforeWait = st != null && st.TryGetValue("paused", out var p) && p is bool pb && pb;
                        waitingNow = true;
                    }
                    if (!active.SequenceEqual(waitNames))
                    {
                        waitNames = active;
                        var wait = new Dictionary<string, object> { ["type"] = "wait", ["names"] = active };
                        await Post(wait);
                        ShowWait(active);
                    }
                }
                else if (waitingNow)
                {
                    waitingNow = false;
                    waitNames = new string[0];
                    var st = await PageState();
                    var go = new Dictionary<string, object> { ["type"] = "go", ["paused"] = pausedBeforeWait };
                    if (st != null) { go["time"] = st["time"]; go["hash"] = st["hash"]; }
                    Console.WriteLine("Tout le monde est prêt : reprise ensemble.");
                    await Post(go);
                    go["receivedAt"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                    ToPage(go);
                }
            }
            catch (Exception e) { Program.Log("attente : " + e.Message); }
            finally { waitLock.Release(); }
        }

        // Affiche l'attente chez soi : « toi » à la place de son propre pseudo, et pas de pause si c'est nous qu'on attend.
        void ShowWait(IEnumerable<string> names)
        {
            var shown = names.Select(n => n == name ? "toi" : n).ToArray();
            bool mine = shown.Contains("toi");
            Console.WriteLine(mine && shown.Length == 1 ? "Les autres t'attendent (chargement)…" : $"En attente de {string.Join(", ", shown)} (chargement)…");
            ToPage(new Dictionary<string, object> { ["type"] = "wait", ["names"] = shown, ["mine"] = mine });
        }

        bool IsTooSlow(string who) => tooSlowUntil.TryGetValue(who, out var until) && DateTime.UtcNow < until;

        void CheckWaitTimeouts()
        {
            var expired = new List<string>();
            lock (waitGate)
                foreach (var kv in bufferingSince)
                    if (!timedOut.Contains(kv.Key) && !dontWait.Contains(kv.Key) && (DateTime.UtcNow - kv.Value).TotalMilliseconds > MaxWaitMs)
                    { timedOut.Add(kv.Key); expired.Add(kv.Key); }
            if (expired.Count == 0) return;
            foreach (var who in expired)
            {
                Console.WriteLine($"{who} charge depuis plus de 30 s : on ne l'attend plus.");
                ToPage(new Dictionary<string, object> { ["type"] = "toast", ["text"] = L.F("{0} charge depuis trop longtemps, on continue sans lui", who) });
            }
            _ = UpdateWait();
        }
    }
}
