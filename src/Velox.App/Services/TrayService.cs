using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace Velox.App.Services;

/// <summary>Ícone na bandeja do sistema com menu e notificações.</summary>
public sealed class TrayService : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _pauseItem;
    private readonly WF.ToolStripMenuItem _resumeItem;

    public event Action? OpenRequested;
    public event Action? AddRequested;
    public event Action? PauseAllRequested;
    public event Action? ResumeAllRequested;
    public event Action? ExitRequested;

    public bool NotificationsEnabled { get; set; } = true;

    public TrayService()
    {
        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            Font = new SD.Font("Segoe UI", 9.5f)
        };

        var open = new WF.ToolStripMenuItem("Abrir Velox");
        open.Font = new SD.Font(menu.Font, SD.FontStyle.Bold);
        open.Click += (_, _) => OpenRequested?.Invoke();

        var add = new WF.ToolStripMenuItem("Novo download…");
        add.Click += (_, _) => AddRequested?.Invoke();

        _pauseItem = new WF.ToolStripMenuItem("Pausar todos");
        _pauseItem.Click += (_, _) => PauseAllRequested?.Invoke();

        _resumeItem = new WF.ToolStripMenuItem("Retomar todos");
        _resumeItem.Click += (_, _) => ResumeAllRequested?.Invoke();

        var exit = new WF.ToolStripMenuItem("Sair");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.AddRange(new WF.ToolStripItem[]
        {
            open, add, new WF.ToolStripSeparator(), _pauseItem, _resumeItem, new WF.ToolStripSeparator(), exit
        });

        _icon = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Velox Download Manager",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    private static SD.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = SD.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { }
        return SD.SystemIcons.Application;
    }

    public void SetStatus(string text)
    {
        var t = "Velox — " + text;
        if (t.Length > 63) t = t[..60] + "…";
        try { _icon.Text = t; } catch { }
    }

    public void Notify(string title, string message, bool error = false)
    {
        if (!NotificationsEnabled) return;
        try
        {
            _icon.ShowBalloonTip(4000, title, message, error ? WF.ToolTipIcon.Error : WF.ToolTipIcon.Info);
        }
        catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private sealed class DarkRenderer : WF.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? SD.Color.White : SD.Color.FromArgb(0xED, 0xEF, 0xF7);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(0x32, 0x39, 0x52));
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class DarkColors : WF.ProfessionalColorTable
    {
        private static readonly SD.Color Bg = SD.Color.FromArgb(0x16, 0x1A, 0x26);
        private static readonly SD.Color Hover = SD.Color.FromArgb(0x27, 0x2D, 0x40);
        private static readonly SD.Color Border = SD.Color.FromArgb(0x32, 0x39, 0x52);

        public override SD.Color ToolStripDropDownBackground => Bg;
        public override SD.Color MenuItemSelected => Hover;
        public override SD.Color MenuItemSelectedGradientBegin => Hover;
        public override SD.Color MenuItemSelectedGradientEnd => Hover;
        public override SD.Color MenuItemBorder => Hover;
        public override SD.Color MenuBorder => Border;
        public override SD.Color ImageMarginGradientBegin => Bg;
        public override SD.Color ImageMarginGradientMiddle => Bg;
        public override SD.Color ImageMarginGradientEnd => Bg;
        public override SD.Color SeparatorDark => Border;
        public override SD.Color SeparatorLight => Border;
    }
}
