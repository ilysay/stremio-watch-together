namespace StremioWT
{
    class ChatMessage
    {
        public string From, Text, Gif;
        public bool System;
        public bool Mine;
        public DateTime Time = DateTime.Now;
    }

    static class Chat
    {
        public const int MaxText = 300;
        // Recherche de GIF : le serveur Cloudflare relaie Giphy (la clé Giphy reste sur le serveur, pas dans l'exe).
        public const string GifServer = "https://stremiowt-stats.stremiowt.workers.dev/gif";

        public static readonly Dictionary<string, string> Shortcuts = new Dictionary<string, string>
        {
            [":feu:"] = "🔥", [":fire:"] = "🔥", [":mdr:"] = "😂", [":lol:"] = "😂", [":coeur:"] = "❤️", [":heart:"] = "❤️",
            [":mort:"] = "💀", [":skull:"] = "💀", [":bravo:"] = "👏", [":clap:"] = "👏", [":ok:"] = "👌", [":pop:"] = "🍿",
            [":peur:"] = "😱", [":triste:"] = "😢", [":ouf:"] = "😮‍💨", [":cool:"] = "😎", [":wow:"] = "😮", [":pleure:"] = "😭",
        };

        public static readonly string[] Emotes =
        {
            "😂", "🤣", "😭", "😱", "😮", "😍", "🥰", "😎", "🤔", "😴", "🙄", "😬",
            "😡", "🥶", "🤯", "💀", "👀", "👏", "🙏", "👌", "👍", "👎", "🔥", "❤️",
            "💔", "💯", "🍿", "🎬", "🍕", "🥤", "🎉", "⭐", "🤡", "👻", "😈", "🫡",
        };

        public static string ApplyShortcuts(string text)
        {
            foreach (var kv in Shortcuts) text = text.Replace(kv.Key, kv.Value);
            return text;
        }

        // GIF acceptés : seulement les serveurs d'images de Giphy et Tenor. Une image venant de n'importe quel
        // site permettrait à son propriétaire de voir l'IP de tous ceux qui l'affichent.
        static readonly Regex AllowedGif = new Regex(@"^https://(media\d?\.giphy\.com|i\.giphy\.com|media\.tenor\.com|c\.tenor\.com)/[^\s""'<>]+$", RegexOptions.IgnoreCase);

        public static bool IsAllowedGif(string url) => !string.IsNullOrEmpty(url) && url.Length < 400 && AllowedGif.IsMatch(url);

        public static string GifFromLink(string text)
        {
            var t = (text ?? "").Trim();
            var page = Regex.Match(t, @"^https?://(www\.)?giphy\.com/(gifs|stickers)/(?:[\w-]*-)?(\w+)/?(\?.*)?$", RegexOptions.IgnoreCase);
            if (page.Success) return $"https://media.giphy.com/media/{page.Groups[3].Value}/giphy.gif";
            return IsAllowedGif(t) ? t : null;
        }

        public static ChatMessage Sanitize(string from, object text, object gif)
        {
            var t = (text as string ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length > MaxText) t = t.Substring(0, MaxText);
            var g = gif as string;
            if (!IsAllowedGif(g)) g = null;
            if (t.Length == 0 && g == null) return null;
            return new ChatMessage { From = from, Text = t, Gif = g };
        }
    }
}
