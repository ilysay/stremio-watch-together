namespace StremioWT
{
    partial class Session
    {
        public event Action<ChatMessage> ChatReceived;
        DateTime lastChatSent = DateTime.MinValue;

        public bool SendChat(string text, string gif)
        {
            var msg = Chat.Sanitize(name, Chat.ApplyShortcuts(text ?? ""), gif);
            if (msg == null) return false;
            if ((DateTime.Now - lastChatSent).TotalMilliseconds < 700) return false;
            lastChatSent = DateTime.Now;
            msg.Mine = true;
            _ = Post(new Dictionary<string, object> { ["type"] = "chat", ["text"] = msg.Text, ["gif"] = msg.Gif });
            ShowChat(msg);
            return true;
        }

        void ShowChat(ChatMessage msg)
        {
            ChatReceived?.Invoke(msg);
            ToPage(new Dictionary<string, object> { ["type"] = "chat", ["from"] = msg.Mine ? L.T("toi") : msg.From, ["mine"] = msg.Mine, ["text"] = msg.Text, ["gif"] = msg.Gif });
        }

        public event Action<JournalEntry> FilmFinished;
        readonly Dictionary<string, Dictionary<string, double>> roomRatings = new Dictionary<string, Dictionary<string, double>>();

        async Task OnWatched(string imdb, string kind)
        {
            if (kind != "series")
            {
                var entry = await Journal.Record(imdb, members.Where(m => m != name));
                Console.WriteLine($"Film ajouté à ton journal : {entry.Display}.");
                FilmFinished?.Invoke(entry);
                return;
            }
            await Task.Delay(3000);
            var history = await ReadHistory();
            foreach (var added in Journal.AddSeriesFromHistory(history, imdb, members.Where(m => m != name)))
                Console.WriteLine($"Série ajoutée à ton journal : {added.Display}.");
            foreach (var done in Journal.SyncSeries(history))
            {
                Console.WriteLine($"Tu as terminé {done.Display} !");
                FilmFinished?.Invoke(done);
            }
        }

        public void ShareRating(JournalEntry entry)
        {
            if (entry.Rating <= 0) return;
            AddRoomRating(entry.ImdbId, entry.Display, name, entry.Rating);
            _ = Post(new Dictionary<string, object> { ["type"] = "rating", ["imdb"] = entry.ImdbId, ["title"] = entry.Display, ["rating"] = entry.Rating });
        }

        void AddRoomRating(string imdb, string title, string who, double rating)
        {
            double avg; int count;
            lock (roomRatings)
            {
                if (!roomRatings.TryGetValue(imdb, out var byName)) roomRatings[imdb] = byName = new Dictionary<string, double>();
                byName[who] = rating;
                avg = byName.Values.Average(); count = byName.Count;
            }
            var line = (who == name ? "Ta note" : $"{who} a noté") + $" {title} : {Journal.Stars(rating)}";
            if (count > 1) line += $" · moyenne du salon {Journal.Stars(Math.Round(avg * 2) / 2)} ({avg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}/5, {count} notes)";
            Console.WriteLine(line);
            if (who != name) ToPage(new Dictionary<string, object> { ["type"] = "toast", ["text"] = L.F("{0} a noté {1} : {2}", who, title, Journal.Stars(rating)) });
        }
    }
}
