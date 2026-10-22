namespace StremioWT
{
    class ChatFeed : FlowLayoutPanel
    {
        const int MaxRows = 120;
        static readonly Color[] NameColors =
        {
            Color.FromArgb(84, 152, 255), Color.FromArgb(61, 220, 132), Color.FromArgb(255, 170, 70), Color.FromArgb(230, 110, 220),
            Color.FromArgb(90, 210, 220), Color.FromArgb(255, 110, 110), Color.FromArgb(190, 160, 255), Color.FromArgb(240, 220, 90),
        };

        public ChatFeed()
        {
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoScroll = true;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            MainForm.DarkScrollbars(this);
        }

        int RowWidth => ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;

        public void AddSystem(string text) => AddRow(new ChatMessage { System = true, Text = text });

        public void AddMessage(ChatMessage m) => AddRow(m);

        void AddRow(ChatMessage m)
        {
            var row = new Panel { Width = RowWidth, Height = 0, Margin = new Padding(0, 2, 0, 2), BackColor = Theme.Card };
            int y = 0;
            if (m.System)
            {
                var l = new Label { Text = m.Time.ToString("HH:mm") + "  " + m.Text, AutoSize = true, MaximumSize = new Size(RowWidth, 0), ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Card };
                row.Controls.Add(l);
                y = l.PreferredHeight;
            }
            else
            {
                var who = m.Mine ? L.T("Toi") : m.From;
                var name = new Label { Text = who, AutoSize = true, Font = Theme.Bold, ForeColor = m.Mine ? Theme.Muted : ColorFor(who), BackColor = Theme.Card, Location = new Point(0, 0) };
                row.Controls.Add(name);
                y = name.PreferredHeight;
                if (!string.IsNullOrEmpty(m.Text))
                {
                    var text = new Label { Text = m.Text, AutoSize = true, MaximumSize = new Size(RowWidth - 4, 0), Font = new Font("Segoe UI Emoji", 10f), ForeColor = Theme.Text, BackColor = Theme.Card, Location = new Point(2, y), UseMnemonic = false };
                    row.Controls.Add(text);
                    y += text.PreferredHeight;
                }
                if (m.Gif != null)
                {
                    var pic = new PictureBox { Location = new Point(2, y + 2), Size = new Size(160, 110), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Input };
                    row.Controls.Add(pic);
                    y += 116;
                    LoadGif(pic, m.Gif, this);
                }
            }
            row.Height = y + 2;
            Controls.Add(row);
            while (Controls.Count > MaxRows) { var old = Controls[0]; Controls.RemoveAt(0); old.Dispose(); }
            ScrollControlIntoView(row);
        }

        static Color ColorFor(string name) => NameColors[Math.Abs((name ?? "").Aggregate(17, (h, c) => h * 31 + c)) % NameColors.Length];

        // Charge un GIF animé (le flux reste ouvert : WinForms en a besoin pour l'animation).
        public static async void LoadGif(PictureBox box, string url, Control owner)
        {
            if (!Chat.IsAllowedGif(url)) return;
            try
            {
                var bytes = await Program.Http.GetByteArrayAsync(url);
                var img = Image.FromStream(new MemoryStream(bytes));
                if (box.IsDisposed) return;
                if (img.Width > 0 && img.Height > 0)
                {
                    double ratio = Math.Min(box.Width / (double)img.Width, box.Height / (double)img.Height);
                    box.Size = new Size(Math.Max(40, (int)(img.Width * ratio)), Math.Max(30, (int)(img.Height * ratio)));
                }
                box.Image = img;
            }
            catch { }
        }
    }

    class EmotePicker : Form
    {
        public EmotePicker(Action<string> pick)
        {
            Dialogs.Dark(this);
            Text = L.T("Émotes");
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            const int cell = 40, cols = 8;
            int rows = (Chat.Emotes.Length + cols - 1) / cols;
            ClientSize = new Size(cols * cell + 16, rows * cell + 56);
            for (int i = 0; i < Chat.Emotes.Length; i++)
            {
                var e = Chat.Emotes[i];
                var b = new Label { Text = e, Font = new Font("Segoe UI Emoji", 16f), Size = new Size(cell, cell), Location = new Point(8 + i % cols * cell, 8 + i / cols * cell), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, ForeColor = Theme.Text };
                b.MouseEnter += (s, a) => b.BackColor = Theme.Card;
                b.MouseLeave += (s, a) => b.BackColor = Theme.Background;
                b.Click += (s, a) => pick(e);
                Controls.Add(b);
            }
            var hint = Dialogs.Text(L.T("Raccourcis") + " : :mdr: :feu: :coeur: :mort: :pop: :bravo: …", Theme.Small, Theme.Muted, 8, rows * cell + 20);
            Controls.Add(hint);
            Deactivate += (s, a) => Close();
        }
    }

    class GifPicker : Form
    {
        readonly InputBox search = new InputBox();
        readonly FlowLayoutPanel results = new FlowLayoutPanel { AutoScroll = true, BackColor = Theme.Card };
        readonly Label status = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Background };
        readonly Action<string> pick;
        CancellationTokenSource running;

        public GifPicker(Action<string> pick)
        {
            this.pick = pick;
            Dialogs.Dark(this);
            Text = "GIF";
            ClientSize = new Size(520, 520);
            search.SetBounds(16, 16, 488, 36);
            search.BackColor = Theme.Background;
            status.SetBounds(16, 58, 488, 36);
            results.SetBounds(16, 96, 488, 408);
            MainForm.DarkScrollbars(results);
            Controls.AddRange(new Control[] { search, status, results });
            var timer = new System.Windows.Forms.Timer { Interval = 450 };
            timer.Tick += (s, e) => { timer.Stop(); _ = Search(search.Box.Text.Trim()); };
            search.Box.TextChanged += (s, e) => { timer.Stop(); timer.Start(); };
            search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                var link = Chat.GifFromLink(search.Box.Text);
                if (link != null) { pick(link); Close(); }
            };
            Shown += (s, e) => { search.Box.Focus(); _ = Search(""); };
        }

        async Task Search(string query)
        {
            running?.Cancel();
            var cts = running = new CancellationTokenSource();
            status.Text = query.Length == 0 ? L.T("Tendances : tape pour chercher, ou colle un lien Giphy puis Entrée") : L.T("Recherche…");
            try
            {
                var url = Chat.GifServer + "?q=" + Uri.EscapeDataString(query) + "&lang=" + L.Lang;
                using (var res = await Program.Http.GetAsync(url, cts.Token))
                {
                    if ((int)res.StatusCode == 503)
                    {
                        status.Text = L.T("Recherche de GIF pas encore activée. Colle un lien Giphy dans la barre ci-dessus puis Entrée.");
                        return;
                    }
                    var data = Program.Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync());
                    if (cts.IsCancellationRequested) return;
                    results.Controls.Clear();
                    var list = (data.TryGetValue("results", out var r) ? r as System.Collections.ArrayList : null) ?? new System.Collections.ArrayList();
                    foreach (Dictionary<string, object> g in list.OfType<Dictionary<string, object>>())
                    {
                        var gif = g.TryGetValue("gif", out var gg) ? gg as string : null;
                        var preview = g.TryGetValue("preview", out var pp) ? pp as string : gif;
                        if (!Chat.IsAllowedGif(gif)) continue;
                        var pic = new PictureBox { Size = new Size(150, 110), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Input, Cursor = Cursors.Hand, Margin = new Padding(4) };
                        pic.Click += (s, e) => { pick(gif); Close(); };
                        results.Controls.Add(pic);
                        ChatFeed.LoadGif(pic, preview, this);
                    }
                    status.Text = L.T(list.Count == 0 ? "Aucun GIF trouvé." : (query.Length == 0 ? "Tendances : clique sur un GIF pour l'envoyer" : "Clique sur un GIF pour l'envoyer"));
                }
            }
            catch (OperationCanceledException) { }
            catch { status.Text = L.T("Recherche impossible pour le moment. Tu peux coller un lien Giphy puis Entrée."); }
        }
    }
}
