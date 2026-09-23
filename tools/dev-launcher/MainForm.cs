namespace Hispania.DevLauncher;

public sealed class MainForm : Form
{
    private readonly LauncherRuntime runtime = new();
    private readonly Label backend = new(), frontend = new();
    private readonly TextBox logs = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox root = new() { Dock = DockStyle.Fill };
    private readonly TextBox movies = new() { Dock = DockStyle.Fill };
    private readonly TextBox ffprobe = new() { Dock = DockStyle.Fill };
    private readonly TextBox tokenFile = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private LauncherSettings settings = LauncherConfig.Load();
    private Button open = new();
    private Button restart = new();
    private Button start = new();
    private Button stop = new();
    private bool transitioning;
    public MainForm()
    {
        Text = "HISPANIA Dev Launcher"; Width = 900; Height = 620; MinimumSize = new Size(650, 450); BackColor = Color.FromArgb(8, 10, 16); ForeColor = Color.White;
        root.Text = settings.RepositoryRoot ?? ProjectLocator.FindRoot(AppContext.BaseDirectory) ?? "";
        movies.Text = settings.MoviesPath ?? ""; ffprobe.Text = settings.FfprobePath ?? "ffprobe"; tokenFile.Text = TokenPath();
        start = new Button { Text = "Iniciar servidores" }; stop = new Button { Text = "Detener servidores", Enabled = false }; restart = new Button { Text = "Reiniciar" }; open = new Button { Text = "Abrir HISPANIA", Enabled = false };
        var browse = new Button { Text = "Elegir raíz…" }; browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = root.Text }; if (dialog.ShowDialog() == DialogResult.OK) root.Text = dialog.SelectedPath; };
        var openToken = new Button { Text = "Abrir archivo…" }; openToken.Click += (_, _) => { if (string.IsNullOrWhiteSpace(root.Text)) return; _ = LauncherTokenFile.Load(root.Text); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", LauncherTokenFile.PathFor(root.Text)) { UseShellExecute = true }); };
        root.TextChanged += (_, _) => tokenFile.Text = TokenPath();
        start.Click += async (_, _) => await Start(start, stop); stop.Click += async (_, _) => await Stop(start, stop); restart.Click += async (_, _) => await Restart(start, stop); open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://127.0.0.1:5173") { UseShellExecute = true });
        runtime.Log += (name, line) => BeginInvoke(() => logs.AppendText($"[{name}] {line}{Environment.NewLine}")); runtime.ReadyChanged += (name, ready) => BeginInvoke(() => SetState(name, ready));
        FormClosing += async (_, e) => { if (runtime.Processes.Count == 0) return; e.Cancel = true; if (MessageBox.Show("Hay servidores activos. ¿Detenerlos y salir?", "HISPANIA", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { await Stop(start, stop); Close(); } };
        var status = new TableLayoutPanel { Dock = DockStyle.Top, Height = 90, ColumnCount = 2, RowCount = 2 }; status.Controls.Add(new Label { Text = "Backend", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); status.Controls.Add(backend, 1, 0); status.Controls.Add(new Label { Text = "Frontend", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1); status.Controls.Add(frontend, 1, 1); SetState("backend", false); SetState("frontend", false);
        var path = new TableLayoutPanel { Dock = DockStyle.Top, Height = 132, ColumnCount = 3, RowCount = 4 }; path.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); path.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); path.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); AddPathRow(path, "Raíz:", root, browse, 0); AddPathRow(path, "Películas:", movies, null, 1); AddPathRow(path, "ffprobe:", ffprobe, null, 2); AddPathRow(path, "Archivo TMDB:", tokenFile, openToken, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 }; buttons.Controls.AddRange([start, stop, restart, open]);
        Controls.Add(logs); Controls.Add(buttons); Controls.Add(path); Controls.Add(status);
    }
    private void SetState(string name, bool ready) { var label = name == "backend" ? backend : frontend; label.Text = ready ? "Funcionando" : "Detenido"; label.ForeColor = ready ? Color.LightGreen : Color.LightGray; open.Enabled = runtime.Processes.Count > 0 && backend.Text == "Funcionando" && frontend.Text == "Funcionando"; }
    private static void AddPathRow(TableLayoutPanel table, string label, Control value, Control? action, int row) { table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); table.Controls.Add(value, 1, row); if (action is not null) table.Controls.Add(action, 2, row); }
    private async Task Start(Button start, Button stop)
    {
        if (!TryBeginTransition()) return;
        try
        {
            settings = settings with { RepositoryRoot = root.Text, MoviesPath = movies.Text, FfprobePath = ffprobe.Text };
            LauncherConfig.Save(settings);
            await runtime.StartAsync(root.Text, settings);
            start.Enabled = false; stop.Enabled = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "No se pudo iniciar", MessageBoxButtons.OK, MessageBoxIcon.Error); await runtime.StopAsync(); }
        finally { EndTransition(); }
    }
    private async Task Stop(Button start, Button stop)
    {
        if (!TryBeginTransition()) return;
        try { await runtime.StopAsync(); start.Enabled = true; stop.Enabled = false; open.Enabled = false; }
        finally { EndTransition(); }
    }
    private async Task Restart(Button start, Button stop)
    {
        if (!TryBeginTransition()) return;
        try
        {
            settings = settings with { RepositoryRoot = root.Text, MoviesPath = movies.Text, FfprobePath = ffprobe.Text };
            LauncherConfig.Save(settings);
            await runtime.StopAsync();
            await runtime.StartAsync(root.Text, settings);
            start.Enabled = false; stop.Enabled = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "No se pudo reiniciar", MessageBoxButtons.OK, MessageBoxIcon.Error); await runtime.StopAsync(); start.Enabled = true; stop.Enabled = false; open.Enabled = false; }
        finally { EndTransition(); }
    }
    private bool TryBeginTransition()
    {
        if (transitioning) return false;
        transitioning = true;
        start.Enabled = stop.Enabled = restart.Enabled = open.Enabled = false;
        return true;
    }
    private void EndTransition()
    {
        transitioning = false;
        restart.Enabled = true;
        open.Enabled = runtime.Processes.Count > 0 && backend.Text == "Funcionando" && frontend.Text == "Funcionando";
        if (runtime.Processes.Count == 0) { start.Enabled = true; stop.Enabled = false; }
        else { start.Enabled = false; stop.Enabled = true; }
    }
    private string TokenPath() => string.IsNullOrWhiteSpace(root.Text) ? "Selecciona la raíz del repositorio" : LauncherTokenFile.PathFor(root.Text);
}
