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
        bool waitingNow, pausedBeforeWait;

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

        void OnBuffer(string who, bool on)
        {
            lock (waitGate)
            {
                if (on && !bufferingSince.ContainsKey(who)) bufferingSince[who] = DateTime.UtcNow;
                else if (!on) { bufferingSince.Remove(who); opening.Remove(who); }
            }
            Program.Log($"{who} {(on ? "charge" : "a fini de charger")}");
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
                lock (waitGate) active = bufferingSince.Keys.Union(opening).Where(n => !dontWait.Contains(n)).Distinct().OrderBy(n => n).ToArray();
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
    }
}
