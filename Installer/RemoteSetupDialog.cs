namespace MonitorAndControl.Installer;

/// <summary>
/// Setup dialog to configure remote dashboard access, port selection, and remote change PIN.
/// </summary>
internal sealed class RemoteSetupDialog : Form
{
    private readonly RadioButton _enableRemote;
    private readonly RadioButton _disableRemote;
    private readonly Panel _remotePanel;
    private readonly TextBox _portBox;
    private readonly Label _portStatus;
    private readonly RadioButton? _keepPin;
    private readonly RadioButton? _changePin;
    private readonly Label _pinLabel;
    private readonly TextBox _pinBox;
    private readonly Label _confirmLabel;
    private readonly TextBox _confirmBox;
    private readonly Label _pinStatus;
    private readonly Button _ok;
    private readonly bool _hasExistingPin;
    private readonly int _initialPort;

    public bool EnableRemote => _enableRemote.Checked;
    public int Port { get; private set; } = 5000;
    public string? NewPin { get; private set; }

    public RemoteSetupDialog(bool initialRemote, int initialPort, bool hasExistingPin)
    {
        _initialPort = initialPort > 0 ? initialPort : 5000;
        _hasExistingPin = hasExistingPin;

        Text = InstallerLocalization.RemoteDialogTitle;
        ClientSize = new Size(580, hasExistingPin ? 480 : 440);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;

        var intro = new Label
        {
            Text = InstallerLocalization.RemoteDialogIntro,
            UseMnemonic = false,
            Location = new Point(16, 12),
            Size = new Size(548, 32),
        };

        _enableRemote = new RadioButton
        {
            Text = InstallerLocalization.RemoteEnableOption,
            Location = new Point(16, 48),
            Size = new Size(548, 28),
            Checked = initialRemote,
            Font = new Font(Font, FontStyle.Bold),
        };

        _disableRemote = new RadioButton
        {
            Text = InstallerLocalization.RemoteDisableOption,
            Location = new Point(16, 76),
            Size = new Size(548, 28),
            Checked = !initialRemote,
        };

        _remotePanel = new Panel
        {
            Location = new Point(16, 108),
            Size = new Size(548, hasExistingPin ? 320 : 280),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(12),
        };

        var portTitle = new Label
        {
            Text = InstallerLocalization.RemotePortLabel,
            Location = new Point(12, 10),
            Size = new Size(320, 20),
            Font = new Font(Font, FontStyle.Bold),
        };

        _portBox = new TextBox
        {
            Text = _initialPort.ToString(),
            Location = new Point(12, 32),
            Size = new Size(120, 24),
            MaxLength = 5,
        };

        _portStatus = new Label
        {
            Location = new Point(140, 34),
            Size = new Size(390, 22),
            ForeColor = Color.Red,
        };

        int curY = 64;
        var pinTitle = new Label
        {
            Text = InstallerLocalization.RemotePinSectionHeader,
            Location = new Point(12, curY),
            Size = new Size(520, 20),
            Font = new Font(Font, FontStyle.Bold),
        };
        curY += 24;

        _remotePanel.Controls.AddRange(new Control[] { portTitle, _portBox, _portStatus, pinTitle });

        if (_hasExistingPin)
        {
            _keepPin = new RadioButton
            {
                Text = InstallerLocalization.RemotePinExistingKeep,
                Location = new Point(12, curY),
                Size = new Size(520, 24),
                Checked = true,
            };
            curY += 24;

            _changePin = new RadioButton
            {
                Text = InstallerLocalization.RemotePinExistingChange,
                Location = new Point(12, curY),
                Size = new Size(520, 24),
            };
            curY += 28;

            _remotePanel.Controls.Add(_keepPin);
            _remotePanel.Controls.Add(_changePin);
        }

        _pinLabel = new Label
        {
            Text = InstallerLocalization.RemotePinLabel,
            Location = new Point(12, curY),
            Size = new Size(520, 20),
        };
        curY += 22;

        _pinBox = new TextBox
        {
            Location = new Point(12, curY),
            Size = new Size(240, 24),
            UseSystemPasswordChar = true,
            MaxLength = RemotePinSetup.MaxLength,
        };
        curY += 28;

        _confirmLabel = new Label
        {
            Text = InstallerLocalization.RemotePinConfirmLabel,
            Location = new Point(12, curY),
            Size = new Size(520, 20),
        };
        curY += 22;

        _confirmBox = new TextBox
        {
            Location = new Point(12, curY),
            Size = new Size(240, 24),
            UseSystemPasswordChar = true,
            MaxLength = RemotePinSetup.MaxLength,
        };
        curY += 28;

        _pinStatus = new Label
        {
            Location = new Point(12, curY),
            Size = new Size(520, 22),
            ForeColor = Color.Red,
        };

        _remotePanel.Controls.AddRange(new Control[] { _pinLabel, _pinBox, _confirmLabel, _confirmBox, _pinStatus });

        _ok = new Button
        {
            Text = InstallerLocalization.ButtonContinue,
            Location = new Point(484, ClientSize.Height - 40),
            Size = new Size(80, 28),
        };
        _ok.Click += OnOkClicked;
        AcceptButton = _ok;

        Controls.AddRange(new Control[] { intro, _enableRemote, _disableRemote, _remotePanel, _ok });

        _enableRemote.CheckedChanged += (_, _) => UpdateUiState();
        _disableRemote.CheckedChanged += (_, _) => UpdateUiState();
        if (_keepPin != null) _keepPin.CheckedChanged += (_, _) => UpdateUiState();
        if (_changePin != null) _changePin.CheckedChanged += (_, _) => UpdateUiState();

        _portBox.TextChanged += (_, _) => ValidatePort(showError: true);
        _pinBox.TextChanged += (_, _) => ValidatePin(showError: false);
        _confirmBox.TextChanged += (_, _) => ValidatePin(showError: false);

        UpdateUiState();
        ValidatePort(showError: false);
    }

