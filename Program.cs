using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrinketedDesktop;

// ── Design System (matching Trinketed webapp) ──────────────────────
static class Theme
{
    // Surface palette
    public static readonly Color BgDeep = Color.FromArgb(11, 12, 15);        // #0B0C0F
    public static readonly Color BgBase = Color.FromArgb(21, 23, 27);        // #15171B
    public static readonly Color BgRaised = Color.FromArgb(30, 32, 40);      // #1E2028
    public static readonly Color BgElevated = Color.FromArgb(42, 46, 54);    // #2A2E36
    public static readonly Color BgHover = Color.FromArgb(50, 54, 64);       // #323640

    // Borders
    public static readonly Color BorderSubtle = Color.FromArgb(37, 40, 48);   // #252830
    public static readonly Color BorderDefault = Color.FromArgb(54, 58, 69);  // #363A45

    // Gold palette
    public static readonly Color Gold = Color.FromArgb(212, 170, 68);         // #D4AA44
    public static readonly Color GoldDim = Color.FromArgb(156, 123, 43);      // #9C7B2B
    public static readonly Color GoldGlow = Color.FromArgb(246, 200, 107);    // #F6C86B

    // Semantic
    public static readonly Color Positive = Color.FromArgb(34, 197, 94);      // #22C55E
    public static readonly Color Negative = Color.FromArgb(239, 68, 68);      // #EF4444
    public static readonly Color AccentBlue = Color.FromArgb(59, 130, 246);   // #3B82F6

    // Text
    public static readonly Color TextPrimary = Color.FromArgb(244, 244, 245);  // #F4F4F5
    public static readonly Color TextSecondary = Color.FromArgb(156, 163, 175); // #9CA3AF
    public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);    // #6B7280
    public static readonly Color TextDim = Color.FromArgb(55, 58, 69);         // #373A45
}

// ── Entry Point ────────────────────────────────────────────────────
static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}

// ── Data ───────────────────────────────────────────────────────────
class ReleaseInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Body { get; set; } = "";
}

class AddonInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "#D4AA44";

    public System.Drawing.Color ParsedColor
    {
        get
        {
            try
            {
                if (Color.StartsWith("#") && Color.Length == 7)
                {
                    int r = Convert.ToInt32(Color.Substring(1, 2), 16);
                    int g = Convert.ToInt32(Color.Substring(3, 2), 16);
                    int b = Convert.ToInt32(Color.Substring(5, 2), 16);
                    return System.Drawing.Color.FromArgb(r, g, b);
                }
            }
            catch { }
            return Theme.Gold;
        }
    }
}

// ── Tray Application Context ──────────────────────────────────────
class TrayContext : ApplicationContext
{
    const string AppVersion = "1.0.0";
    const string AppRepo = "Trinketed/desktop";
    const string Repo = "Trinketed/addon";
    const string AddonName = "Trinketed";
    const string TocFile = "Trinketed.toc";
    const string AppName = "TrinketedDesktop";
    const int CheckIntervalMs = 30 * 60 * 1000;

    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly HttpClient _http;
    InstallForm? _form;
    string? _addOnsPath;
    ReleaseInfo? _latestRelease;
    string? _lastNotifiedVersion;

    public TrayContext()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", $"TrinketedDesktop/{AppVersion}");

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Trinketed Desktop",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => ShowInstallWindow();
        _tray.BalloonTipClicked += (_, _) => ShowInstallWindow();

        _addOnsPath = FindAddOnsPath();

        _timer = new System.Windows.Forms.Timer { Interval = CheckIntervalMs };
        _timer.Tick += async (_, _) =>
        {
            await CheckForUpdate(true);
            await CheckAppUpdate();
        };
        _timer.Start();

        // Auto-open install window on first launch
        ShowInstallWindow();

        _ = CheckForUpdate(true);
        _ = CheckAppUpdate();
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.BackColor = Theme.BgRaised;
        menu.ForeColor = Theme.TextSecondary;
        menu.Renderer = new DarkMenuRenderer();

