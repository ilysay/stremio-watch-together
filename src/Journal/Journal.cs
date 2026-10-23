namespace StremioWT
{
    class JournalEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Type { get; set; } = "movie";
        public string ImdbId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string Poster { get; set; }
        public string WatchedDate { get; set; }
        public double Rating { get; set; }
        public string Review { get; set; } = "";
        public bool Rewatch { get; set; }
        public List<string> With { get; set; } = new List<string>();
        public bool Exported { get; set; }
        public int EpisodesWatched { get; set; }
        public int EpisodesTotal { get; set; }
        public string LastEpisode { get; set; }

        [SkipJson]
        public bool IsMovie => Type == "movie";
        [SkipJson]
        public bool Completed => !IsMovie && EpisodesTotal > 0 && EpisodesWatched >= EpisodesTotal;
        [SkipJson]
        public string Display => (string.IsNullOrEmpty(Title) ? ImdbId : Title) + (string.IsNullOrEmpty(Year) ? "" : $" ({Year})");
        [SkipJson]
        public string Progress => IsMovie ? "" :
            (string.IsNullOrEmpty(LastEpisode) ? "" : LocalEpisode(LastEpisode) + " · ") +
            (EpisodesTotal > 0 ? L.F("{0}/{1} épisodes", EpisodesWatched, EpisodesTotal) : L.F("{0} épisode(s) vu(s)", EpisodesWatched)) +
            (Completed ? L.T(" · terminée") : "");

        static string LocalEpisode(string label)
        {
            var m = System.Text.RegularExpressions.Regex.Match(label, @"^S(\d+) É(\d+)$");
            return m.Success ? L.F("S{0} É{1}", m.Groups[1].Value, m.Groups[2].Value) : label;
        }
    }

    class StremioFilm
    {
        public string ImdbId, Title, Year, Poster, WatchedDate;
        public string Type = "movie";
        public int EpisodesWatched;
        public string LastEpisode;
        public string Display => Title + (string.IsNullOrEmpty(Year) ? "" : $" ({Year})");
    }

    static partial class Journal
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StremioWT");
        static readonly string File = Path.Combine(Dir, "journal.json");
        static readonly string SeenFile = Path.Combine(Dir, "journal-historique.json");
        static readonly string PosterDir = Path.Combine(Dir, "affiches");
        static readonly object gate = new object();
        static List<JournalEntry> entries;
        static HashSet<string> seenHistory;

        public static event Action Changed;

        public static List<JournalEntry> All()
        {
            lock (gate) { Load(); return entries.OrderByDescending(e => e.WatchedDate).ThenByDescending(e => e.Id).ToList(); }
        }

        static void Load()
        {
            if (entries != null) return;
            try { entries = Program.Json.Deserialize<List<JournalEntry>>(System.IO.File.ReadAllText(File)) ?? new List<JournalEntry>(); }
            catch { entries = new List<JournalEntry>(); }
            foreach (var e in entries) if (string.IsNullOrEmpty(e.Type)) e.Type = "movie";
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                System.IO.File.WriteAllText(File, Program.Json.Serialize(entries), new UTF8Encoding(false));
            }
            catch (Exception e) { Program.Log("journal non enregistré : " + e.Message); }
        }

        public static async Task<JournalEntry> Record(string imdbId, IEnumerable<string> with)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            JournalEntry entry;
            lock (gate)
            {
                Load();
                entry = entries.FirstOrDefault(e => e.IsMovie && e.ImdbId == imdbId && e.WatchedDate == today);
                if (entry != null) return entry;
                entry = new JournalEntry
                {
                    ImdbId = imdbId,
                    WatchedDate = today,
                    Rewatch = entries.Any(e => e.IsMovie && e.ImdbId == imdbId),
                    With = with.ToList(),
                };
                entries.Add(entry);
                Save();
            }
            await FillMeta(entry);
            Program.Log($"journal : {entry.Display} ajouté");
            Changed?.Invoke();
            return entry;
        }

        static async Task FillMeta(JournalEntry entry)
        {
            try
            {
                var kind = entry.IsMovie ? "movie" : "series";
                using (var cts = new CancellationTokenSource(8000))
                using (var res = await Program.Http.GetAsync($"https://v3-cinemeta.strem.io/meta/{kind}/{entry.ImdbId}.json", cts.Token))
                {
                    var root = Program.Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync());
                    if (!(root?["meta"] is Dictionary<string, object> meta)) return;
                    lock (gate)
                    {
                        if (string.IsNullOrEmpty(entry.Title)) entry.Title = meta.TryGetValue("name", out var n) ? n as string : null;
                        if (string.IsNullOrEmpty(entry.Year))
                        {
                            var year = (meta.TryGetValue("year", out var y) ? y : meta.TryGetValue("releaseInfo", out var r) ? r : null)?.ToString() ?? "";
                            entry.Year = entry.IsMovie && year.Length >= 4 ? year.Substring(0, 4) : year;
                        }
                        if (string.IsNullOrEmpty(entry.Poster)) entry.Poster = meta.TryGetValue("poster", out var p) ? p as string : null;
                        if (!entry.IsMovie)
                        {
                            if (meta.TryGetValue("videos", out var v) && v is System.Collections.ArrayList videos)
                                entry.EpisodesTotal = videos.OfType<Dictionary<string, object>>().Count(x => x.TryGetValue("season", out var s) && Convert.ToInt32(s) > 0);
                            var genres = (meta.TryGetValue("genres", out var g) ? g as System.Collections.ArrayList : null)?.OfType<string>() ?? Enumerable.Empty<string>();
                            var country = meta.TryGetValue("country", out var c) ? c as string ?? "" : "";
                            if (genres.Contains("Animation") && country.Contains("Japan")) entry.Type = "anime";
                        }
                        Save();
                    }
                }
            }
            catch (Exception e) { Program.Log("cinemeta : " + e.Message); }
        }

        // Lecture de la bibliothèque de Stremio (localStorage « lib_<compte>_<imdb> ») :
        // films vus (marqués vus, regardés au moins une fois, ou position ≥ 85 %) et séries entamées,
        public const string HistoryScript = @"(()=>{const out=[];for(const key of Object.keys(localStorage)){
            if(!/^lib_[^_]+_tt\d+$/.test(key))continue;
            try{const it=JSON.parse(localStorage.getItem(key));const s=it.state||{};
              if(it.type==='movie'){if(!(s.flaggedWatched||s.timesWatched>0||(s.duration>0&&s.timeOffset/s.duration>=0.85)))continue;}
              else if(it.type==='series'){if(!(s.watched||s.timesWatched>0||s.flaggedWatched))continue;}
              else continue;
              out.push({id:it._id,type:it.type,name:it.name||'',year:String(it.year||''),poster:it.poster||'',last:s.lastWatched||it._mtime||'',
                watched:s.watched||'',video:s.video_id||''});
            }catch(e){}}return out})()";

        public static List<StremioFilm> ParseHistory(object value)
        {
            var list = new List<StremioFilm>();
            foreach (var o in (value as System.Collections.ArrayList) ?? new System.Collections.ArrayList())
            {
                if (!(o is Dictionary<string, object> d) || !(d["id"] is string id)) continue;
                var type = d.TryGetValue("type", out var t) ? t as string ?? "movie" : "movie";
                var year = d["year"] as string ?? "";
                var date = DateTime.TryParse(d["last"] as string, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt)
                    ? dt.ToLocalTime().ToString("yyyy-MM-dd") : DateTime.Now.ToString("yyyy-MM-dd");
                var f = new StremioFilm { ImdbId = id, Type = type, Title = d["name"] as string ?? id, Poster = d["poster"] as string, WatchedDate = date,
                    Year = type == "movie" && year.Length >= 4 ? year.Substring(0, 4) : year };
                if (type == "series")
                {
                    f.EpisodesWatched = CountWatched(d.TryGetValue("watched", out var w) ? w as string : null);
                    f.LastEpisode = EpisodeLabel(d.TryGetValue("video", out var vid) ? vid as string : null);
                }
                list.Add(f);
            }
            return list;
        }

        // « tt123:1:8:8:<base64 zlib> » : un bit par épisode, à 1 si vu.
        static int CountWatched(string watched)
        {
            try
            {
                if (string.IsNullOrEmpty(watched)) return 0;
                var data = Convert.FromBase64String(watched.Substring(watched.LastIndexOf(':') + 1));
                int n = 0, b;
                using (var ds = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress)) // 2 octets d'en-tête zlib
                    while ((b = ds.ReadByte()) >= 0) for (; b != 0; b &= b - 1) n++;
                return n;
            }
            catch { return 0; }
        }

        static string EpisodeLabel(string videoId)
        {
            var parts = (videoId ?? "").Split(':');
            return parts.Length >= 3 && int.TryParse(parts[1], out var s) && int.TryParse(parts[2], out var e) ? $"S{s} É{e}" : null;
        }

        static void LoadSeen()
        {
            if (seenHistory != null) return;
            try { seenHistory = new HashSet<string>(Program.Json.Deserialize<List<string>>(System.IO.File.ReadAllText(SeenFile)) ?? new List<string>()); }
            catch { seenHistory = new HashSet<string>(); }
        }

        static void SaveSeen()
        {
            try { Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(SeenFile, Program.Json.Serialize(seenHistory.ToList())); } catch { }
        }

        static bool InJournal(StremioFilm f) =>
            entries.Any(e => e.ImdbId == f.ImdbId && (f.Type == "movie" ? e.IsMovie : !e.IsMovie));

        public static List<StremioFilm> NewFromHistory(List<StremioFilm> history)
        {
            lock (gate)
            {
                Load(); LoadSeen();
                return history.Where(f => !seenHistory.Contains(f.ImdbId) && !InJournal(f)).OrderByDescending(f => f.WatchedDate).ToList();
            }
        }

        public static List<StremioFilm> NotInJournal(List<StremioFilm> history)
        {
            lock (gate) { Load(); return history.Where(f => !InJournal(f)).ToList(); }
        }

        public static int ImportHistory(List<StremioFilm> items, bool alreadyOnLetterboxd)
        {
            var added = new List<JournalEntry>();
            lock (gate)
            {
                Load(); LoadSeen();
                foreach (var f in items)
                {
                    seenHistory.Add(f.ImdbId);
                    if (InJournal(f)) continue;
                    var e = new JournalEntry
                    {
                        Type = f.Type, ImdbId = f.ImdbId, Title = f.Title, Year = f.Year, Poster = f.Poster, WatchedDate = f.WatchedDate,
                        Exported = f.Type == "movie" && alreadyOnLetterboxd, EpisodesWatched = f.EpisodesWatched, LastEpisode = f.LastEpisode,
                    };
                    entries.Add(e);
                    added.Add(e);
                }
                Save(); SaveSeen();
            }
            Program.Log($"journal : {added.Count} titre(s) importé(s) depuis l'historique Stremio");
            Changed?.Invoke();
            CompleteSeriesInfo(added.Where(e => !e.IsMovie));
            return added.Count;
        }

        static void CompleteSeriesInfo(IEnumerable<JournalEntry> series)
        {
            var list = series.ToList();
            if (list.Count == 0) return;
            _ = Task.Run(async () =>
            {
                foreach (var e in list) await FillMeta(e);
                Changed?.Invoke();
            });
        }

        public static List<JournalEntry> SyncSeries(List<StremioFilm> history)
        {
            var finished = new List<JournalEntry>();
            bool changed = false;
            lock (gate)
            {
                Load();
                foreach (var f in history.Where(h => h.Type == "series"))
                {
                    var e = entries.FirstOrDefault(x => !x.IsMovie && x.ImdbId == f.ImdbId);
                    if (e == null || (e.EpisodesWatched == f.EpisodesWatched && e.LastEpisode == f.LastEpisode)) continue;
                    bool wasDone = e.Completed;
                    e.EpisodesWatched = Math.Max(e.EpisodesWatched, f.EpisodesWatched);
                    e.LastEpisode = f.LastEpisode ?? e.LastEpisode;
                    if (string.CompareOrdinal(f.WatchedDate, e.WatchedDate) > 0) e.WatchedDate = f.WatchedDate;
                    changed = true;
                    if (!wasDone && e.Completed) finished.Add(e);
                }
                if (changed) Save();
            }
            if (changed) Changed?.Invoke();
            return finished;
        }

        public static List<JournalEntry> AddSeriesFromHistory(List<StremioFilm> history, string imdbId, IEnumerable<string> with)
        {
            var f = history.FirstOrDefault(h => h.Type == "series" && h.ImdbId == imdbId);
            if (f == null) return new List<JournalEntry>();
            lock (gate)
            {
                Load();
                if (entries.Any(x => !x.IsMovie && x.ImdbId == imdbId)) return new List<JournalEntry>();
            }
            ImportHistory(new List<StremioFilm> { f }, false);
            lock (gate)
            {
                var e = entries.First(x => !x.IsMovie && x.ImdbId == imdbId);
                e.With = with.ToList();
                Save();
                return new List<JournalEntry> { e };
            }
        }

        public static void MarkSeen(List<StremioFilm> items)
        {
            lock (gate) { LoadSeen(); foreach (var f in items) seenHistory.Add(f.ImdbId); SaveSeen(); }
        }

        public static void Update(JournalEntry entry, double rating, string review)
        {
            lock (gate) { entry.Rating = rating; entry.Review = review ?? ""; Save(); }
            Changed?.Invoke();
        }

        public static void Remove(JournalEntry entry)
        {
            lock (gate) { Load(); entries.RemoveAll(e => e.Id == entry.Id); Save(); }
            Changed?.Invoke();
        }

        public static int PendingExport() { lock (gate) { Load(); return entries.Count(e => e.IsMovie && !e.Exported); } }
        public static int MovieCount() { lock (gate) { Load(); return entries.Count(e => e.IsMovie); } }

        public static readonly string ExportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StremioWT");

        // Fichier CSV au format d'import Letterboxd (letterboxd.com/import), notés ou non.
        public static (int count, string path) ExportCsv(bool all)
        {
            List<JournalEntry> list;
            lock (gate) { Load(); list = entries.Where(e => e.IsMovie && (all || !e.Exported)).OrderBy(e => e.WatchedDate).ToList(); }
            var sb = new StringBuilder("imdbID,Title,Year,WatchedDate,Rating,Rewatch,Review\n");
            foreach (var e in list)
                sb.Append(string.Join(",", Csv(e.ImdbId), Csv(e.Title), Csv(e.Year), Csv(e.WatchedDate),
                    e.Rating > 0 ? e.Rating.ToString("0.#", CultureInfo.InvariantCulture) : "",
                    e.Rewatch ? "true" : "false", Csv(e.Review))).Append('\n');
            Directory.CreateDirectory(ExportDir);
            foreach (var old in Directory.GetFiles(ExportDir, "letterboxd-import-*.csv")) try { System.IO.File.Delete(old); } catch { }
            var path = Path.Combine(ExportDir, $"letterboxd-import-{DateTime.Now:yyyy-MM-dd-HHmm}.csv");
            System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            lock (gate) { foreach (var e in list) e.Exported = true; Save(); }
            Changed?.Invoke();
            return (list.Count, path);
        }

        static string Csv(string v)
        {
            v = v ?? "";
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        public static string Stars(double rating)
        {
            if (rating <= 0) return L.T("pas noté");
            int full = (int)Math.Floor(rating);
            return new string('★', full) + (rating - full >= 0.5 ? "½" : "");
        }
    }
}
