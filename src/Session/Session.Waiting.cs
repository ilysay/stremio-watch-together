namespace StremioWT
{
    partial class Session
    {
        // On attend toujours ceux qui chargent, aussi longtemps qu'il le faut : l'appli ne laisse jamais quelqu'un
        // en arrière d'elle-même. Seul l'hôte peut choisir de ne plus attendre une personne (clic sur son pseudo).
        readonly object waitGate = new object();
        readonly SemaphoreSlim waitLock = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, DateTime> bufferingSince = new Dictionary<string, DateTime>();
        readonly HashSet<string> dontWait = new HashSet<string>();
        // Nouvel épisode : ceux qui ne l'ont pas encore chargé. Sans ça, celui qui l'ouvre démarrait seul, et les autres,
        // prêts quelques secondes plus tard, devaient sauter à sa position : leur torrent chargeait deux fois.
        readonly HashSet<string> opening = new HashSet<string>();
        string openedKey;
        string[] waitNames = new string[0];
        string waitShown = "";
        bool waitingNow, pausedBeforeWait;
        // Réserve de vidéo faite par ceux qu'on attend (0 à 100), affichée aux autres.
        readonly Dictionary<string, int> bufferProgress = new Dictionary<string, int>();
        // Position du dernier qui a fini de charger, en pause sur sa réserve : tout le monde repart de là.
        // Repartir de la position de l'hôte le faisait sauter en avant, hors de ce qu'il avait chargé : il rechargeait aussitôt.
        double? resumeAt;

        public bool IsLoading(string who) { lock (waitGate) return bufferingSince.ContainsKey(who) || opening.Contains(who); }
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

        void OnBuffer(string who, bool on, Dictionary<string, object> msg = null)
        {
            object v = null;
            bool known;
            lock (waitGate)
            {
                known = bufferingSince.ContainsKey(who);
                if (on && !known) bufferingSince[who] = DateTime.UtcNow;
                if (on && msg != null && msg.TryGetValue("progress", out v) && v != null)
                    bufferProgress[who] = (int)Math.Max(0, Math.Min(100, Convert.ToDouble(v)));
                if (!on)
                {
                    bool waited = known && waitingNow && !dontWait.Contains(who);
                    bufferingSince.Remove(who); opening.Remove(who); bufferProgress.Remove(who);
                    if (waited && msg != null && msg.TryGetValue("time", out v) && v != null)
                    {
                        var t = Convert.ToDouble(v);
                        if (!double.IsNaN(t) && !double.IsInfinity(t) && t >= 0) resumeAt = t;
                    }
                }
            }
            // Simple progression d'un chargement déjà connu : pas de ligne de journal à chaque fois.
            if (!(on && known)) Program.Log($"{who} {(on ? "charge" : "a fini de charger")}" + (!on && msg != null && msg.ContainsKey("time") ? $" (réserve faite, à {Convert.ToDouble(msg["time"]) / 1000:0}s)" : ""));
            if (isHost) _ = UpdateWait();
            StateChanged?.Invoke();
        }

        // Hôte : quelqu'un ouvre un épisode. Tous les autres sont attendus jusqu'à ce que leur lecteur soit prêt.
        // Le même épisode avec une autre source (choisie à la main, Real-Debrid…) ne relance pas l'attente des autres.
        void OnOpen(string who, Dictionary<string, object> msg)
        {
            var hash = msg.TryGetValue("hash", out var h) ? h as string : null;
            if (!isHost || hash == null || !hash.StartsWith("#/player/")) return;
            var key = string.Join("/", hash.Split('/').Take(5));
            lock (waitGate)
            {
                if (key == openedKey) opening.Remove(who);
                else
                {
                    openedKey = key;
                    opening.Clear();
                    foreach (var m in members) if (m != who) opening.Add(m);
                }
            }
            _ = UpdateWait();
            StateChanged?.Invoke();
        }

        // Lecteur prêt sur l'épisode ouvert par un autre (la page demande alors la position de l'hôte).
        void OnReady(string who)
        {
            bool was;
            lock (waitGate) was = opening.Remove(who);
            if (!was) return;
            Program.Log($"{who} a chargé l'épisode");
            _ = UpdateWait();
            StateChanged?.Invoke();
        }

        // Hôte : met tout le monde en pause tant qu'un participant attendu charge, puis relance tout le monde ensemble.
        async Task UpdateWait()
        {
            await waitLock.WaitAsync();
            try
            {
                string[] active;
                Dictionary<string, object> progress;
                lock (waitGate)
                {
                    active = bufferingSince.Keys.Union(opening).Where(n => !dontWait.Contains(n)).Distinct().OrderBy(n => n).ToArray();
                    progress = active.Where(bufferProgress.ContainsKey).ToDictionary(n => n, n => (object)bufferProgress[n]);
                }
                if (active.Length > 0)
                {
                    if (!waitingNow)
                    {
                        var st = await PageState();
                        pausedBeforeWait = st != null && st.TryGetValue("paused", out var p) && p is bool pb && pb;
                        lock (waitGate) { waitingNow = true; resumeAt = null; }
                    }
                    var shown = string.Join(",", active) + "|" + string.Join(",", progress.Select(kv => kv.Key + "=" + kv.Value));
                    if (shown != waitShown)
                    {
                        waitNames = active;
                        waitShown = shown;
                        var wait = new Dictionary<string, object> { ["type"] = "wait", ["names"] = active, ["progress"] = progress };
                        await Post(wait);
                        ShowWait(active, progress);
                    }
                }
                else if (waitingNow)
                {
                    double? from;
                    lock (waitGate) { waitingNow = false; from = resumeAt; resumeAt = null; }
                    waitNames = new string[0];
                    waitShown = "";
                    var st = await PageState();
                    var go = new Dictionary<string, object> { ["type"] = "go", ["paused"] = pausedBeforeWait };
                    if (st != null) { go["time"] = st["time"]; go["hash"] = st["hash"]; }
                    // Tout le monde est en pause sur ce qu'il a chargé : on se cale au plus près sur celui qu'on attendait.
                    if (from != null) { go["time"] = from.Value; go["exact"] = true; }
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
        void ShowWait(IEnumerable<string> names, IDictionary<string, object> progress = null)
        {
            var shown = names.Select(n => n == name ? "toi" : n).ToArray();
            bool mine = shown.Contains("toi");
            var pct = new Dictionary<string, object>();
            if (progress != null)
                foreach (var kv in progress)
                    if (kv.Value != null) pct[kv.Key == name ? "toi" : kv.Key] = (int)Math.Max(0, Math.Min(100, Convert.ToDouble(kv.Value)));
            if (pct.Count == 0)
                Console.WriteLine(mine && shown.Length == 1 ? "Les autres t'attendent (chargement)…" : $"En attente de {string.Join(", ", shown)} (chargement)…");
            ToPage(new Dictionary<string, object> { ["type"] = "wait", ["names"] = shown, ["mine"] = mine, ["progress"] = pct });
        }
    }
}