    private void UpdateUiState()
    {
        _remotePanel.Enabled = _enableRemote.Checked;
        if (!_enableRemote.Checked)
        {
            _portStatus.Text = "";
            _pinStatus.Text = "";
            return;
        }

        bool pinFieldsEnabled = !_hasExistingPin || (_changePin != null && _changePin.Checked);
        _pinLabel.Enabled = pinFieldsEnabled;
        _pinBox.Enabled = pinFieldsEnabled;
        _confirmLabel.Enabled = pinFieldsEnabled;
        _confirmBox.Enabled = pinFieldsEnabled;
    }

    private bool ValidatePort(bool showError)
    {
        if (!_enableRemote.Checked) return true;

        if (!int.TryParse(_portBox.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            if (showError) _portStatus.Text = InstallerLocalization.RemotePortInvalid;
            return false;
        }

        // Check if port is in use
        if (RemotePinSetup.IsPortInUse(port))
        {
            if (showError) _portStatus.Text = string.Format(InstallerLocalization.RemotePortInUse, port);
            return false;
        }

        _portStatus.Text = "";
        Port = port;
        return true;
    }

    private bool ValidatePin(bool showError)
    {
        if (!_enableRemote.Checked) return true;
        if (_hasExistingPin && _keepPin != null && _keepPin.Checked)
        {
            _pinStatus.Text = "";
            return true;
        }

        string pin = _pinBox.Text;
        string confirm = _confirmBox.Text;

        if (string.IsNullOrEmpty(pin) && string.IsNullOrEmpty(confirm))
        {
            // If the user chooses not to set a PIN, that's allowed (remote changes remain disabled)
            _pinStatus.Text = "";
            return true;
        }

        if (!RemotePinSetup.IsAcceptable(pin, out var error))
        {
            if (showError) _pinStatus.Text = error;
            return false;
        }

        if (!string.Equals(pin, confirm, StringComparison.Ordinal))
        {
            if (showError) _pinStatus.Text = InstallerLocalization.RemotePinMismatch;
            return false;
        }

        _pinStatus.Text = "";
        return true;
    }

    private void OnOkClicked(object? sender, EventArgs e)
    {
        if (_enableRemote.Checked)
        {
            if (!ValidatePort(showError: true))
            {
                _portBox.Focus();
                return;
            }

            if (!ValidatePin(showError: true))
            {
                _pinBox.Focus();
                return;
            }

            if (!_hasExistingPin || (_changePin != null && _changePin.Checked))
            {
                NewPin = string.IsNullOrEmpty(_pinBox.Text) ? null : _pinBox.Text;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
