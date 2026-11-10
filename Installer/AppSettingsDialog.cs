namespace MonitorAndControl.Installer;

/// <summary>Asks what to do with appsettings.json when the new version adds options it lacks.</summary>
internal sealed class AppSettingsDialog : Form
{
    private readonly RadioButton _keep;
    private readonly RadioButton _replace;

    public AppSettingsDialog(IReadOnlyList<(string Name, string Value)> newOptions)
    {
        Text = "Monitor & Control Setup - new settings";
        ClientSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;

        var intro = new Label
        {
            Text = "This version adds settings that are missing from your appsettings.json:",
            UseMnemonic = false,
            Location = new Point(16, 14),
            Size = new Size(528, 22),
        };
        var list = new TextBox
        {
            Location = new Point(16, 40),
            Size = new Size(528, 130),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f),
            BackColor = Color.White,
            Text = string.Join(Environment.NewLine, newOptions.Select(o => $"{o.Name} = {Shorten(o.Value)}")),
        };
        var ask = new Label { Text = "What should Setup do?", UseMnemonic = false, Location = new Point(16, 184), Size = new Size(528, 22) };
        var merge = new RadioButton
        {
            Text = "Add the new settings with their default values and keep all my existing values (recommended)",
            Location = new Point(24, 210),
            Size = new Size(520, 36),
            Checked = true,
        };
        _keep = new RadioButton
        {
            Text = "Keep my file exactly as it is (the new settings use the program's built-in defaults)",
            Location = new Point(24, 250),
            Size = new Size(520, 36),
        };
        _replace = new RadioButton
        {
            Text = "Replace it with the new default file (my file is saved as a backup first)",
            Location = new Point(24, 290),
            Size = new Size(520, 36),
        };
        var ok = new Button { Text = "Continue", Location = new Point(464, 340), Size = new Size(80, 28), DialogResult = DialogResult.OK };
        AcceptButton = ok;
        Controls.AddRange(new Control[] { intro, list, ask, merge, _keep, _replace, ok });
    }

    public AppSettingsAction Choice =>
        _replace.Checked ? AppSettingsAction.Replace : _keep.Checked ? AppSettingsAction.Keep : AppSettingsAction.Merge;

    private static string Shorten(string value) => value.Length <= 70 ? value : value[..67] + "...";
}
