namespace StremioWT
{
    // Goûts d'une personne, tirés de son historique Stremio (films et séries vus) : la part de chaque genre.
    // Les notes ne servent pas : la plupart des gens ne notent rien, mais tout le monde a un historique.
    class TasteProfile
    {
        public Dictionary<string, double> Genres = new Dictionary<string, double>();
        public int Titles; // nombre de titres de l'historique qui ont servi au calcul
    }

    // Un film proposé par la roulette.
    class RouletteFilm
    {
        public string Id, Name, Year, Poster, Trailer; // Trailer : identifiant YouTube de la bande-annonce
        public List<string> Genres = new List<string>();
        public double Rating, Score;
        public int Minutes;
    }

    // Modes de la roulette : des filtres sur le même calcul de goûts.
    static class RouletteMode
    {
        public const string Classic = "classic", Horror = "horror", Nanar = "nanar", Nineties = "90s", Gem = "gem", Short = "short";
        public static readonly string[] All = { Classic, Horror, Nanar, Nineties, Gem, Short };
        public static string Check(string mode) => All.Contains(mode) ? mode : Classic;

        // Ce que le mode garde : note, durée, année.
        public static bool Accepts(string mode, RouletteFilm f)
        {
            switch (mode)
            {
                case Nanar: return f.Rating >= 2.5 && f.Rating <= 5.6;           // les « nanars » : mal notés mais connus
                case Gem: return f.Rating >= 7.3;                                // pépites : très bien notées, cherchées loin dans le catalogue
                case Short: return f.Rating >= 6 && f.Minutes >= 70 && f.Minutes <= 100;
                case Nineties: return f.Rating >= 6 && f.Year != null && f.Year.StartsWith("199");
                case Horror: return f.Rating >= 5.5 && f.Genres.Contains("Horror");
                default: return f.Rating >= 6;
            }
        }
    }

    static class Taste
    {
        // Au-delà, l'historique ancien n'apprend plus grand-chose et coûterait des requêtes.
        const int MaxTitles = 60;
        // En dessous, l'historique est trop maigre pour parler de goûts : la personne ne pèse pas sur les genres.
        internal const int MinTitles = 5;
        // Genres qui divisent : quelqu'un qui n'en regarde jamais (avec assez d'historique) ne les veut sans doute pas.
        static readonly string[] Divisive = { "Horror", "Romance", "Animation", "Documentary", "Family", "Western", "War", "Sport", "Biography", "History" };
        const int MinTitlesToAvoid = 15;

