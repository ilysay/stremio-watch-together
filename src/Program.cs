namespace StremioWT
{
    static class Program
    {
        internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StremioWT");
        static readonly string ConfigFile = Path.Combine(DataDir, "config.json");
        static readonly string LogFile = Path.Combine(DataDir, "log.txt");
        static StreamWriter log;
        static Mutex singleInstance;

        // Version « majeure.mineure » : l'hôte et ses amis doivent avoir la même (vérifié par le relay).
        internal static readonly string ProtocolVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(2);
        internal static readonly string ScriptVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        internal static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            L.Init();
            // Un seul exemplaire à la fois : deux copies ouvriraient deux salons et piloteraient le même Stremio.
            singleInstance = new Mutex(true, @"Local\StremioWatchTogether", out bool firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show(L.T("StremioWT est déjà ouvert sur ce PC.\nUtilise la fenêtre déjà ouverte, ou ferme-la avant d'en relancer une."),
                    "Stremio Watch Together", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenLog();
            Telemetry.Send("launch");
            Application.Run(new MainForm());
        }

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
                Log("StremioWT " + Assembly.GetExecutingAssembly().GetName().Version);
            }
            catch { }
            Console.SetOut(new UiWriter());
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

        internal static void Fail(string msg)
        {
            Log("arrêt : " + msg);
            MessageBox.Show(msg, "Stremio Watch Together", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Environment.Exit(1);
        }

        internal static void Restart()
        {
            Log("relance (changement de langue)");
            try { singleInstance.ReleaseMutex(); singleInstance.Dispose(); } catch { }
            Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true });
            Environment.Exit(0);
        }

        internal static T Sta<T>(Func<T> f)
        {
            T result = default;
            var t = new Thread(() => { try { result = f(); } catch { } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return result;
        }

        static Dictionary<string, object> LoadConfig()
        {
            try { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile)) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        internal static void SaveConfig(Dictionary<string, object> c)
        {
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