        menu.Items.Add("Open", null, (_, _) => ShowInstallWindow());
        menu.Items.Add("Check for Updates", null, async (_, _) =>
        {
            _lastNotifiedVersion = null;
            await CheckForUpdate(true);
        });
        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsInStartup() };
        startupItem.Click += (_, _) =>
        {
            if (IsInStartup()) RemoveFromStartup(); else AddToStartup();
            startupItem.Checked = IsInStartup();
        };
        menu.Items.Add(startupItem);

        menu.Items.Add("Open AddOns Folder", null, (_, _) =>
        {
            if (_addOnsPath != null && Directory.Exists(_addOnsPath))
                Process.Start("explorer.exe", _addOnsPath);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });

        return menu;
    }

    async Task CheckForUpdate(bool showBalloon)
    {
        try
        {
            var release = await GetLatestRelease();
            if (release == null) return;
            _latestRelease = release;
            var installed = GetInstalledVersion();

            if (showBalloon && installed != release.Version && _lastNotifiedVersion != release.Version)
            {
                _lastNotifiedVersion = release.Version;
                var fromVer = installed ?? "not installed";
                _tray.ShowBalloonTip(5000,
                    $"Trinketed {release.Version} Available",
                    $"Currently {fromVer}. Click to update.",
                    ToolTipIcon.Info);
            }

            _tray.Text = installed != null
                ? $"Trinketed {installed} (latest: {release.Version})"
                : $"Trinketed — not installed (latest: {release.Version})";
        }
        catch { }
    }

    async Task<ReleaseInfo?> GetLatestRelease()
    {
        var json = await _http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        var body = "";
        if (root.TryGetProperty("body", out var bodyEl))
            body = bodyEl.GetString() ?? "";

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip") && !name.Contains("nolib"))
                return new ReleaseInfo
                {
                    Version = tag ?? "unknown",
                    DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
                    FileName = name,
                    Body = body
                };
        }
        return null;
    }

    public string? GetInstalledVersion()
    {
        if (_addOnsPath == null) return null;
        var toc = Path.Combine(_addOnsPath, AddonName, TocFile);
        if (!File.Exists(toc)) return null;
        foreach (var line in File.ReadLines(toc))
            if (line.StartsWith("## Version:"))
                return line["## Version:".Length..].Trim();
        return null;
    }

    static string? FindAddOnsPath()
    {
        string[] bases = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "World of Warcraft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "World of Warcraft"),
            @"C:\World of Warcraft", @"D:\World of Warcraft", @"E:\World of Warcraft"
        };
        string[] clients = { "_anniversary_", "_retail_", "_classic_era_", "_classic_" };
        foreach (var b in bases)
            foreach (var c in clients)
            {
                var p = Path.Combine(b, c, "Interface", "AddOns");
                if (Directory.Exists(p)) return p;
            }
        return null;
    }

    public async Task CheckAppUpdate()
    {
        try
        {
            var json = await _http.GetStringAsync($"https://api.github.com/repos/{AppRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            if (tag == AppVersion) return;

            string? downloadUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (downloadUrl == null) return;

            var result = MessageBox.Show(
                $"Trinketed Desktop v{tag} is available (you have v{AppVersion}).\n\nUpdate now?",
                "Trinketed Desktop Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result != DialogResult.Yes) return;

            var tempExe = Path.Combine(Path.GetTempPath(), "TrinketedDesktop_update.exe");
            var bytes = await _http.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(tempExe, bytes);

            var currentExe = Application.ExecutablePath;
            var batchPath = Path.Combine(Path.GetTempPath(), "TrinketedDesktop_updater.cmd");
            var batchScript = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                copy /y "{tempExe}" "{currentExe}"
                del "{tempExe}"
                start "" "{currentExe}"
                del "%~f0"
                """;
            await File.WriteAllTextAsync(batchPath, batchScript);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            _tray.Visible = false;
            Application.Exit();
        }
        catch { }
    }

    void ShowInstallWindow()
    {
        if (_form != null && !_form.IsDisposed) { _form.BringToFront(); _form.Activate(); return; }
        _form = new InstallForm(this);
        _form.Show();
    }

    public async Task<string> InstallAddon(HashSet<string> selectedAddons, Action<string> log, Action<int> progress)
    {
        if (_addOnsPath == null) return "AddOns folder not found.";
        if (_latestRelease == null) return "No release info available.";
        if (selectedAddons.Count == 0) return "No addons selected.";

        var tempZip = Path.Combine(Path.GetTempPath(), _latestRelease.FileName);
        var tempDir = Path.Combine(Path.GetTempPath(), "TrinketedExtract");

        try
        {
            log("Downloading...");
            progress(10);
            var bytes = await _http.GetByteArrayAsync(_latestRelease.DownloadUrl);
            await File.WriteAllBytesAsync(tempZip, bytes);
            log($"Downloaded {bytes.Length / 1024} KB");
            progress(50);

            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            log("Extracting...");
            ZipFile.ExtractToDirectory(tempZip, tempDir);
            progress(70);

            int count = 0;
            foreach (var dir in Directory.GetDirectories(tempDir))
            {
                var dirName = Path.GetFileName(dir);
                if (Directory.GetFiles(dir, "*.toc").Length == 0) continue;
                if (!selectedAddons.Contains(dirName)) { log($"Skipped {dirName}"); continue; }
                var dest = Path.Combine(_addOnsPath, dirName);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                CopyDirectory(dir, dest);
                log($"Installed {dirName}");
                count++;
            }
            progress(100);
            log($"Done — {count} addon(s) installed. /reload in WoW.");
            await CheckForUpdate(false);
            return "success";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
        finally
        {
            try { File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public string? AddOnsPath { get => _addOnsPath; set => _addOnsPath = value; }
    public ReleaseInfo? LatestRelease => _latestRelease;
    public string? InstalledVersion => GetInstalledVersion();
    public async Task RefreshRelease() => await CheckForUpdate(false);

    public async Task<List<AddonInfo>> FetchAddonManifest()
    {
        try
        {
            var url = $"https://raw.githubusercontent.com/{Repo}/main/addons.json";
            var json = await _http.GetStringAsync(url);
            var addons = JsonSerializer.Deserialize<List<AddonInfo>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (addons != null && addons.Count > 0) return addons;
        }
        catch { }

        // Fallback — return defaults so the app still works offline
        return new List<AddonInfo>
        {
            new() { Name = "Trinketed", Description = "Core framework & shared library", Color = "#D4AA44" },
            new() { Name = "TrinketedCD", Description = "Arena cooldown tracker", Color = "#3B82F6" },
            new() { Name = "TrinketedHistory", Description = "Match history & VOD timestamps", Color = "#22C55E" },
        };
    }

    static bool IsInStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue(AppName) != null;
    }
    static void AddToStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        key?.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
    }
    static void RemoveFromStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        key?.DeleteValue(AppName, false);
    }

    internal static PointF[] SvgHexPoints(float[][] coords, float scale, float ox = 0, float oy = 0)
    {
        var pts = new PointF[coords.Length];
        for (int i = 0; i < coords.Length; i++)
            pts[i] = new PointF(coords[i][0] * scale + ox, coords[i][1] * scale + oy);
        return pts;
    }

    // SVG paths from webapp nav-logo (viewBox 0 0 24 24)
    internal static readonly float[][] OuterHex = { new[]{12f,2f}, new[]{4f,7f}, new[]{4f,17f}, new[]{12f,22f}, new[]{20f,17f}, new[]{20f,7f} };
    internal static readonly float[][] InnerHex = { new[]{9f,10f}, new[]{12f,8f}, new[]{15f,10f}, new[]{15f,14f}, new[]{12f,16f}, new[]{9f,14f} };

    internal static Icon CreateTrayIcon()
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var s = 32f / 24f;
        var outer = SvgHexPoints(OuterHex, s);
        var inner = SvgHexPoints(InnerHex, s);

        // Outer hexagon — stroke only
        using var outerPen = new Pen(Theme.Gold, 1.5f * s);
        g.DrawPolygon(outerPen, outer);

        // Inner hexagon — filled at 30% opacity + stroke
        using var innerFill = new SolidBrush(Color.FromArgb(77, Theme.Gold));
        g.FillPolygon(innerFill, inner);
        using var innerPen = new Pen(Theme.Gold, 1f * s);
        g.DrawPolygon(innerPen, inner);

        return Icon.FromHandle(bmp.GetHicon());
    }
}

// ── Custom Controls ────────────────────────────────────────────────

// Panel with rounded corners and border
class SurfacePanel : Panel
{
    public Color BorderColor { get; set; } = Theme.BorderSubtle;
    public int Radius { get; set; } = 6;

    public SurfacePanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        using var path = RoundedRect(rect, Radius);
        using var fill = new SolidBrush(BackColor);
        g.FillPath(fill, path);
        using var pen = new Pen(BorderColor);
        g.DrawPath(pen, path);
    }

    static GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        var d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// Gold-accented progress bar
class GoldProgressBar : Control
{
    int _value;
    public int Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    public GoldProgressBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Height = 4;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Track
        using var trackBrush = new SolidBrush(Theme.BgElevated);
        g.FillRectangle(trackBrush, 0, 0, Width, Height);

        // Fill
        if (_value > 0)
        {
            var fillW = (int)(Width * _value / 100.0);
            using var fillBrush = new LinearGradientBrush(
                new Point(0, 0), new Point(fillW, 0),
                Theme.GoldDim, Theme.Gold);
            g.FillRectangle(fillBrush, 0, 0, fillW, Height);
        }
    }
}

// Gold gradient button with hover states
class GoldButton : Control
{
    bool _hovering;
    bool _pressing;
    public bool IsUpToDate { get; set; }

    public GoldButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 10, FontStyle.Bold);
        Height = 38;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, 4);

        if (IsUpToDate)
        {
            // Gold-tinted "up to date" state using brand colors
            using var fill = new SolidBrush(Color.FromArgb(12, Theme.Gold.R, Theme.Gold.G, Theme.Gold.B));
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb(50, Theme.Gold.R, Theme.Gold.G, Theme.Gold.B));
            g.DrawPath(border, path);
            DrawText(g, Theme.Gold);
            Cursor = Cursors.Default;
            return;
        }

        if (!Enabled)
        {
            using var disabledBrush = new SolidBrush(Theme.BgElevated);
            g.FillPath(disabledBrush, path);
            using var disabledPen = new Pen(Theme.BorderSubtle);
            g.DrawPath(disabledPen, path);
            DrawText(g, Theme.TextMuted);
            Cursor = Cursors.Default;
            return;
        }

        Cursor = Cursors.Hand;

        // Gold gradient fill
        var c1 = _pressing ? Theme.GoldDim : (_hovering ? Theme.GoldGlow : Theme.Gold);
        var c2 = _pressing ? Color.FromArgb(150, 120, 20) : (_hovering ? Theme.Gold : Theme.GoldDim);
        using var brush = new LinearGradientBrush(new Point(0, 0), new Point(0, Height), c1, c2);
        g.FillPath(brush, path);

        // Subtle glow shadow
        if (_hovering && !_pressing)
        {
            using var glowPen = new Pen(Color.FromArgb(60, Theme.Gold), 2);
            g.DrawPath(glowPen, path);
        }

        DrawText(g, Theme.BgDeep);
    }

    void DrawText(Graphics g, Color color)
    {
        using var brush = new SolidBrush(color);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, brush, new RectangleF(0, 0, Width, Height), sf);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; _pressing = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressing = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressing = false; Invalidate(); base.OnMouseUp(e); }

    internal static new GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        var d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// Ghost-style button (outlined)
class GhostButton : Control
{
    bool _hovering;

    public GhostButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 8);
        ForeColor = Theme.TextSecondary;
        Height = 24;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = GoldButton.RoundedRect(rect, 3);

        var bg = _hovering ? Theme.BgElevated : Color.Transparent;
        var border = _hovering ? Theme.BorderDefault : Theme.BorderSubtle;
        var fg = _hovering ? Theme.TextPrimary : Theme.TextSecondary;

        using var fill = new SolidBrush(bg);
        g.FillPath(fill, path);
        using var pen = new Pen(border);
        g.DrawPath(pen, path);

        using var brush = new SolidBrush(fg);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, brush, new RectangleF(0, 0, Width, Height), sf);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Invalidate(); base.OnMouseUp(e); }

    // Expose RoundedRect for reuse
    internal static GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        var d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// Sub-addon card with left edge color bar and toggle selection
class AddonCard : Control
{
    public string AddonTitle { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string Description { get; set; } = "";
    public Color EdgeColor { get; set; } = Theme.Gold;
    public bool Selected { get; set; } = true;
    bool _hovering;

    public AddonCard()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Height = 48;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Background — dimmed when deselected
        var bg = !Selected ? Theme.BgBase
               : _hovering ? Theme.BgHover : Theme.BgElevated;
        using var fill = new SolidBrush(bg);
        g.FillRectangle(fill, 0, 0, Width, Height);

        // Left edge bar — dimmed when deselected
        var edgeColor = Selected ? EdgeColor : Color.FromArgb(60, EdgeColor);
        using var edgeBrush = new SolidBrush(edgeColor);
        g.FillRectangle(edgeBrush, 0, 0, 3, Height);

        // Border
        using var borderPen = new Pen(Selected && _hovering ? Theme.BorderDefault : Theme.BorderSubtle);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        // Title — dimmed when deselected
        var titleColor = Selected ? Theme.TextPrimary : Theme.TextMuted;
        using var titleFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var titleBrush = new SolidBrush(titleColor);
        g.DrawString(AddonTitle, titleFont, titleBrush, 12, 8);

        // Description
        var descColor = Selected ? Theme.TextMuted : Theme.TextDim;
        using var descFont = new Font("Segoe UI", 7.5f);
        using var descBrush = new SolidBrush(descColor);
        g.DrawString(Description, descFont, descBrush, 12, 26);

        // Toggle indicator on the right
        var indicatorX = Width - 28;
        var indicatorY = Height / 2 - 7;
        if (Selected)
        {
            using var checkBrush = new SolidBrush(EdgeColor);
            g.FillRectangle(checkBrush, indicatorX, indicatorY, 14, 14);
            // Draw checkmark
            using var checkPen = new Pen(Theme.BgDeep, 2f);
            g.DrawLine(checkPen, indicatorX + 3, indicatorY + 7, indicatorX + 6, indicatorY + 10);
            g.DrawLine(checkPen, indicatorX + 6, indicatorY + 10, indicatorX + 11, indicatorY + 4);
        }
        else
        {
            using var boxPen = new Pen(Theme.BorderDefault);
            g.DrawRectangle(boxPen, indicatorX, indicatorY, 14, 14);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        Selected = !Selected;
        Invalidate();
        base.OnMouseUp(e);
    }
}

// ── Install Form ───────────────────────────────────────────────────
class InstallForm : Form
{
    readonly TrayContext _ctx;
    readonly Label _installedValue;
    readonly Label _latestValue;
    readonly Label _arrow;
    readonly GoldButton _installBtn;
    readonly GoldProgressBar _progressBar;
    readonly Label _statusLine;
    readonly Label _pathValue;
    readonly List<AddonCard> _addonCards = new();
    readonly Panel _cardsPanel;
    readonly Panel _bottomPanel; // holds install btn, progress, status, check-updates
    readonly Panel _pathRow;

    public InstallForm(TrayContext ctx)
    {
        _ctx = ctx;
        DoubleBuffered = true;

        Text = "Trinketed Desktop";
        Icon = TrayContext.CreateTrayIcon();
        ClientSize = new Size(420, 460);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Theme.BgDeep;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 9);

        var y = 0;

        // ── Header panel ──
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Theme.BgBase
        };
        header.Paint += (_, e) =>
        {
            // Gold gradient accent line at the bottom (matching webapp AppHeader)
            using var pen = new Pen(Color.FromArgb(50, Theme.Gold));
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);

            // Brighter center
            var centerX = header.Width / 2;
            using var goldPen = new Pen(Color.FromArgb(30, Theme.Gold), 1);
            e.Graphics.DrawLine(goldPen,
                centerX - 120, header.Height - 1,
                centerX + 120, header.Height - 1);
        };
        Controls.Add(header);

        // Logo — matching webapp nav-logo SVG
        var logo = new Panel { Size = new Size(36, 36), Location = new Point(20, 18), BackColor = Color.Transparent };
        logo.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var s = 36f / 24f;
            var outer = TrayContext.SvgHexPoints(TrayContext.OuterHex, s);
            var inner = TrayContext.SvgHexPoints(TrayContext.InnerHex, s);

            using var outerPen = new Pen(Theme.Gold, 1.5f * s);
            g.DrawPolygon(outerPen, outer);
            using var innerFill = new SolidBrush(Color.FromArgb(77, Theme.Gold));
            g.FillPolygon(innerFill, inner);
            using var innerPen = new Pen(Theme.Gold, 1f * s);
            g.DrawPolygon(innerPen, inner);
        };
        header.Controls.Add(logo);

        // Brand text — owner-drawn so "T" is gold, rest is text-primary
        var brandPanel = new Panel
        {
            Size = new Size(180, 28),
            Location = new Point(62, 14),
            BackColor = Color.Transparent
        };
        brandPanel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Segoe UI", 14, FontStyle.Bold);
            using var goldBrush = new SolidBrush(Theme.Gold);
            using var textBrush = new SolidBrush(Theme.TextPrimary);
            var tWidth = TextRenderer.MeasureText(g, "T", font, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, "T", font, new Point(0, 0), Theme.Gold, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "RINKETED", font, new Point(tWidth, 0), Theme.TextPrimary, TextFormatFlags.NoPadding);
        };
        header.Controls.Add(brandPanel);

        var subtitle = new Label
        {
            Text = "Arena Suite",
            Font = new Font("Segoe UI", 8),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(62, 42),
            BackColor = Color.Transparent
        };
        header.Controls.Add(subtitle);

        y = 72;

        // ── Version hero ──
        var versionPanel = new SurfacePanel
        {
            Location = new Point(16, y + 16),
            Size = new Size(388, 56),
            BackColor = Theme.BgRaised,
            BorderColor = Theme.BorderSubtle
        };
        Controls.Add(versionPanel);

        _installedValue = new Label
        {
            Text = "...",
            Font = new Font("Consolas", 13, FontStyle.Bold),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(16, 16),
            BackColor = Color.Transparent
        };
        versionPanel.Controls.Add(_installedValue);

        _arrow = new Label
        {
            Text = "→",
            Font = new Font("Segoe UI", 12),
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Location = new Point(120, 16),
            BackColor = Color.Transparent,
            Visible = false
        };
        versionPanel.Controls.Add(_arrow);

        _latestValue = new Label
        {
            Text = "checking...",
            Font = new Font("Consolas", 13, FontStyle.Bold),
            ForeColor = Theme.Gold,
            AutoSize = true,
            Location = new Point(148, 16),
            BackColor = Color.Transparent
        };
        versionPanel.Controls.Add(_latestValue);

        y += 16 + 56;

        // ── Sub-addon cards (loaded dynamically) ──
        var cardLabel = new Label
        {
            Text = "LOADING ADDONS...",
            Font = new Font("Segoe UI", 7, FontStyle.Bold),
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Location = new Point(18, y + 14)
        };
        Controls.Add(cardLabel);
        y += 32;

        _cardsPanel = new Panel
        {
            Location = new Point(0, y),
            Size = new Size(420, 0), // will grow dynamically
            BackColor = Color.Transparent
        };
        Controls.Add(_cardsPanel);

        // ── Bottom section (install btn, progress, status, check-updates) ──
        _bottomPanel = new Panel
        {
            Location = new Point(0, y),
            Size = new Size(420, 100),
            BackColor = Color.Transparent
        };
        Controls.Add(_bottomPanel);

        int by = 4;
        _installBtn = new GoldButton
        {
            Text = "INSTALL",
            Location = new Point(16, by),
            Size = new Size(388, 38),
            Enabled = false
        };
        _installBtn.Click += InstallClick;
        _bottomPanel.Controls.Add(_installBtn);

        by += 42;
        _progressBar = new GoldProgressBar
        {
            Location = new Point(16, by),
            Size = new Size(388, 4),
            Visible = false
        };
        _bottomPanel.Controls.Add(_progressBar);

        by += 10;
        _statusLine = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Theme.TextMuted,
            AutoSize = false,
            Size = new Size(260, 16),
            Location = new Point(18, by),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _bottomPanel.Controls.Add(_statusLine);

        var checkUpdatesBtn = new GhostButton
        {
            Text = "Check for Updates",
            Size = new Size(120, 20),
            Location = new Point(284, by - 2)
        };
        checkUpdatesBtn.Click += async (_, _) =>
        {
            checkUpdatesBtn.Enabled = false;
            checkUpdatesBtn.Text = "Checking...";
            SetStatus("Checking for updates...");
            await _ctx.RefreshRelease();
            await _ctx.CheckAppUpdate();
            await RefreshUI();
            checkUpdatesBtn.Text = "Check for Updates";
            checkUpdatesBtn.Enabled = true;
        };
        _bottomPanel.Controls.Add(checkUpdatesBtn);

        // ── Footer: path ──
        _pathRow = new Panel
        {
            Location = new Point(0, 0), // positioned in LayoutFromCards
            Size = new Size(420, 30),
            BackColor = Theme.BgBase
        };
        _pathRow.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.BorderSubtle);
            e.Graphics.DrawLine(pen, 0, 0, _pathRow.Width, 0);
        };
        Controls.Add(_pathRow);

        var pathIcon = new Label
        {
            Text = "\U0001F4C2",
            Font = new Font("Segoe UI", 8),
            AutoSize = true,
            Location = new Point(16, 6),
            BackColor = Color.Transparent
        };
        _pathRow.Controls.Add(pathIcon);

        _pathValue = new Label
        {
            Text = "Detecting...",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(36, 8),
            BackColor = Color.Transparent
        };
        _pathRow.Controls.Add(_pathValue);

        var browseBtn = new GhostButton
        {
            Text = "Browse",
            Size = new Size(56, 22),
            Location = new Point(350, 4)
        };
        browseBtn.Click += BrowseClick;
        _pathRow.Controls.Add(browseBtn);

        Shown += async (_, _) =>
        {
            await LoadAddonCards(cardLabel);
            await RefreshUI();
        };
    }

    async Task LoadAddonCards(Label cardLabel)
    {
        var addons = await _ctx.FetchAddonManifest();
        cardLabel.Text = "INCLUDED";

        _cardsPanel.SuspendLayout();
        _addonCards.Clear();
        _cardsPanel.Controls.Clear();

        int cy = 0;
        foreach (var addon in addons)
        {
            var card = new AddonCard
            {
                AddonTitle = addon.Name,
                FolderName = addon.Name,
                Description = addon.Description,
                EdgeColor = addon.ParsedColor,
                Location = new Point(16, cy),
                Size = new Size(388, 48)
            };
            _addonCards.Add(card);
            _cardsPanel.Controls.Add(card);
            cy += 52;
        }

        _cardsPanel.Size = new Size(420, cy);
        _cardsPanel.ResumeLayout();
        LayoutFromCards();
    }

    void LayoutFromCards()
    {
        // Position bottom panel right after the cards panel
        int y = _cardsPanel.Bottom;
        _bottomPanel.Location = new Point(0, y);

        // Position path row after bottom panel
        _pathRow.Location = new Point(0, _bottomPanel.Bottom);

        // Resize the form
        ClientSize = new Size(420, _pathRow.Bottom);
    }

    void SetStatus(string msg) { if (InvokeRequired) Invoke(() => SetStatus(msg)); else _statusLine.Text = msg; }
    void SetProgress(int val) { if (InvokeRequired) Invoke(() => SetProgress(val)); else _progressBar.Value = val; }

    async Task RefreshUI()
    {
        // Path
        if (_ctx.AddOnsPath != null)
        {
            var client = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(_ctx.AddOnsPath)) ?? "");
            _pathValue.Text = $@"...\{client}\...\AddOns";
            _pathValue.ForeColor = Theme.TextSecondary;
        }
        else
        {
            _pathValue.Text = "Not found — click Browse";
            _pathValue.ForeColor = Theme.Negative;
        }

        SetStatus("Checking for updates...");
        await _ctx.RefreshRelease();

        // Installed version
        var ver = _ctx.InstalledVersion;
        if (ver != null)
        {
            _installedValue.Text = ver;
            _installedValue.ForeColor = Theme.TextSecondary;
        }
        else
        {
            _installedValue.Text = "not installed";
            _installedValue.ForeColor = Theme.TextMuted;
        }

        // Latest
        var rel = _ctx.LatestRelease;
        if (rel != null)
        {
            _latestValue.Text = rel.Version;
            _latestValue.ForeColor = Theme.Gold;

            if (ver != null && ver == rel.Version)
            {
                _arrow.Visible = false;
                _latestValue.Visible = false;
                _installedValue.ForeColor = Theme.Gold;
                _installBtn.Text = "UP TO DATE";
                _installBtn.Enabled = false;
                _installBtn.IsUpToDate = true;
                _installBtn.Invalidate();
                SetStatus("You're running the latest version.");
            }
            else
            {
                _arrow.Visible = true;
                _latestValue.Visible = true;
                _installBtn.IsUpToDate = false;
                // Position arrow and latest value after installed text
                var installedWidth = TextRenderer.MeasureText(_installedValue.Text, _installedValue.Font).Width;
                _arrow.Location = new Point(16 + installedWidth + 8, 16);
                var arrowWidth = TextRenderer.MeasureText("→", _arrow.Font).Width;
                _latestValue.Location = new Point(16 + installedWidth + 8 + arrowWidth + 4, 16);

                _installBtn.Text = ver == null ? "INSTALL" : "UPDATE";
                _installBtn.Enabled = _ctx.AddOnsPath != null;
                SetStatus(ver == null ? "Ready to install." : $"Update available: {ver} → {rel.Version}");
            }
        }
        else
        {
            _latestValue.Text = "offline";
            _latestValue.ForeColor = Theme.Negative;
            SetStatus("Could not reach GitHub.");
        }
    }

    void BrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Select your WoW AddOns folder" };
        if (_ctx.AddOnsPath != null) dialog.SelectedPath = _ctx.AddOnsPath;
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _ctx.AddOnsPath = dialog.SelectedPath;
            _ = RefreshUI();
        }
    }

    async void InstallClick(object? sender, EventArgs e)
    {
        var selected = new HashSet<string>(
            _addonCards.Where(c => c.Selected).Select(c => c.FolderName));
        if (selected.Count == 0) { SetStatus("No addons selected."); return; }

        _installBtn.Enabled = false;
        _installBtn.Text = "INSTALLING...";
        _progressBar.Visible = true;
        _progressBar.Value = 0;

        var result = await _ctx.InstallAddon(selected, SetStatus, SetProgress);
        if (result != "success") SetStatus(result);

        _progressBar.Visible = false;
        await RefreshUI();
    }
}

// ── Dark Context Menu Renderer ─────────────────────────────────────
class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        var color = e.Item.Selected ? Theme.BgHover : Theme.BgRaised;
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, rect);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Fill the image margin column with the same background
        using var brush = new SolidBrush(Theme.BgRaised);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.BorderSubtle);
        e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Theme.BgRaised);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.BorderSubtle);
        e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Dark background behind checkmark
        using var bgBrush = new SolidBrush(Theme.BgRaised);
        e.Graphics.FillRectangle(bgBrush, e.ImageRectangle);
        using var font = new Font("Segoe UI", 8, FontStyle.Bold);
        using var brush = new SolidBrush(Theme.Gold);
        e.Graphics.DrawString("✓", font, brush, e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2);
    }
}

class DarkMenuColors : ProfessionalColorTable
{
    public override Color MenuBorder => Theme.BorderSubtle;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Theme.BgHover;
    public override Color MenuStripGradientBegin => Theme.BgRaised;
    public override Color MenuStripGradientEnd => Theme.BgRaised;
    public override Color MenuItemSelectedGradientBegin => Theme.BgHover;
    public override Color MenuItemSelectedGradientEnd => Theme.BgHover;
    public override Color MenuItemPressedGradientBegin => Theme.BgElevated;
    public override Color MenuItemPressedGradientEnd => Theme.BgElevated;
    public override Color ImageMarginGradientBegin => Theme.BgRaised;
    public override Color ImageMarginGradientMiddle => Theme.BgRaised;
    public override Color ImageMarginGradientEnd => Theme.BgRaised;
    public override Color SeparatorDark => Theme.BorderSubtle;
    public override Color SeparatorLight => Theme.BorderSubtle;
    public override Color CheckBackground => Theme.BgRaised;
    public override Color CheckPressedBackground => Theme.BgRaised;
    public override Color CheckSelectedBackground => Theme.BgRaised;
    public override Color ToolStripDropDownBackground => Theme.BgRaised;
}