        // Profil à partir de l'historique et des genres connus de chaque titre (identifiant IMDb → genres).
        // Les titres récents comptent plus ; une série compte un peu plus qu'un film quand on en a vu beaucoup d'épisodes.
        internal static TasteProfile Build(IEnumerable<StremioFilm> history, IDictionary<string, List<string>> genresOf, DateTime today)
        {
            var profile = new TasteProfile();
            double total = 0;
            foreach (var f in Recent(history))
            {
                if (!genresOf.TryGetValue(f.ImdbId, out var genres) || genres.Count == 0) continue;
                double w = f.Type == "series" ? 1 + Math.Log(1 + f.EpisodesWatched, 2) / 2 : 1;
                if (DateTime.TryParse(f.WatchedDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && (today - d).TotalDays <= 90) w *= 1.5;
                foreach (var g in genres) profile.Genres[g] = (profile.Genres.TryGetValue(g, out var v) ? v : 0) + w;
                total += w;
                profile.Titles++;
            }
            if (total > 0) foreach (var g in profile.Genres.Keys.ToList()) profile.Genres[g] = Math.Round(profile.Genres[g] / total, 3);
            return profile;
        }

        internal static IEnumerable<StremioFilm> Recent(IEnumerable<StremioFilm> history) =>
            history.Where(f => f.Type == "movie" || f.Type == "series").OrderByDescending(f => f.WatchedDate).Take(MaxTitles);

        // Goûts du salon : la moyenne des parts de chaque genre, entre les personnes qui ont assez d'historique.
        // Un genre qui divise est écarté si quelqu'un (avec un vrai historique) n'en regarde jamais : personne ne doit subir le film.
        internal static (Dictionary<string, double> combined, HashSet<string> avoided) Merge(IEnumerable<TasteProfile> profiles)
        {
            var valid = profiles.Where(p => p != null && p.Titles >= MinTitles).ToList();
            var combined = new Dictionary<string, double>();
            var avoided = new HashSet<string>();
            if (valid.Count == 0) return (combined, avoided);
            foreach (var g in valid.SelectMany(p => p.Genres.Keys).Distinct())
                combined[g] = valid.Average(p => p.Genres.TryGetValue(g, out var v) ? v : 0);
            foreach (var g in Divisive)
                if (valid.Any(p => p.Titles >= MinTitlesToAvoid && !(p.Genres.TryGetValue(g, out var v) && v > 0.02))) avoided.Add(g);
            foreach (var g in avoided) combined.Remove(g);
            return (combined, avoided);
        }

        // Les genres les plus partagés, pour aller chercher des films dans le catalogue.
        internal static List<string> TopGenres(Dictionary<string, double> combined, int count) =>
            combined.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(count).Select(kv => kv.Key).ToList();

        // Note d'un film pour le salon : surtout l'accord avec les goûts, un peu la note IMDb.
        // Sans goûts connus (historiques trop maigres), seule la note IMDb compte.
        internal static double Score(RouletteFilm film, Dictionary<string, double> combined, string mode = RouletteMode.Classic)
        {
            // Mode nanar : plus c'est mal noté, mieux c'est.
            double quality = mode == RouletteMode.Nanar ? Math.Max(0, Math.Min(1, (6 - film.Rating) / 3)) : Math.Max(0, Math.Min(1, (film.Rating - 5) / 4));
            if (combined.Count == 0 || film.Genres.Count == 0) return quality;
            double fit = film.Genres.Sum(g => combined.TryGetValue(g, out var v) ? v : 0) / Math.Sqrt(film.Genres.Count);
            return 0.75 * fit + 0.25 * quality;
        }

        // Films retenus : pas déjà vus, pas d'un genre écarté, de vrais longs métrages corrects ; triés du plus au moins adapté.
        internal static List<RouletteFilm> Rank(IEnumerable<RouletteFilm> pool, Dictionary<string, double> combined, HashSet<string> avoided, ISet<string> seen,
            string mode = RouletteMode.Classic)
        {
            // Le mode horreur passe outre le rejet de l'horreur : c'est le thème choisi.
            var avoid = mode == RouletteMode.Horror ? new HashSet<string>(avoided.Where(g => g != "Horror")) : avoided;
            return pool
                .Where(f => !seen.Contains(f.Id) && !f.Genres.Any(avoid.Contains) && (f.Minutes == 0 || f.Minutes >= 70) && RouletteMode.Accepts(mode, f))
                .GroupBy(f => f.Id).Select(g => g.First())
                .Select(f => { f.Score = Score(f, combined, mode); return f; })
                .OrderByDescending(f => f.Score).ThenBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
        }

        // Les films de la roue : tirés au sort parmi les mieux classés (les plus adaptés ont plus de chances d'y être),
        // sans reprendre ceux des derniers tirages. Sinon la même liste revenait à chaque fois pour un même salon.
        internal static List<RouletteFilm> SampleReel(IList<RouletteFilm> ranked, int size, Random rng, ISet<string> recentlyShown)
        {
            var fresh = ranked.Where(f => !recentlyShown.Contains(f.Id)).ToList();
            var pool = (fresh.Count >= size ? fresh : ranked).Take(Math.Max(size * 5, 60)).ToList();
            var reel = new List<RouletteFilm>();
            while (reel.Count < size && pool.Count > 0)
            {
                int i = PickWinner(pool, rng);
                reel.Add(pool[i]);
                pool.RemoveAt(i);
            }
            return reel;
        }

        // Compatibilité de deux personnes : à quel point elles regardent les mêmes genres (0 à 100 %).
        internal static int Compatibility(TasteProfile a, TasteProfile b)
        {
            var genres = a.Genres.Keys.Union(b.Genres.Keys).ToList();
            double dot = 0, na = 0, nb = 0;
            foreach (var g in genres)
            {
                double x = a.Genres.TryGetValue(g, out var va) ? va : 0, y = b.Genres.TryGetValue(g, out var vb) ? vb : 0;
                dot += x * y; na += x * x; nb += y * y;
            }
            return na == 0 || nb == 0 ? 0 : (int)Math.Round(100 * dot / Math.Sqrt(na * nb));
        }

        // La personne dont le film colle le mieux aux goûts (parmi celles qui ont assez d'historique), ou null.
        internal static string BestFit(IDictionary<string, TasteProfile> profiles, RouletteFilm film) =>
            profiles.Where(kv => kv.Value != null && kv.Value.Titles >= MinTitles && film.Genres.Count > 0)
                .Select(kv => (name: kv.Key, fit: film.Genres.Sum(g => kv.Value.Genres.TryGetValue(g, out var v) ? v : 0)))
                .Where(x => x.fit > 0).OrderByDescending(x => x.fit).ThenBy(x => x.name, StringComparer.Ordinal).Select(x => x.name).FirstOrDefault();

        // Battle royale : l'ordre d'élimination des finalistes. Les moins adaptés ont plus de chances de partir les premiers.
        internal static List<int> EliminationOrder(IList<RouletteFilm> finalists, Random rng)
        {
            var left = Enumerable.Range(0, finalists.Count).ToList();
            var order = new List<int>();
            while (left.Count > 1)
            {
                var weights = left.Select(i => 1.2 - Math.Min(1, Math.Max(0, finalists[i].Score))).ToList();
                double r = rng.NextDouble() * weights.Sum();
                int k = 0;
                for (; k < weights.Count - 1; k++) { r -= weights[k]; if (r <= 0) break; }
                order.Add(left[k]);
                left.RemoveAt(k);
            }
            return order;
        }

        // Tirage du gagnant parmi la sélection : au hasard, mais les films les plus adaptés ont plus de chances.
        internal static int PickWinner(IList<RouletteFilm> shortlist, Random rng)
        {
            var weights = shortlist.Select(f => Math.Max(0.01, f.Score) * Math.Max(0.01, f.Score)).ToList();
            double r = rng.NextDouble() * weights.Sum();
            for (int i = 0; i < weights.Count; i++) { r -= weights[i]; if (r <= 0) return i; }
            return weights.Count - 1;
        }

        // ---- Catalogue de Stremio (Cinemeta) --------------------------------------------------------------------

        const string Cinemeta = "https://v3-cinemeta.strem.io";
        static readonly string CacheFile = Path.Combine(Program.DataDir, "genres.json");
        static readonly object cacheGate = new object();
        static Dictionary<string, List<string>> cache;

        static Dictionary<string, List<string>> Cache()
        {
            lock (cacheGate)
            {
                if (cache != null) return cache;
                try
                {
                    var raw = Program.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(CacheFile)) ?? new Dictionary<string, object>();
                    cache = raw.ToDictionary(kv => kv.Key, kv => ((kv.Value as System.Collections.ArrayList) ?? new System.Collections.ArrayList()).OfType<string>().ToList());
                }
                catch { cache = new Dictionary<string, List<string>>(); }
                return cache;
            }
        }

