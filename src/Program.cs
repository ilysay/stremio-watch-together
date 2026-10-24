namespace StremioWT
{
    // Cœur commun à toutes les plateformes : dossier de données, configuration, journal technique.
    // Le démarrage, les messages d'erreur et le presse-papier sont dans Platform/Program.<plateforme>.cs.
    static partial class Program
    {
        internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StremioWT");
        static readonly string ConfigFile = Path.Combine(DataDir, "config.json");
        static readonly string LogFile = Path.Combine(DataDir, "log.txt");
        static StreamWriter log;

        // Version « majeure.mineure » : l'hôte et ses amis doivent avoir la même (vérifié par le relay).
        internal static readonly string ProtocolVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(2);
        internal static readonly string ScriptVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

        internal static readonly JsonCodec Json = new JsonCodec();
        internal static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        internal static Dictionary<string, object> Config = LoadConfig();

        // Console.WriteLine sert de « message pour l'utilisateur » : écrit dans le journal et affiché dans la fenêtre.
        internal static event Action<string> Output;

        static void OpenLog()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (File.Exists(LogFile)) File.Copy(LogFile, Path.Combine(DataDir, "log-precedent.txt"), true);
                log = new StreamWriter(new FileStream(LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
                Log("StremioWT " + Assembly.GetExecutingAssembly().GetName().Version + " · " + RuntimeInformation.OSDescription);
            }
            catch { }
            Console.SetOut(new UiWriter());
        }

        // Dernières lignes du journal (pour un rapport de bug) ; le fichier reste ouvert en écriture.
        internal static string LogTail(int lines)
        {
            try
            {
                lock (LogFile)
                using (var r = new StreamReader(new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
                {
                    var all = r.ReadToEnd().Split('\n');
                    return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
                }
            }
            catch { return "(journal illisible)"; }
        }

        internal static void Log(string line)
        {
            try { lock (LogFile) log?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line); } catch { }
        }

        class UiWriter : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) { }
            public override void WriteLine(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                Log(value.Trim());
                Output?.Invoke(value.Trim());
            }
        }

        // Invitation reçue au lancement (lien « stremiowt://join/WT-… » cliqué sur la page d'invitation).
        internal static string LaunchInvite;
        internal static string InviteFromArgs(string[] args) => args.FirstOrDefault(a => Invite.Parse(a) != null);

        internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        internal static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        static Dictionary<string, object> LoadConfig()
        {
            try { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile)) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        internal static void SaveConfig(Dictionary<string, object> c)
        {
            if (TestMode.Active) return;
            try { Directory.CreateDirectory(DataDir); File.WriteAllText(ConfigFile, Json.Serialize(c)); Log("config enregistrée"); }
            catch (Exception e) { Log("config non enregistrée : " + e.Message); }
        }

        internal static async Task<T> WithCancel<T>(this Task<T> task, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(() => tcs.TrySetResult(true)))
                if (await Task.WhenAny(task, tcs.Task) != task) throw new OperationCanceledException();
            return await task;
        }
    }
}
