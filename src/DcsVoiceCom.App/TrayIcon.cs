using DcsVoiceCom.ViewModels;
using Forms = System.Windows.Forms;

namespace DcsVoiceCom.App;

/// <summary>The notification-area icon: show the window, pin a profile (or Auto), quit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _profiles;

    public TrayIcon(MainViewModel vm, Action show, Action quit)
    {
        _vm = vm;
        _profiles = new Forms.ToolStripMenuItem("Profile");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show DCS VoiceCom", null, (_, _) => show());
        menu.Items.Add(_profiles);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());
        menu.Opening += (_, _) => BuildProfileMenu();

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.TrayIcon(),
            Text = "DCS VoiceCom",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) show();
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.ActiveProfileText) or nameof(MainViewModel.ListeningText)) UpdateText();
        };
    }

    private void BuildProfileMenu()
    {
        _profiles.DropDownItems.Clear();
        foreach (var option in _vm.OverrideOptions)
        {
            var item = new Forms.ToolStripMenuItem(option == MainViewModel.AutoProfile ? "Auto (follow the aircraft)" : option)
            {
                Checked = option == _vm.SelectedOverride,
            };
            var chosen = option;
            item.Click += (_, _) => _vm.SelectedOverride = chosen;
            _profiles.DropDownItems.Add(item);
        }
    }

    private void UpdateText()
    {
        var text = $"DCS VoiceCom · {_vm.ListeningText}" + (_vm.ActiveProfileText.Length > 0 ? $" · {_vm.ActiveProfileText}" : "");
        _icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
