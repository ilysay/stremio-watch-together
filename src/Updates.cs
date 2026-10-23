using static StremioWT.Program;

namespace StremioWT
{
    static class Updates
    {
        public const string RepoUrl = "https://github.com/ilysay/stremio-watch-together";
        public const string DownloadUrl = RepoUrl + "/releases/latest";
        const string LatestApi = "https://api.github.com/repos/ilysay/stremio-watch-together/releases/latest";

        static Version Current => Assembly.GetExecutingAssembly().GetName().Version;

        // Numéro de la dernière version publiée si elle est plus récente que celle-ci, sinon null (aussi en cas d'erreur réseau).
        public static async Task<string> NewerVersion()
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, LatestApi))
                using (var cts = new CancellationTokenSource(6000))
                {
                    req.Headers.UserAgent.ParseAdd("StremioWT/" + Current.ToString(3)); // exigé par l'API GitHub
                    using (var res = await Http.SendAsync(req, cts.Token))
                    {
                        if (!res.IsSuccessStatusCode) return null;
                        var tag = Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync())["tag_name"] as string;
                        return IsNewer(tag, Current) ? tag.TrimStart('v', 'V') : null;
                    }
                }
            }
            catch (Exception e) { Log("mise à jour : " + e.Message); return null; }
        }

        internal static bool IsNewer(string tag, Version current) =>
            Version.TryParse((tag ?? "").TrimStart('v', 'V'), out var latest) && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
    }
}
