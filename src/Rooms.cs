using static StremioWT.Program;

namespace StremioWT
{
    static class Rooms
    {
        const int RelayPort = 8787;

        internal static string DefaultName() =>
            Config.TryGetValue("name", out var n) && n is string s && s.Length > 0 ? s : (Environment.UserName ?? L.T("Invité"));

        // Code d'invitation (long ou court) présent dans le presse-papier, sauf celui qu'on a soi-même créé en hébergeant.
        // already : code d'une soirée déjà rejointe ; resté dans le presse-papier, il ne doit pas reconnecter tout seul.
        internal static (string code, bool already)? ClipboardInvite()
        {
            var clip = GetClipboardText();
            string Saved(string key) => Config.TryGetValue(key, out var v) ? v as string : null;
            var invite = Invite.Parse(clip);
            if (invite != null)
            {
                if (Saved("lastHosted") is string lh && clip.Contains(lh)) return null;
                return (clip.Trim(), Saved("lastJoined") == invite.Value.server + "/" + invite.Value.room);
            }
            var code = Invite.ShortIn(clip, anywhere: true);
            if (code == null || Saved("lastHostedShort") == code) return null;
            return (Invite.ShowShort(code), Saved("lastJoinedShort") == code);
        }

        internal static string RequireStremio()
        {
            var exe = StremioApp.FindExe(Config);
            if (exe != null) return exe;
            Telemetry.Error("stremio-missing");
            throw new Exception(IsMac
                ? L.T("Aucun navigateur compatible trouvé (Chrome, Edge ou Brave). Installe Google Chrome puis relance.")
                : !IsWindows
                ? L.T("Ni Stremio 4 ni navigateur compatible trouvé. Installe Stremio 4 (.deb) ou Google Chrome / Chromium, puis relance.")
                : L.T("Stremio est introuvable. Installe Stremio pour Windows depuis https://www.stremio.com/downloads puis relance."));
        }

        static void Remember(string name, string stremioExe)
        {
            Config["name"] = name;
            Config["stremioPath"] = stremioExe;
            SaveConfig(Config);
        }

        // code : le code long (lien d'invitation) ; shortCode : le code court à montrer, ou null si le serveur n'a pas répondu.
        internal static async Task<(Session session, string code, string shortCode)> Host(string name, bool local)
        {
            var stremioExe = RequireStremio();
            var relay = new Relay();
            int port = relay.Start(RelayPort, lan: local);
            if (!await Relay.Answers(port))
            {
                // Port 8787 occupé de façon invisible (ancienne session) : on en prend un libre.
                Log($"relay : le port {port} ne répond pas, passage sur un autre port");
                relay = new Relay();
                port = relay.Start(0, lan: local);
            }
            var room = RandomHex(3).ToUpperInvariant();
            string publicUrl;
            if (local)
            {
                publicUrl = LanUrl(port);
                Console.WriteLine(L.T("Mode réseau local uniquement."));
            }
            else
            {
                Console.WriteLine(L.T("Ouverture de l'accès Internet…"));
                try { publicUrl = await Tunnel.Start(port); }
                catch (Exception e) { Telemetry.Error("tunnel"); throw new Exception(L.F("Accès Internet impossible ({0}). Si vous êtes sur le même wifi, coche l'option « même wifi seulement ».", e.Message)); }
            }
            var code = Invite.Make(publicUrl, room);
            var shortCode = await Invite.RegisterShort(publicUrl, room);
            Config["lastHosted"] = code;
            Config["lastHostedShort"] = shortCode ?? "";
            SetClipboardText(Invite.Link(code));
            Console.WriteLine(L.T("Code d'invitation copié : colle-le à tes amis."));
            Remember(name, stremioExe);
            _ = Telemetry.Send("host", 1);
            return (new Session($"http://127.0.0.1:{port}", room, name, true, stremioExe, relay), code, shortCode);
        }

        // Message à coller aux amis : le lien de téléchargement pour ceux qui n'ont pas l'appli, puis le code
        // (l'appli retrouve le code WT-… n'importe où dans le texte copié).
        internal static string InviteMessage(string code) => L.F("Rejoins ma soirée film sur Stremio !\n1. Télécharge l'appli : {0}\n2. Copie ce message puis ouvre StremioWT.\n\n{1}", Invite.Link(code), code);

        internal static async Task<Session> Join(string name, string inviteText)
        {
            var invite = Invite.Parse(inviteText);
            var shortCode = invite == null ? Invite.ShortIn(inviteText, anywhere: false) ?? Invite.ShortIn(inviteText, anywhere: true) : null;
            if (invite == null && shortCode != null) invite = await Invite.ResolveShort(shortCode);
            if (invite == null) throw new Exception(L.T("Ce code n'est pas valide : il doit commencer par WT-"));
            var stremioExe = RequireStremio();
            Config["lastJoined"] = invite.Value.server + "/" + invite.Value.room;
            Config["lastJoinedShort"] = shortCode ?? "";
            Remember(name, stremioExe);
            return new Session(invite.Value.server, invite.Value.room, name, false, stremioExe, null);
        }

        static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")));
        }

        static string LanUrl(int port)
        {
            var ip = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback && i.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return $"http://{ip?.ToString() ?? "127.0.0.1"}:{port}";
        }
    }
}
