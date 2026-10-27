namespace StremioWT
{
    partial class Session
    {
        public event Action<List<StremioFilm>> HistoryFound;

        public async Task<List<StremioFilm>> ReadHistory()
        {
            if (cdp == null) return new List<StremioFilm>();
            return Journal.ParseHistory(await cdp.Eval(Journal.HistoryScript));
        }

        async Task AttachStremio(string wsUrl)
        {
            var c = new Cdp();
            c.BindingCalled += payload =>
            {
                try
                {
                    var msg = Program.Json.Deserialize<Dictionary<string, object>>(payload);
                    var type = msg.TryGetValue("type", out var t) ? t as string : null;
                    if (type == "debug") { Program.Log("stremio : " + (msg.TryGetValue("text", out var x) ? x : "")); return; }
                    if (type == "torrent") { OnTorrent(msg); return; }
                    if (type == "chat")
                    {
                        var text = msg.TryGetValue("text", out var ct) ? ct as string : "";
                        var gif = msg.TryGetValue("gif", out var cg) ? cg as string : null;
                        SendChat(text, gif ?? Chat.GifFromLink(text));
                        return;
                    }
                    if (type == "gif-search") { _ = SearchGifsForPage(msg); return; }
                    if (type != null && type.StartsWith("roulette")) { OnRoulettePage(type, msg); return; }
                    if (type == "watched")
                    {
                        var imdb = msg.TryGetValue("imdb", out var im) ? im as string : null;
                        if (imdb != null) _ = OnWatched(imdb, msg.TryGetValue("kind", out var kd) ? kd as string : "movie");
                        return;
                    }
                    if (type == "buffer")
                    {
                        OnBuffer(name, msg.TryGetValue("on", out var o) && o is bool ob && ob);
                        if (!isHost) _ = Post(msg);
                        return;
                    }
                    if (!PageTypes.Contains(type)) return;
                    if (type == "open") OnOpen(name, msg);
                    if (type == "need-sync" && isHost) OnReady(name);
                    if (type != "need-sync") Program.Log($"action locale : {type}" + (msg.TryGetValue("time", out var tm) ? $" à {Convert.ToDouble(tm) / 1000:0}s" : ""));
                    _ = Post(msg);
                }
                catch { }
            };
            c.Closed += () => { if (!releasing) _ = Task.Run(ReattachStremio); };
            await c.Connect(wsUrl);
            // Une autre connexion de débogage (ou un rechargement) peut remettre le blanc : réimposé tant que c'est la nôtre.
            await StremioApp.Prepare(c, () => (cdp == null || cdp == c) && !releasing);
            cdp = c;
            PushStatus();
        }

        volatile bool releasing;

        // Fermeture de StremioWT : une fois déconnecté, le fond blanc reviendrait par-dessus la vidéo.
        // On recharge l'interface de Stremio en partant, ce qui lui rend sa transparence normale.
        // Ordre important (vérifié) : d'abord se déconnecter, puis recharger via une connexion brève
        // qui se ferme aussitôt ; recharger pendant la connexion principale ne suffit pas.
        public void ReleaseStremio()
        {
            var c = cdp;
            if (c == null) return;
            releasing = true;
            Program.Log("fermeture : déconnexion de Stremio puis rechargement de son interface (fond transparent)");
            try { c.Close(); } catch { }
            try
            {
                var url = StremioApp.FindPageWsUrl().GetAwaiter().GetResult();
                if (url == null) return;
                var brief = new Cdp();
                brief.Connect(url).Wait(2000);
                brief.Send("Page.reload");
                Thread.Sleep(300);
                brief.Close();
            }
            catch (Exception e) { Program.Log("fermeture : " + e.Message); }
        }

        // La liaison avec Stremio s'est coupée : soit Stremio est fermé (on attend qu'il revienne),
        // soit son interface a été rechargée (on se rebranche dessus).
        async Task ReattachStremio()
        {
            Program.Log("liaison Stremio coupée");
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                var url = await StremioApp.FindPageWsUrl();
                if (url != null)
                {
                    try { await AttachStremio(url); Console.WriteLine("Reconnecté à Stremio."); return; }
                    catch (Exception e) { Program.Log("rebranchement Stremio : " + e.Message); }
                }
                else if (Process.GetProcessesByName("stremio").Length + Process.GetProcessesByName("stremio-shell-ng").Length == 0) break;
            }
            // Stremio fermé (ou planté) : avant, la séance s'arrêtait. Le salon reste maintenant ouvert.
            await WaitForStremioBack();
        }
    }
}