        // Genres de chaque titre de l'historique : gardés sur le disque, seuls les nouveaux titres sont demandés à Cinemeta.
        internal static async Task<Dictionary<string, List<string>>> GenresFor(IEnumerable<StremioFilm> titles)
        {
            var known = Cache();
            List<StremioFilm> missing;
            lock (cacheGate) missing = titles.Where(t => !known.ContainsKey(t.ImdbId)).ToList();
            var slots = new SemaphoreSlim(6);
            var all = Task.WhenAll(missing.Select(async t =>
            {
                await slots.WaitAsync();
                try
                {
                    var meta = await Get($"{Cinemeta}/meta/{(t.Type == "series" ? "series" : "movie")}/{t.ImdbId}.json", 6000);
                    var genres = meta?["meta"] is Dictionary<string, object> m ? GenresOf(m) : null;
                    if (genres != null) lock (cacheGate) known[t.ImdbId] = genres;
                }
                finally { slots.Release(); }
            }));
            // Au premier tirage, tout l'historique est à demander : 15 s au plus, les genres manquants viendront au suivant.
            await Task.WhenAny(all, Task.Delay(15000));
            lock (cacheGate)
            {
                if (missing.Count > 0) try { Directory.CreateDirectory(Program.DataDir); File.WriteAllText(CacheFile, Program.Json.Serialize(known)); } catch { }
                return titles.Where(t => known.ContainsKey(t.ImdbId)).GroupBy(t => t.ImdbId).ToDictionary(g => g.Key, g => known[g.Key]);
            }
        }

