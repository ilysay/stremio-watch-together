using static StremioWT.Program;

namespace StremioWT
{
    static class Tunnel
    {
        static readonly string CloudflaredUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/" +
            (IsWindows ? "cloudflared-windows-amd64.exe"
             : IsMac ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-darwin-arm64.tgz" : "cloudflared-darwin-amd64.tgz")
             : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-linux-arm64" : "cloudflared-linux-amd64");
        static readonly string CloudflaredExe = Path.Combine(DataDir, IsWindows ? "cloudflared.exe" : "cloudflared");

        internal static async Task<string> Start(int port)
        {
            if (!File.Exists(CloudflaredExe))
            {
                Console.WriteLine(L.T("Premier lancement en hôte : téléchargement de cloudflared (outil officiel Cloudflare, ~60 Mo)…"));
                Console.WriteLine(L.T("Ça peut prendre une minute, une seule fois."));
                Directory.CreateDirectory(DataDir);
                var tmp = CloudflaredExe + ".part";
                using (var res = await Http.GetAsync(CloudflaredUrl, HttpCompletionOption.ResponseHeadersRead))
                using (var file = File.Create(tmp))
                {
                    res.EnsureSuccessStatusCode();
                    await res.Content.CopyToAsync(file);
                }
#if !NETFRAMEWORK
                // Mac : cloudflared est livré dans une archive .tgz qui contient le seul fichier « cloudflared ».
                if (IsMac)
                {
                    var archive = tmp + ".tgz";
                    File.Move(tmp, archive);
                    using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
                    using (var tar = new System.Formats.Tar.TarReader(gz))
                        for (var entry = tar.GetNextEntry(); entry != null; entry = tar.GetNextEntry())
                            if (Path.GetFileName(entry.Name) == "cloudflared") { entry.ExtractToFile(tmp, true); break; }
                    File.Delete(archive);
                }
                if (!IsWindows) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#endif
                File.Move(tmp, CloudflaredExe);
            }
            var psi = new ProcessStartInfo(CloudflaredExe, $"tunnel --no-autoupdate --url http://localhost:{port}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            var proc = Process.Start(psi);
            Job.KillOnExit(proc);
            var found = new TaskCompletionSource<string>();
            DataReceivedEventHandler onLine = (o, e) =>
            {
                var line = e.Data ?? "";
                if (line.Contains(" ERR ") || line.Contains(" WRN ")) Log("cloudflared: " + line);
                else if (line.Contains("Registered tunnel connection")) Log("cloudflared: tunnel prêt");
                var m = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com");
                if (m.Success) found.TrySetResult(m.Value);
            };
            proc.ErrorDataReceived += onLine;
            proc.OutputDataReceived += onLine;
            proc.EnableRaisingEvents = true;
            proc.Exited += (o, e) => found.TrySetException(new Exception(L.T("cloudflared s'est arrêté")));
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            if (await Task.WhenAny(found.Task, Task.Delay(45000)) != found.Task) throw new Exception(L.T("le tunnel ne répond pas"));
            var url = await found.Task;
            // L'adresse met quelques secondes à devenir joignable.
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    using (var cts = new CancellationTokenSource(4000))
                    using (var res = await Http.GetAsync(url, cts.Token))
                        if (res.IsSuccessStatusCode) return url;
                }
                catch { }
                await Task.Delay(1000);
            }
            return url;
        }
    }

    // Objet « job » Windows : cloudflared est tué avec StremioWT, même si celui-ci plante.
    static class Job
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInfo info, int length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimitInfo
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimitInfo
        {
            public BasicLimitInfo Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        static IntPtr handle;

        public static void KillOnExit(Process p)
        {
            if (!IsWindows) { AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { if (!p.HasExited) p.Kill(); } catch { } }; return; }
            try
            {
                if (handle == IntPtr.Zero)
                {
                    handle = CreateJobObject(IntPtr.Zero, null);
                    var info = new ExtendedLimitInfo { Basic = new BasicLimitInfo { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
                    SetInformationJobObject(handle, 9, ref info, Marshal.SizeOf(typeof(ExtendedLimitInfo)));
                }
                AssignProcessToJobObject(handle, p.Handle);
            }
            catch { }
        }
    }
}
