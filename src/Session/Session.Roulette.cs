namespace StremioWT
{
    // Roulette : un film tiré au sort selon les goûts mélangés du salon. Seul l'hôte fait tourner la roue et mène le tirage :
    //   1. « roulette-ask » : chacun calcule ses goûts à partir de son historique Stremio et ne renvoie que la part de
    //      chaque genre (« roulette-taste »), jamais la liste de ce qu'il a regardé ;
    //   2. l'hôte mélange les goûts, prend des films dans le catalogue de Stremio et demande lesquels ont déjà été vus
    //      (« roulette-check » → « roulette-seen ») : chacun ne répond que pour ces quelques films ;
    //   3. « roulette-spin » : la même roue tourne chez tout le monde et s'arrête sur le même film (en battle royale :
    //      trois tirages, puis les finalistes sont éliminés un à un) ;
    //   4. vote express (« roulette-vote-open » / « roulette-vote » / « roulette-votes »), veto d'une personne par soirée
    //      (« roulette-veto » / « roulette-vetoed »), puis « roulette-open » : le film s'ouvre chez tout le monde.
    //      Un film refusé (vote ou veto) est écarté pour la soirée ; c'est l'hôte qui relance.
    // Les réactions (« roulette-react ») passent par le salon comme le chat, avec la même limite.
    partial class Session
    {
        const int TasteWaitMs = 8000, SeenWaitMs = 5000, ShortlistSize = 60, ReelSize = 12, BattlePool = 20, VoteSeconds = 12;
        static readonly string[] Reactions = { "🔥", "😂", "😱", "🤞", "😍", "💀", "👏", "🍿" };
        int rouletteRound, rouletteBusy;
        readonly object rouletteGate = new object();
        readonly Dictionary<string, TasteProfile> roomTastes = new Dictionary<string, TasteProfile>();
        readonly HashSet<string> seenAnswered = new HashSet<string>();
        readonly HashSet<string> seenByRoom = new HashSet<string>();
        readonly Queue<string> recentReel = new Queue<string>(); // films des derniers tirages, pas reproposés tout de suite
        readonly HashSet<string> refusedFilms = new HashSet<string>(); // refusés ce soir (veto, vote) : plus proposés
        readonly HashSet<string> vetoUsed = new HashSet<string>(); // une seule fois par personne et par soirée
        readonly Dictionary<string, bool> votes = new Dictionary<string, bool>();
        (string mode, bool battle, bool mystery) lastOptions = (RouletteMode.Classic, false, false);
        string winnerId, winnerName;
        int voteRound; // tirage dont le vote est ouvert (0 : aucun)
        TasteProfile myTaste;
        HashSet<string> myHistory = new HashSet<string>();
        DateTime myTasteAt;

        // Clic sur « Lancer la roulette » dans Stremio : seul l'hôte fait tourner la roue (le bouton n'apparaît que chez lui).
        void RequestRoulette(Dictionary<string, object> msg)
        {
            if (isHost) _ = Task.Run(() => RunRoulette(name, Options(msg)));
        }

        static (string mode, bool battle, bool mystery) Options(Dictionary<string, object> msg) =>
            (RouletteMode.Check(msg.TryGetValue("mode", out var m) ? m as string : null),
             msg.TryGetValue("battle", out var b) && b is bool bb && bb,
             msg.TryGetValue("mystery", out var y) && y is bool yb && yb);

        async Task<TasteProfile> MyTaste()
        {
            if (myTaste != null && DateTime.UtcNow - myTasteAt < TimeSpan.FromMinutes(10)) return myTaste;
            var history = await ReadHistory();
            var recent = Taste.Recent(history).ToList();
            var genres = await Taste.GenresFor(recent);
            myHistory = new HashSet<string>(history.Select(h => h.ImdbId));
            myTaste = Taste.Build(recent, genres, DateTime.Today);
            myTasteAt = DateTime.UtcNow;
            Program.Log($"roulette : goûts calculés sur {myTaste.Titles} titre(s) de l'historique");
            return myTaste;
        }

        async Task RunRoulette(string by, (string mode, bool battle, bool mystery) options)
        {
            if (Interlocked.Exchange(ref rouletteBusy, 1) == 1) return;
            try
            {
                int round;
                lock (rouletteGate)
                {
                    round = ++rouletteRound;
                    roomTastes.Clear(); seenAnswered.Clear(); seenByRoom.Clear(); votes.Clear();
                    voteRound = 0; lastOptions = options;
                }
                Program.Log($"roulette : lancée par {by} (mode {options.mode}{(options.battle ? ", battle royale" : "")}{(options.mystery ? ", mystère" : "")})");
                _ = Post(new Dictionary<string, object> { ["type"] = "roulette-ask", ["round"] = round, ["by"] = by });
                ToPage(new Dictionary<string, object> { ["type"] = "roulette-wait", ["by"] = by == name ? L.T("toi") : by });

                var mine = await MyTaste();
                var others = members.Where(m => m != name).ToList();
                // Goûts des autres : on attend tout le monde, 8 s au plus (une ancienne version ne répondrait jamais).
                await WaitFor(() => { lock (rouletteGate) return others.All(roomTastes.ContainsKey); }, TasteWaitMs);
                Dictionary<string, TasteProfile> byName;
                lock (rouletteGate) byName = new Dictionary<string, TasteProfile>(roomTastes) { [name] = mine };
                var answered = byName.Keys.Where(n => n != name).ToList();
                var (combined, avoided) = Taste.Merge(byName.Values);
                var rng = new Random(Guid.NewGuid().GetHashCode());
                // Les deux genres les plus partagés, et un troisième pris au hasard parmi les suivants : d'un tirage à l'autre,
                // la roue explore aussi les goûts secondaires du salon.
                var ordered = Taste.TopGenres(combined, 6);
                var top = ordered.Take(2).Concat(ordered.Skip(2).OrderBy(_ => rng.Next()).Take(1)).ToList();
                Program.Log($"roulette : {byName.Count} profil(s), genres communs : {string.Join(", ", top)}" + (avoided.Count > 0 ? $" ; écartés : {string.Join(", ", avoided)}" : ""));

                HashSet<string> excluded;
                lock (rouletteGate) excluded = new HashSet<string>(myHistory.Concat(refusedFilms));
                var ranked = Taste.Rank(await Taste.Candidates(top, rng, options.mode), combined, avoided, excluded, options.mode);
                // Thème sans aucun film (catalogue lent, tout déjà vu…) : on tire plutôt en classique que de ne rien proposer.
                if (ranked.Count == 0 && options.mode != RouletteMode.Classic)
                {
                    Program.Log($"roulette : aucun film pour le mode {options.mode}, tirage classique à la place");
                    options.mode = RouletteMode.Classic;
                    ranked = Taste.Rank(await Taste.Candidates(top, rng), combined, avoided, excluded);
                }
                if (ranked.Count == 0) throw new Exception("catalogue vide");
                var shortlist = ranked.Take(ShortlistSize).ToList();

                // Déjà vus par les autres : chacun répond seulement pour ces films.
                if (answered.Count > 0)
                {
                    _ = Post(new Dictionary<string, object> { ["type"] = "roulette-check", ["round"] = round, ["ids"] = shortlist.Select(f => f.Id).ToList() });
                    await WaitFor(() => { lock (rouletteGate) return answered.All(seenAnswered.Contains); }, SeenWaitMs);
                    lock (rouletteGate) { var unseen = shortlist.Where(f => !seenByRoom.Contains(f.Id)).ToList(); if (unseen.Count > 0) shortlist = unseen; }
                }

                HashSet<string> recent;
                lock (rouletteGate) recent = new HashSet<string>(recentReel);
                var items = Taste.SampleReel(shortlist, options.battle ? BattlePool : ReelSize, rng, recent);
                lock (rouletteGate) { foreach (var f in items) recentReel.Enqueue(f.Id); while (recentReel.Count > 48) recentReel.Dequeue(); }

                // Le ou les films sur lesquels la roue s'arrête, puis (battle royale) l'ordre d'élimination des finalistes.
                var picks = new List<int>();
                var left = items.ToList();
                for (int i = 0; i < (options.battle ? Math.Min(3, items.Count) : 1); i++)
                {
                    var p = left[Taste.PickWinner(left, rng)];
                    picks.Add(items.IndexOf(p));
                    left.Remove(p);
                }
                var eliminated = options.battle ? Taste.EliminationOrder(picks.Select(i => items[i]).ToList(), rng) : new List<int>();
                int final = options.battle ? picks[Enumerable.Range(0, picks.Count).First(i => !eliminated.Contains(i))] : picks[0];
                var winner = items[final];
                // Chaque tirage montre 12 affiches prises parmi les films, dont celle où il s'arrête.
                var reels = picks.Select(pick => items.Select((f, i) => i).Where(i => i != pick).OrderBy(_ => rng.Next()).Take(ReelSize - 1)
                    .Concat(new[] { pick }).OrderBy(_ => rng.Next()).ToList()).ToList();

                var compat = new List<Dictionary<string, object>>();
                var valid = byName.Where(kv => kv.Value.Titles >= Taste.MinTitles).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                for (int i = 0; i < valid.Count; i++)
                    for (int j = i + 1; j < valid.Count; j++)
                        compat.Add(new Dictionary<string, object> { ["a"] = valid[i].Key, ["b"] = valid[j].Key, ["pct"] = Taste.Compatibility(valid[i].Value, valid[j].Value) });

                List<string> vetoes;
                lock (rouletteGate) { winnerId = winner.Id; winnerName = winner.Name; vetoes = vetoUsed.ToList(); }
                var spin = new Dictionary<string, object>
                {
                    ["type"] = "roulette-spin",
                    ["round"] = round,
                    ["by"] = by,
                    ["mode"] = options.mode,
                    ["battle"] = options.battle,
                    ["mystery"] = options.mystery,
                    ["reels"] = reels,
                    ["picks"] = picks,
                    ["eliminated"] = eliminated,
                    ["winner"] = final,
                    ["common"] = options.mode == RouletteMode.Classic ? top : new List<string>(),
                    ["compat"] = compat.OrderByDescending(c => (int)c["pct"]).Take(4).ToList(),
                    ["fits"] = byName.Count > 1 ? Taste.BestFit(byName, winner) : null,
                    ["vetoUsed"] = vetoes,
                    ["items"] = items.Select(f => new Dictionary<string, object>
                    {
                        ["id"] = f.Id, ["name"] = f.Name.Length > 60 ? f.Name.Substring(0, 60) : f.Name, ["year"] = f.Year,
                        ["poster"] = f.Poster, ["genres"] = f.Genres.Take(3).ToList(), ["rating"] = Math.Round(f.Rating, 1), ["trailer"] = f.Trailer,
                    }).ToList(),
                };
                Program.Log($"roulette : {items.Count} film(s), gagnant {winner.Name} ({winner.Id})" + (options.battle ? $", finalistes {string.Join(", ", picks.Select(i => items[i].Name))}" : ""));
                await Post(spin);
                ToPage(ForMe(spin, true));
                // Statistiques anonymes : le thème et les options du tour, et la taille du salon. Rien sur les films ni les gens.
                _ = Telemetry.Send("roulette", byName.Count, options.mode + (options.battle ? "-battle" : "") + (options.mystery ? "-mystery" : ""));
                // Si la page de l'hôte ne signale pas la fin de l'animation (Stremio fermé…), le vote s'ouvre quand même.
                _ = Task.Delay(options.battle ? 60000 : 30000).ContinueWith(_ => OpenVote(round));
            }
            catch (Exception e)
            {
                Program.Log("roulette : " + e.Message);
                _ = Post(new Dictionary<string, object> { ["type"] = "roulette-error" });
                ToPage(new Dictionary<string, object> { ["type"] = "roulette-error" });
            }
            finally { rouletteBusy = 0; }
        }

        // Ce que la page de chacun reçoit en plus du tirage : est-ce l'hôte, reste-t-il son veto, ses statistiques de roulette.
        Dictionary<string, object> ForMe(Dictionary<string, object> spin, bool host)
        {
            var page = new Dictionary<string, object>(spin) { ["host"] = host };
            var used = ((spin.TryGetValue("vetoUsed", out var v) ? v as System.Collections.IEnumerable : null) ?? new string[0]).OfType<string>();
            page["vetoLeft"] = !used.Contains(name);
            var stats = RouletteStats.Load();
            page["stats"] = new Dictionary<string, object> { ["picks"] = stats.Picks, ["good"] = stats.Good, ["rated"] = stats.Good + stats.Bad };
            return page;
        }

        static async Task WaitFor(Func<bool> done, int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs && !done(); waited += 200) await Task.Delay(200);
        }

        // ---- Vote express et veto (décidés par l'hôte) ---------------------------------------------------------------

        void OpenVote(int round)
        {
            lock (rouletteGate) { if (round != rouletteRound || voteRound == round || voteRound == -round || winnerId == null) return; voteRound = round; votes.Clear(); }
            var open = new Dictionary<string, object> { ["type"] = "roulette-vote-open", ["round"] = round, ["seconds"] = VoteSeconds };
            _ = Post(open);
            ToPage(open);
            _ = Task.Delay(VoteSeconds * 1000 + 500).ContinueWith(_ => CloseVote(round));
        }

        void AddVote(string who, int round, bool yes)
        {
            Dictionary<string, object> tally;
            bool everyone;
            lock (rouletteGate)
            {
                if (round != voteRound || round != rouletteRound) return;
                votes[who] = yes;
                tally = Tally(round, null);
                everyone = votes.Count >= Math.Max(1, members.Count);
            }
            _ = Post(tally);
            ToPage(tally);
            // Tout le monde a voté : inutile d'attendre la fin du compte à rebours.
            if (everyone) _ = Task.Delay(800).ContinueWith(_ => CloseVote(round));
        }

        Dictionary<string, object> Tally(int round, string result) => new Dictionary<string, object>
        {
            ["type"] = "roulette-votes", ["round"] = round, ["yes"] = votes.Count(v => v.Value), ["no"] = votes.Count(v => !v.Value), ["result"] = result,
        };

        void CloseVote(int round)
        {
            Dictionary<string, object> tally;
            string result, id;
            lock (rouletteGate)
            {
                if (round != voteRound || round != rouletteRound) return;
                voteRound = -round; // vote terminé pour ce tirage
                int yes = votes.Count(v => v.Value), no = votes.Count(v => !v.Value);
                result = yes > no ? "yes" : no > yes ? "no" : "none";
                tally = Tally(round, result);
                id = winnerId;
                if (result == "no") refusedFilms.Add(id);
            }
            _ = Post(tally);
            ToPage(tally);
            Program.Log($"roulette : vote terminé ({result})");
            if (result == "yes") _ = Task.Delay(1500).ContinueWith(_ => OpenRouletteFilm(id));
            // Vote « non » : le film est écarté, c'est l'hôte qui relance quand il veut.
        }

        void Veto(string who, int round)
        {
            lock (rouletteGate)
            {
                if (round != rouletteRound || winnerId == null || !vetoUsed.Add(who)) return;
                refusedFilms.Add(winnerId);
                voteRound = -round;
            }
            var vetoed = new Dictionary<string, object> { ["type"] = "roulette-vetoed", ["by"] = who };
            _ = Post(vetoed);
            ToPage(new Dictionary<string, object>(vetoed) { ["by"] = who == name ? L.T("toi") : who });
            Program.Log($"roulette : veto de {who}");
        }

        // Hôte : « On regarde ! » sur le film gagnant (ou vote gagné).
        void OpenRouletteFilm(string id)
        {
            if (!isHost || id == null || !Regex.IsMatch(id, @"^tt\d{1,10}$")) return;
            string film;
            lock (rouletteGate) { voteRound = -rouletteRound; film = id == winnerId ? winnerName : null; }
            var open = new Dictionary<string, object> { ["type"] = "roulette-open", ["id"] = id, ["name"] = film };
            _ = Post(open);
            OnRouletteOpen(id, film);
        }

        void OnRouletteOpen(string id, string film)
        {
            var stats = RouletteStats.Picked(id, film);
            ToPage(new Dictionary<string, object> { ["type"] = "roulette-open", ["id"] = id, ["picks"] = stats.Picks });
        }

        // Film fini : si c'est le dernier choisi par la roue, on demande si elle avait vu juste.
        void AskRouletteFeedback(string imdb)
        {
            var stats = RouletteStats.Load();
            if (stats.Last == imdb && !stats.Asked) ToPage(new Dictionary<string, object> { ["type"] = "roulette-feedback-ask", ["name"] = stats.LastName ?? "" });
        }

        static void OpenTrailer(string yt)
        {
            if (yt == null || !Regex.IsMatch(yt, "^[A-Za-z0-9_-]{11}$")) return;
            var url = "https://www.youtube.com/watch?v=" + yt;
            try
            {
                if (Program.IsWindows) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(Program.IsMac ? "open" : "xdg-open", url) { UseShellExecute = false });
            }
            catch (Exception e) { Program.Log("bande-annonce : " + e.Message); }
        }

        // ---- Messages de la page (clics dans Stremio) -----------------------------------------------------------------

        void OnRoulettePage(string type, Dictionary<string, object> msg)
        {
            int round;
            lock (rouletteGate) round = rouletteRound;
            switch (type)
            {
                case "roulette": RequestRoulette(msg); break;
                case "roulette-go": OpenRouletteFilm(msg.TryGetValue("id", out var id) ? id as string : null); break;
                case "roulette-done": if (isHost && msg.TryGetValue("round", out var r) && r != null && Convert.ToInt32(r) == round) OpenVote(round); break;
                case "roulette-vote":
                    bool yes = msg.TryGetValue("yes", out var y) && y is bool yb && yb;
                    if (isHost) AddVote(name, round, yes);
                    else _ = Post(new Dictionary<string, object> { ["type"] = "roulette-vote", ["round"] = round, ["yes"] = yes });
                    break;
                case "roulette-veto":
                    if (isHost) Veto(name, round);
                    else _ = Post(new Dictionary<string, object> { ["type"] = "roulette-veto", ["round"] = round });
                    break;
                case "roulette-react":
                    var e = msg.TryGetValue("e", out var em) ? em as string : null;
                    if (Reactions.Contains(e)) _ = Post(new Dictionary<string, object> { ["type"] = "roulette-react", ["e"] = e });
                    break;
                case "roulette-trailer": OpenTrailer(msg.TryGetValue("yt", out var yt) ? yt as string : null); break;
                case "roulette-feedback": RouletteStats.Feedback(msg.TryGetValue("good", out var g) && g is bool gb && gb); break;
            }
        }

        // ---- Messages de roulette venus du salon -----------------------------------------------------------------------
        // Le relais ne laisse passer que ceux de l'hôte pour request/ask/check/spin/vote-open/votes/vetoed/open/error.
        void OnRouletteMessage(string type, Dictionary<string, object> msg)
        {
            var from = msg.TryGetValue("from", out var f) ? f as string ?? "?" : "?";
            int round = msg.TryGetValue("round", out var r) && r != null ? Convert.ToInt32(r) : 0;
            switch (type)
            {
                case "roulette-ask":
                    lock (rouletteGate) rouletteRound = round;
                    ToPage(new Dictionary<string, object> { ["type"] = "roulette-wait", ["by"] = msg.TryGetValue("by", out var b) ? b as string ?? from : from });
                    _ = Task.Run(async () =>
                    {
                        var t = await MyTaste();
                        await Post(new Dictionary<string, object> { ["type"] = "roulette-taste", ["round"] = round, ["genres"] = t.Genres, ["titles"] = t.Titles });
                    });
                    break;
                case "roulette-taste":
                    if (!isHost) break;
                    var profile = new TasteProfile { Titles = msg.TryGetValue("titles", out var tt) && tt != null ? Math.Max(0, Math.Min(10000, Convert.ToInt32(tt))) : 0 };
                    if (msg.TryGetValue("genres", out var g) && g is Dictionary<string, object> genres)
                        foreach (var kv in genres.Take(40))
                            if (kv.Key.Length <= 30 && kv.Value != null) profile.Genres[kv.Key] = Math.Max(0, Math.Min(1, Convert.ToDouble(kv.Value)));
                    lock (rouletteGate) if (round == rouletteRound) roomTastes[from] = profile;
                    break;
                case "roulette-check":
                    var asked = ((msg.TryGetValue("ids", out var ids) ? ids as System.Collections.ArrayList : null) ?? new System.Collections.ArrayList())
                        .OfType<string>().Take(ShortlistSize).ToList();
                    _ = Task.Run(async () =>
                    {
                        await MyTaste();
                        await Post(new Dictionary<string, object> { ["type"] = "roulette-seen", ["round"] = round, ["seen"] = asked.Where(myHistory.Contains).ToList() });
                    });
                    break;
                case "roulette-seen":
                    if (!isHost) break;
                    var seen = ((msg.TryGetValue("seen", out var s) ? s as System.Collections.ArrayList : null) ?? new System.Collections.ArrayList()).OfType<string>().Take(ShortlistSize);
                    lock (rouletteGate) if (round == rouletteRound) { foreach (var seenId in seen) seenByRoom.Add(seenId); seenAnswered.Add(from); }
                    break;
                case "roulette-spin":
                    lock (rouletteGate) rouletteRound = round;
                    ToPage(ForMe(msg, false));
                    break;
                case "roulette-vote":
                    if (isHost) AddVote(from, round, msg.TryGetValue("yes", out var y) && y is bool yb && yb);
                    break;
                case "roulette-veto":
                    if (isHost) Veto(from, round);
                    break;
                case "roulette-react":
                    var e = msg.TryGetValue("e", out var em) ? em as string : null;
                    if (Reactions.Contains(e)) ToPage(new Dictionary<string, object> { ["type"] = "roulette-react", ["e"] = e, ["from"] = from });
                    break;
                case "roulette-vote-open":
                case "roulette-votes":
                case "roulette-vetoed":
                case "roulette-error":
                    ToPage(msg);
                    break;
                case "roulette-open":
                    if (msg.TryGetValue("id", out var oid) && oid is string id && Regex.IsMatch(id, @"^tt\d{1,10}$"))
                    {
                        var film = msg.TryGetValue("name", out var fn) ? fn as string : null;
                        OnRouletteOpen(id, film != null && film.Length <= 80 ? film : null);
                    }
                    break;
            }
        }
    }

    // Statistiques de roulette de chacun (sur son PC) : combien de films choisis par la roue, et si elle a vu juste.
    class RouletteStats
    {
        public int Picks { get; set; }
        public int Good { get; set; }
        public int Bad { get; set; }
        public string Last { get; set; }
        public string LastName { get; set; }
        public bool Asked { get; set; }

        static readonly string File = Path.Combine(Program.DataDir, "roulette.json");
        static readonly object gate = new object();

        public static RouletteStats Load()
        {
            lock (gate)
            {
                try { return Program.Json.Deserialize<RouletteStats>(System.IO.File.ReadAllText(File)) ?? new RouletteStats(); }
                catch { return new RouletteStats(); }
            }
        }

        static RouletteStats Change(Action<RouletteStats> change)
        {
            lock (gate)
            {
                var s = Load();
                change(s);
                try { Directory.CreateDirectory(Program.DataDir); System.IO.File.WriteAllText(File, Program.Json.Serialize(s)); } catch { }
                return s;
            }
        }

        public static RouletteStats Picked(string id, string name) =>
            Change(s => { if (s.Last != id) s.Picks++; s.Last = id; s.LastName = name; s.Asked = false; });

        public static void Feedback(bool good) =>
            Change(s => { if (s.Asked) return; if (good) s.Good++; else s.Bad++; s.Asked = true; });
    }
}