        // Films populaires et les mieux notés dans les genres du salon ; sans goûts, les films populaires du moment.
        // Chaque tirage regarde des pages différentes du catalogue (au-delà des 50 premiers), pour varier les propositions.
        internal static async Task<List<RouletteFilm>> Candidates(List<string> genres, Random rng, string mode = RouletteMode.Classic)
        {
            string Page(string catalog, string genre, int minPage = 0, int maxPage = 3) =>
                $"{Cinemeta}/catalog/movie/{catalog}/" + (genre == null ? "" : $"genre={Uri.EscapeDataString(genre)}&") + $"skip={rng.Next(minPage, maxPage + 1) * 50}.json";
            List<string> urls;
            switch (mode)
            {
                case RouletteMode.Horror:
                    urls = new List<string> { Page("top", "Horror"), Page("top", "Horror"), Page("imdbRating", "Horror"), Page("imdbRating", "Horror", 1, 4) };
                    break;
                case RouletteMode.Nineties:
                    // Catalogue par année : trois années des années 90 au hasard, puis le tri par goûts fait le reste.
                    urls = Enumerable.Range(1990, 10).OrderBy(_ => rng.Next()).Take(4).Select(y => $"{Cinemeta}/catalog/movie/year/genre={y}.json").ToList();
                    break;
                case RouletteMode.Gem:
                    // Pépites : les mieux notées, mais loin dans la liste (moins connues que les grands classiques).
                    urls = (genres.Count == 0 ? new List<string> { null, null } : genres).SelectMany(g => new[] { Page("imdbRating", g, 2, 6), Page("imdbRating", g, 2, 6) }).ToList();
                    break;
                case RouletteMode.Nanar:
                    // Le catalogue « populaires » ne garde que les films notés 6 et plus : les nanars sont dans le catalogue
                    // par année (environ un film sur cinq). Huit années au hasard, surtout leur première page (les pages
                    // suivantes répondent souvent lentement) ; le tri par goûts fait le reste.
                    urls = Enumerable.Range(1985, 40).OrderBy(_ => rng.Next()).Take(8)
                        .Select((y, i) => $"{Cinemeta}/catalog/movie/year/genre={y}" + (i % 3 == 2 ? "&skip=50" : "") + ".json").ToList();
                    break;
                default:
                    urls = genres.Count == 0
                        ? new List<string> { Page("top", null), Page("top", null), Page("imdbRating", null) }
                        : genres.SelectMany(g => new[] { Page("top", g), Page("imdbRating", g) }).ToList();
                    break;
            }
            var lists = await Task.WhenAll(urls.Select(u => Get(u, 6000))); // une page lente ne doit pas faire attendre tout le salon
            return lists.Where(l => l != null && l.TryGetValue("metas", out var m) && m is System.Collections.ArrayList)
                .SelectMany(l => ((System.Collections.ArrayList)l["metas"]).OfType<Dictionary<string, object>>())
                .Select(ToFilm).Where(f => f != null).ToList();
        }

        internal static RouletteFilm ToFilm(Dictionary<string, object> m)
        {
            var id = m.TryGetValue("id", out var i) ? i as string : null;
            if (id == null || !Regex.IsMatch(id, @"^tt\d+$")) return null;
            var runtime = Regex.Match(m.TryGetValue("runtime", out var r) ? r as string ?? "" : "", @"\d+");
            double.TryParse(m.TryGetValue("imdbRating", out var ir) ? ir?.ToString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating);
            var year = (m.TryGetValue("year", out var y) ? y : m.TryGetValue("releaseInfo", out var ri) ? ri : null)?.ToString() ?? "";
            return new RouletteFilm
            {
                Id = id,
                Name = m.TryGetValue("name", out var n) ? n as string ?? id : id,
                Year = year.Length >= 4 ? year.Substring(0, 4) : year,
                Poster = m.TryGetValue("poster", out var p) ? p as string : null,
                Genres = GenresOf(m),
                Rating = rating,
                Minutes = runtime.Success ? int.Parse(runtime.Value) : 0,
                Trailer = ((m.TryGetValue("trailerStreams", out var ts) ? ts as System.Collections.ArrayList : null) ?? new System.Collections.ArrayList())
                    .OfType<Dictionary<string, object>>().Select(t => t.TryGetValue("ytId", out var yt) ? yt as string : null)
                    .FirstOrDefault(yt => yt != null && Regex.IsMatch(yt, "^[A-Za-z0-9_-]{11}$")),
            };
        }

        static List<string> GenresOf(Dictionary<string, object> m) =>
            ((m.TryGetValue("genres", out var g) ? g : m.TryGetValue("genre", out var g2) ? g2 : null) as System.Collections.ArrayList)?
                .OfType<string>().Take(6).ToList() ?? new List<string>();

        static async Task<Dictionary<string, object>> Get(string url, int timeoutMs)
        {
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var res = await Program.Http.GetAsync(url, cts.Token))
                    return res.IsSuccessStatusCode ? Program.Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync()) : null;
            }
            catch (Exception e) { Program.Log("roulette : " + url.Substring(Cinemeta.Length) + " : " + e.Message); return null; }
        }
    }
}
