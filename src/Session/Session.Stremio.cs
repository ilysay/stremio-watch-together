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
                    if (type == "chat")
                    {
                        var text = msg.TryGetValue("text", out var ct) ? ct as string : "";
                        SendChat(text, Chat.GifFromLink(text) != null ? Chat.GifFromLink(text) : null);
                        return;
                    }
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
                    if (type != "need-sync") Program.Log($"action locale : {type}" + (msg.TryGetValue("time", out var tm) ? $" à {Convert.ToDouble(tm) / 1000:0}s" : ""));
                    _ = Post(msg);
                }
                catch { }
            };
            c.Closed += () => { if (!releasing) _ = Task.Run(ReattachStremio); };
            await c.Connect(wsUrl);
            // Brancher un outil de débogage remet le fond de page de Stremio en blanc opaque, ce qui masque
            // la vidéo de mpv (dessinée derrière l'interface transparente) : on son, image blanche.
            // On impose un fond transparent pour toute la durée de la connexion.
            await KeepTransparent(c);
            // Une autre connexion de débogage (ou un rechargement) peut remettre le blanc : on réimpose régulièrement.
            _ = Task.Run(async () => { while (cdp == null || cdp == c) { await Task.Delay(3000); if (releasing) return; try { await KeepTransparent(c); } catch { return; } } });
            var script = StremioApp.ReadInject();
            await c.Call("Runtime.enable");
            await c.Call("Page.enable");
            await c.Call("Runtime.addBinding", new Dictionary<string, object> { ["name"] = "__wtSend" });
            await c.Call("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { ["source"] = script });
            await c.Eval(script);
            // Stremio resté ouvert depuis une version précédente de StremioWT : l'ancien script est encore dans la page.
            // On recharge l'interface une fois ; le nouveau script s'y installe tout seul (addScriptToEvaluateOnNewDocument).
            var loaded = await c.Eval("window.__wt && window.__wt.version") as string;
            var lateHook = await c.Eval("!!(window.__wt && window.__wt.needsReload)") is bool late && late;
            if (loaded != StremioApp.InjectVersion || lateHook)
            {
                Program.Log(lateHook ? "Stremio 5 déjà chargé : rechargement de l'interface pour installer le script dès le départ"
                    : $"script {loaded ?? "ancien"} trouvé dans Stremio (attendu {StremioApp.InjectVersion}) : rechargement de l'interface");
                await c.Call("Page.reload");
                for (int i = 0; i < 40 && (await c.Eval("window.__wt && window.__wt.version").ContinueWith(t => t.IsFaulted ? null : t.Result) as string) != StremioApp.InjectVersion; i++)
                    await Task.Delay(250);
            }
            cdp = c;
            PushStatus();
        }

        volatile bool releasing;

        static Task KeepTransparent(Cdp c) => c.Call("Emulation.setDefaultBackgroundColorOverride", new Dictionary<string, object>
        {
            ["color"] = new Dictionary<string, object> { ["r"] = 0, ["g"] = 0, ["b"] = 0, ["a"] = 0 },
        });

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

        // La liaison avec Stremio s'est coupée : soit Stremio est fermé (on s'arrête),
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
            await ReportEnd();
            Program.Fail(L.T("Stremio a été fermé. Fin du Watch Together."));
        }
    }
}
