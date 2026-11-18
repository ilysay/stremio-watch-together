namespace StremioWT
{
    // Un film de « Notre liste » : ce que la roue et la page ont besoin d'afficher, plus qui l'a ajouté.
    class RouletteListFilm
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Year { get; set; }
        public string Poster { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public double Rating { get; set; }
        public int Minutes { get; set; }
        public string Trailer { get; set; }
        public string By { get; set; }

        public RouletteFilm ToFilm() => new RouletteFilm
        {
            Id = Id, Name = Name, Year = Year, Poster = Poster, Genres = Genres ?? new List<string>(), Rating = Rating, Minutes = Minutes, Trailer = Trailer,
            Score = 1, // tous les films de la liste ont la même chance : c'est le salon qui les a choisis
        };

        public Dictionary<string, object> ToPage() => new Dictionary<string, object>
        {
            ["id"] = Id, ["name"] = Name, ["year"] = Year, ["poster"] = Poster, ["genres"] = (Genres ?? new List<string>()).Take(3).ToList(),
            ["rating"] = Math.Round(Rating, 1), ["minutes"] = Minutes, ["trailer"] = Trailer, ["by"] = By,
        };
    }

    // « Notre liste » de la roulette : les films que le salon a choisis lui-même. Gardée chez l'hôte (sur son PC)
    // d'une soirée à l'autre ; un film tiré par la roue puis lancé en sort.
    static class RouletteList
    {
        internal const int Max = 50;
        static readonly string File = Path.Combine(Program.DataDir, "roulette-list.json");
        static readonly object gate = new object();

        public static List<RouletteListFilm> Load()
        {
            lock (gate)
            {
                try
                {
                    var list = Program.Json.Deserialize<List<RouletteListFilm>>(System.IO.File.ReadAllText(File)) ?? new List<RouletteListFilm>();
                    return list.Where(f => f?.Id != null && Regex.IsMatch(f.Id, @"^tt\d{1,10}$")).GroupBy(f => f.Id).Select(g => g.First()).Take(Max).ToList();
                }
                catch { return new List<RouletteListFilm>(); }
            }
        }

        // Applique un changement et renvoie la liste à jour (enregistrée seulement si quelque chose a changé).
        public static List<RouletteListFilm> Change(Func<List<RouletteListFilm>, bool> change)
        {
            lock (gate)
            {
                var list = Load();
                if (change(list))
                    try { Directory.CreateDirectory(Program.DataDir); System.IO.File.WriteAllText(File, Program.Json.Serialize(list)); }
                    catch (Exception e) { Program.Log("roulette, liste : " + e.Message); }
                return list;
            }
        }

        public static bool Contains(string id) => Load().Any(f => f.Id == id);
    }
}
