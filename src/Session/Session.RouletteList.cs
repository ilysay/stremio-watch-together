namespace StremioWT
{
    // « Notre liste » de la roulette : tout le salon y ajoute des films, l'hôte la garde et la renvoie à chacun.
    //   page → appli : « roulette-list-get » / « roulette-list-add » (id) / « roulette-list-remove » (id) / « roulette-list-clear » (hôte) ;
    //   un invité les fait suivre à l'hôte par le salon ; l'hôte répond à tous par « roulette-list » (liste complète).
    // Un membre n'envoie qu'un identifiant IMDb : l'hôte va lui-même chercher la fiche du film dans le catalogue de Stremio.
    // Une 4.1 ignore ces messages : elle voit la roue tourner, mais ne peut pas ajouter de film.
    partial class Session
    {
        static bool ValidId(string id) => id != null && Regex.IsMatch(id, @"^tt\d{1,10}$");

        void OnRouletteListPage(string type, Dictionary<string, object> msg)
        {
            var id = msg.TryGetValue("id", out var i) ? i as string : null;
            if (isHost)
            {
                switch (type)
                {
                    case "roulette-list-get": SendList(false); break;
                    case "roulette-list-add": _ = AddToList(name, id); break;
                    case "roulette-list-remove": RemoveFromList(id); break;
                    case "roulette-list-clear": ClearList(); break;
                }
                return;
            }
            if (type == "roulette-list-clear") return; // seul l'hôte vide la liste
            if (type != "roulette-list-get" && !ValidId(id)) return;
            var fwd = new Dictionary<string, object> { ["type"] = type };
            if (type != "roulette-list-get") fwd["id"] = id;
            _ = Post(fwd);
        }

        void OnRouletteListMessage(string type, Dictionary<string, object> msg)
        {
            if (type == "roulette-list")
            {
                if (!isHost) ToPage(new Dictionary<string, object>(msg) { ["host"] = false });
                return;
            }
            if (!isHost) return;
            var from = msg.TryGetValue("from", out var f) ? f as string ?? "?" : "?";
            var id = msg.TryGetValue("id", out var i) ? i as string : null;
            switch (type)
            {
                case "roulette-list-get": SendList(true); break;
                case "roulette-list-add": _ = AddToList(from, id); break;
                case "roulette-list-remove": RemoveFromList(id); break;
            }
        }

        // Liste complète, à sa page et (si besoin) à tout le salon.
        void SendList(bool toRoom, List<RouletteListFilm> list = null, string note = null)
        {
            list = list ?? RouletteList.Load();
            var films = list.Select(x => x.ToPage()).ToList();
            var msg = new Dictionary<string, object> { ["type"] = "roulette-list", ["films"] = films, ["max"] = RouletteList.Max };
            if (note != null) msg["note"] = note;
            if (toRoom) _ = Post(msg);
            ToPage(new Dictionary<string, object>(msg) { ["host"] = true });
        }

        async Task AddToList(string by, string id)
        {
            if (!ValidId(id)) return;
            var current = RouletteList.Load();
            if (current.Any(x => x.Id == id)) { SendList(true, current); return; }
            if (current.Count >= RouletteList.Max) { SendList(true, current, "full"); return; }
            var film = await Taste.Film(id);
            if (film == null) { Program.Log("roulette, liste : fiche introuvable pour " + id); SendList(true, current, "notfound"); return; }
            var list = RouletteList.Change(l =>
            {
                if (l.Count >= RouletteList.Max || l.Any(x => x.Id == id)) return false;
                l.Add(new RouletteListFilm
                {
                    Id = film.Id, Name = film.Name.Length > 80 ? film.Name.Substring(0, 80) : film.Name, Year = film.Year, Poster = film.Poster,
                    Genres = film.Genres, Rating = film.Rating, Minutes = film.Minutes, Trailer = film.Trailer,
                    By = by == null ? null : by.Length > 32 ? by.Substring(0, 32) : by,
                });
                return true;
            });
            Program.Log($"roulette, liste : {film.Name} ajouté par {by} ({list.Count} film(s))");
            SendList(true, list);
        }

        void RemoveFromList(string id)
        {
            if (!ValidId(id)) return;
            SendList(true, RouletteList.Change(l => l.RemoveAll(x => x.Id == id) > 0));
        }

        void ClearList()
        {
            Program.Log("roulette, liste : vidée par l'hôte");
            SendList(true, RouletteList.Change(l => { if (l.Count == 0) return false; l.Clear(); return true; }));
        }
    }
}
