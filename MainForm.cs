using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TcpSerialComm.Common;
using TcpSerialComm.Communicators;

namespace TcpSerialComm
{
    /// <summary>
    /// Test main form: used solely to exercise the read/write and reconnect behavior of the two communicator classes.
    /// Implements the safety rules: single instance, no duplicate connect, no double-click on send, closing the
    /// device on exit, and confirmation prompts for risky actions such as clearing the log.
    /// Also hosts the "Connection parameters" panel so common settings are visible and editable, then locked while connected.
    /// </summary>
    public sealed class MainForm : Form
    {
        private TcpCommunicator _tcp;
        private SerialPortCommunicator _serial;
        private readonly StringBuilder _log = new StringBuilder();
        private const int MaxLogChars = 200000;

        private TextBox txtIp, txtPort, txtTcpSend, txtSerialSend, txtLog;
        private ComboBox cboPort, cboBaud, cboTcpType, cboSerialType;
        private Button btnTcpConnect, btnTcpDisconnect, btnTcpSend;
        private Button btnSerialConnect, btnSerialDisconnect, btnSerialSend, btnClearLog;
        private Label lblTcpStatus, lblSerialStatus;

        // Common parameter panel controls.
        private GroupBox gbParam;
        private ToolTip _toolTip;
        private CheckBox chkAutoReconnect, chkHeartbeat, chkTcpAbortiveClose;
        private TextBox txtMaxRetry, txtReconnectBase, txtReconnectMax;
        private TextBox txtWriteMin, txtWriteRetry, txtWriteRetryInt;
        private TextBox txtHeartbeatInt, txtSilence;
        private ComboBox cboDelimiter;

        public MainForm()
        {
            InitializeComponent();
            InitializeCommunicators();
            RefreshPortList();
        }

        private void InitializeComponent()
        {
            Text = "TCP / Serial Read-Write Test Tool";
            ClientSize = new System.Drawing.Size(760, 562);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // ---- TCP group ----
            var gbTcp = new GroupBox { Text = "TCP Client", Location = new System.Drawing.Point(12, 12), Size = new System.Drawing.Size(358, 180) };
            gbTcp.Controls.Add(new Label { Text = "IP:", Location = new System.Drawing.Point(12, 24), AutoSize = true });
            txtIp = new TextBox { Text = "127.0.0.1", Location = new System.Drawing.Point(45, 21), Size = new System.Drawing.Size(120, 23) };
            gbTcp.Controls.Add(txtIp);
            gbTcp.Controls.Add(new Label { Text = "Port:", Location = new System.Drawing.Point(172, 24), AutoSize = true });
            txtPort = new TextBox { Text = "502", Location = new System.Drawing.Point(210, 21), Size = new System.Drawing.Size(60, 23) };
            gbTcp.Controls.Add(txtPort);

            btnTcpConnect = new Button { Text = "Connect", Location = new System.Drawing.Point(12, 56), Size = new System.Drawing.Size(80, 28) };
            btnTcpDisconnect = new Button { Text = "Disconnect", Location = new System.Drawing.Point(100, 56), Size = new System.Drawing.Size(88, 28), Enabled = false };
            lblTcpStatus = new Label { Text = "State: Disconnected", Location = new System.Drawing.Point(12, 90), AutoSize = true };
            gbTcp.Controls.Add(btnTcpConnect);
            gbTcp.Controls.Add(btnTcpDisconnect);
            gbTcp.Controls.Add(lblTcpStatus);
            chkTcpAbortiveClose = new CheckBox { Text = "Abortive close (RST)", Location = new System.Drawing.Point(196, 88), AutoSize = true, Checked = true };
            gbTcp.Controls.Add(chkTcpAbortiveClose);

            gbTcp.Controls.Add(new Label { Text = "Send:", Location = new System.Drawing.Point(12, 124), AutoSize = true });
            txtTcpSend = new TextBox { Location = new System.Drawing.Point(55, 121), Size = new System.Drawing.Size(155, 23) };
            cboTcpType = new ComboBox { Location = new System.Drawing.Point(216, 121), Size = new System.Drawing.Size(70, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboTcpType.Items.AddRange(new object[] { "Text", "Hex" });
            cboTcpType.SelectedIndex = 0;
            gbTcp.Controls.Add(txtTcpSend);
            gbTcp.Controls.Add(cboTcpType);
            btnTcpSend = new Button { Text = "Send", Location = new System.Drawing.Point(12, 152), Size = new System.Drawing.Size(80, 28), Enabled = false };
            gbTcp.Controls.Add(btnTcpSend);

            // ---- Serial group ----
            var gbSer = new GroupBox { Text = "Serial Port", Location = new System.Drawing.Point(382, 12), Size = new System.Drawing.Size(358, 180) };
            gbSer.Controls.Add(new Label { Text = "Port:", Location = new System.Drawing.Point(12, 24), AutoSize = true });
            cboPort = new ComboBox { Location = new System.Drawing.Point(56, 21), Size = new System.Drawing.Size(95, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            gbSer.Controls.Add(cboPort);
            gbSer.Controls.Add(new Label { Text = "Baud:", Location = new System.Drawing.Point(175, 24), AutoSize = true });
            cboBaud = new ComboBox { Location = new System.Drawing.Point(222, 21), Size = new System.Drawing.Size(80, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboBaud.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200" });
            cboBaud.SelectedIndex = 0;
            gbSer.Controls.Add(cboBaud);

            btnSerialConnect = new Button { Text = "Connect", Location = new System.Drawing.Point(12, 56), Size = new System.Drawing.Size(80, 28) };
            btnSerialDisconnect = new Button { Text = "Disconnect", Location = new System.Drawing.Point(100, 56), Size = new System.Drawing.Size(88, 28), Enabled = false };
            lblSerialStatus = new Label { Text = "State: Disconnected", Location = new System.Drawing.Point(12, 90), AutoSize = true };
            gbSer.Controls.Add(btnSerialConnect);
            gbSer.Controls.Add(btnSerialDisconnect);
            gbSer.Controls.Add(lblSerialStatus);

            gbSer.Controls.Add(new Label { Text = "Send:", Location = new System.Drawing.Point(12, 124), AutoSize = true });
            txtSerialSend = new TextBox { Location = new System.Drawing.Point(55, 121), Size = new System.Drawing.Size(155, 23) };
            cboSerialType = new ComboBox { Location = new System.Drawing.Point(216, 121), Size = new System.Drawing.Size(70, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboSerialType.Items.AddRange(new object[] { "Text", "Hex" });
            cboSerialType.SelectedIndex = 0;
            gbSer.Controls.Add(txtSerialSend);
            gbSer.Controls.Add(cboSerialType);
            btnSerialSend = new Button { Text = "Send", Location = new System.Drawing.Point(12, 152), Size = new System.Drawing.Size(80, 28), Enabled = false };
            gbSer.Controls.Add(btnSerialSend);

            // ---- Common connection parameter panel ----
            gbParam = new GroupBox { Text = "Connection parameters (editable before connecting, locked while connected)", Location = new System.Drawing.Point(12, 200), Size = new System.Drawing.Size(736, 172) };

            // Row 1: reconnect settings.
            chkAutoReconnect = new CheckBox { Text = "Auto reconnect", Location = new System.Drawing.Point(12, 22), AutoSize = true, Checked = true };
            gbParam.Controls.Add(chkAutoReconnect);
            gbParam.Controls.Add(new Label { Text = "Max retries:", Location = new System.Drawing.Point(128, 26), AutoSize = true });
            txtMaxRetry = new TextBox { Text = "0", Location = new System.Drawing.Point(206, 22), Size = new System.Drawing.Size(40, 23) };
            gbParam.Controls.Add(txtMaxRetry);
            gbParam.Controls.Add(new Label { Text = "(0 = unlimited)", Location = new System.Drawing.Point(256, 26), AutoSize = true });
            gbParam.Controls.Add(new Label { Text = "Backoff:", Location = new System.Drawing.Point(392, 26), AutoSize = true });
            txtReconnectBase = new TextBox { Text = "1000", Location = new System.Drawing.Point(452, 22), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtReconnectBase);
            gbParam.Controls.Add(new Label { Text = "max", Location = new System.Drawing.Point(508, 26), AutoSize = true });
            txtReconnectMax = new TextBox { Text = "30000", Location = new System.Drawing.Point(542, 22), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtReconnectMax);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(598, 26), AutoSize = true });

            // Row 2: framing and min write gap.
            gbParam.Controls.Add(new Label { Text = "Frame end", Location = new System.Drawing.Point(12, 60), AutoSize = true });
            cboDelimiter = new ComboBox { Location = new System.Drawing.Point(82, 56), Size = new System.Drawing.Size(120, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboDelimiter.Items.AddRange(new object[] { "CRLF (\\r\\n)", "LF (\\n)", "None" });
            cboDelimiter.SelectedIndex = 0;
            gbParam.Controls.Add(cboDelimiter);
            gbParam.Controls.Add(new Label { Text = "Min write gap", Location = new System.Drawing.Point(230, 60), AutoSize = true });
            txtWriteMin = new TextBox { Text = "20", Location = new System.Drawing.Point(330, 56), Size = new System.Drawing.Size(45, 23) };
            gbParam.Controls.Add(txtWriteMin);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(382, 60), AutoSize = true });

            // Row 3: write retry.
            gbParam.Controls.Add(new Label { Text = "Write retry", Location = new System.Drawing.Point(12, 94), AutoSize = true });
            txtWriteRetry = new TextBox { Text = "3", Location = new System.Drawing.Point(92, 90), Size = new System.Drawing.Size(40, 23) };
            gbParam.Controls.Add(txtWriteRetry);
            gbParam.Controls.Add(new Label { Text = "x / gap", Location = new System.Drawing.Point(148, 94), AutoSize = true });
            txtWriteRetryInt = new TextBox { Text = "30", Location = new System.Drawing.Point(210, 90), Size = new System.Drawing.Size(45, 23) };
            gbParam.Controls.Add(txtWriteRetryInt);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(268, 94), AutoSize = true });

            // Row 4: heartbeat and silence timeout.
            chkHeartbeat = new CheckBox { Text = "Heartbeat", Location = new System.Drawing.Point(12, 128), AutoSize = true, Checked = false };
            gbParam.Controls.Add(chkHeartbeat);
            gbParam.Controls.Add(new Label { Text = "interval", Location = new System.Drawing.Point(115, 132), AutoSize = true });
            txtHeartbeatInt = new TextBox { Text = "30000", Location = new System.Drawing.Point(175, 128), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtHeartbeatInt);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(233, 132), AutoSize = true });
            gbParam.Controls.Add(new Label { Text = "Silence timeout", Location = new System.Drawing.Point(310, 132), AutoSize = true });
            txtSilence = new TextBox { Text = "15000", Location = new System.Drawing.Point(420, 128), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtSilence);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(476, 132), AutoSize = true });

            // ---- Log group ----
            var gbLog = new GroupBox { Text = "Receive / Log", Location = new System.Drawing.Point(12, 386), Size = new System.Drawing.Size(736, 160) };
            txtLog = new TextBox
            {
                Location = new System.Drawing.Point(12, 22),
                Size = new System.Drawing.Size(712, 96),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new System.Drawing.Font("Consolas", 9f)
            };
            gbLog.Controls.Add(txtLog);
            btnClearLog = new Button { Text = "Clear log", Location = new System.Drawing.Point(634, 126), Size = new System.Drawing.Size(90, 28) };
            gbLog.Controls.Add(btnClearLog);

            Controls.Add(gbTcp);
            Controls.Add(gbSer);
            Controls.Add(gbParam);
            Controls.Add(gbLog);

            btnTcpConnect.Click += BtnTcpConnect_Click;
            btnTcpDisconnect.Click += BtnTcpDisconnect_Click;
            btnTcpSend.Click += BtnTcpSend_Click;
            btnSerialConnect.Click += BtnSerialConnect_Click;
            btnSerialDisconnect.Click += BtnSerialDisconnect_Click;
            btnSerialSend.Click += BtnSerialSend_Click;
            btnClearLog.Click += BtnClearLog_Click;
            FormClosing += MainForm_FormClosing;

            // Tooltips remove any ambiguity about parameter semantics (especially 0 = unlimited).
            _toolTip = new ToolTip();
            _toolTip.SetToolTip(chkAutoReconnect, "When checked, the communicator will keep trying to reconnect after a connection failure.");
            _toolTip.SetToolTip(txtMaxRetry, "0 = unlimited retries while auto reconnect is enabled. Any positive value caps the retry count.");
            _toolTip.SetToolTip(cboDelimiter, "Terminator appended to outbound frames and used to split inbound frames.");
            _toolTip.SetToolTip(txtHeartbeatInt, "Heartbeat request interval in milliseconds. Heartbeat must be enabled above.");
            _toolTip.SetToolTip(txtSilence, "If no data is received within this time, the connection is treated as dead and reconnect starts.");
            _toolTip.SetToolTip(chkTcpAbortiveClose, "When checked (Abortive), closing sends an RST so the peer (e.g. a code-jet printer) frees its connection slot at once. Uncheck (Graceful) for a normal FIN close that flushes pending data.");
        }

        private void InitializeCommunicators()
        {
            _tcp = new TcpCommunicator(new TcpCommunicatorConfig
            {
                Host = "127.0.0.1",
                Port = 502,
                Framing = FramingMode.Delimiter,
                WriteMinIntervalMs = 20
            });
            _tcp.SyncContext = SynchronizationContext.Current; // Marshal events back to the UI thread.
            _tcp.StateChanged += (s, e) => OnTcpStateChanged(e);
            _tcp.DataReceived += (s, e) => Log("[TCP RX] " + FormatData(e.Data));
            _tcp.Error += (s, e) => Log($"[TCP ERROR/{e.Operation}] {e.Exception?.GetType().Name}: {e.Exception?.Message}");

            _serial = new SerialPortCommunicator(new SerialPortCommunicatorConfig
            {
                PortName = "",
                BaudRate = 9600,
                Framing = FramingMode.Delimiter,
                WriteMinIntervalMs = 20
            });
            _serial.SyncContext = SynchronizationContext.Current;
            _serial.StateChanged += (s, e) => OnSerialStateChanged(e);
            _serial.DataReceived += (s, e) => Log("[SERIAL RX] " + FormatData(e.Data));
            _serial.Error += (s, e) => Log($"[SERIAL ERROR/{e.Operation}] {e.Exception?.GetType().Name}: {e.Exception?.Message}");
        }

        /// <summary>Copies the values from the parameter panel into both communicators' configuration (called before connecting).</summary>
        private void ApplyConfigFromUI()
        {
            byte[] delim = DelimiterFromCombo();
            var targets = new CommunicatorConfig[] { _tcp.Config, _serial.Config };
            foreach (var cfg in targets)
            {
                cfg.AutoReconnect = chkAutoReconnect.Checked;
                if (int.TryParse(txtMaxRetry.Text.Trim(), out int mr)) cfg.MaxReconnectAttempts = mr;
                if (int.TryParse(txtReconnectBase.Text.Trim(), out int rb)) cfg.ReconnectBaseDelayMs = rb;
                if (int.TryParse(txtReconnectMax.Text.Trim(), out int rm)) cfg.ReconnectMaxDelayMs = rm;
                cfg.FrameDelimiter = delim;
                cfg.Framing = (delim == null || delim.Length == 0) ? FramingMode.Raw : FramingMode.Delimiter;
                if (int.TryParse(txtWriteMin.Text.Trim(), out int wm)) cfg.WriteMinIntervalMs = wm;
                if (int.TryParse(txtWriteRetry.Text.Trim(), out int wr)) cfg.WriteRetryCount = wr;
                if (int.TryParse(txtWriteRetryInt.Text.Trim(), out int wri)) cfg.WriteRetryIntervalMs = wri;
                cfg.HeartbeatEnabled = chkHeartbeat.Checked;
                if (int.TryParse(txtHeartbeatInt.Text.Trim(), out int hi)) cfg.HeartbeatIntervalMs = hi;
                if (int.TryParse(txtSilence.Text.Trim(), out int si)) cfg.HeartbeatSilenceTimeoutMs = si;
            }
        }

        private byte[] DelimiterFromCombo()
        {
            string s = cboDelimiter.SelectedItem?.ToString() ?? "CRLF";
            if (s.StartsWith("CRLF")) return new byte[] { (byte)'\r', (byte)'\n' };
            if (s.StartsWith("LF")) return new byte[] { (byte)'\n' };
            return new byte[0]; // No terminator -> Raw.
        }

        /// <summary>Locks the parameter panel while either device is active or in transition, and unlocks it once both are disconnected.</summary>
        private void UpdateConfigPanelLock()
        {
            bool lockPanel = IsBusy(_tcp.State) || IsBusy(_serial.State);
            foreach (Control c in gbParam.Controls) c.Enabled = !lockPanel;
        }

        private static bool IsBusy(ConnectionState s) => s != ConnectionState.Disconnected && s != ConnectionState.Error;

        private void Log(string line)
        {
            string entry = $"{DateTime.Now:HH:mm:ss.fff} {line}\r\n";
            if (_log.Length > MaxLogChars) _log.Remove(0, _log.Length - MaxLogChars + 1000);
            _log.Append(entry);
            if (txtLog != null && !txtLog.IsDisposed) txtLog.AppendText(entry);
        }

        private static string FormatData(byte[] data)
        {
            if (data == null || data.Length == 0) return "(empty)";
            bool printable = true;
            foreach (byte b in data)
            {
                if (b < 0x20 && b != (byte)'\r' && b != (byte)'\n' && b != (byte)'\t') { printable = false; break; }
            }
            return printable ? ByteArrayConverter.ToText(data) : ByteArrayConverter.ToHex(data);
        }

        private void OnTcpStateChanged(ConnectionStateChangedEventArgs e)
        {
            lblTcpStatus.Text = "State: " + StateText(e.NewState);
            bool connected = e.NewState == ConnectionState.Connected;
            bool busy = e.NewState == ConnectionState.Connecting || e.NewState == ConnectionState.Reconnecting;
            btnTcpConnect.Enabled = !connected && !busy;
            btnTcpDisconnect.Enabled = connected || e.NewState == ConnectionState.Reconnecting;
            btnTcpSend.Enabled = connected;
            Log($"[TCP] State: {StateText(e.OldState)} -> {StateText(e.NewState)}{(e.Message == null ? "" : " (" + e.Message + ")")}");
            UpdateConfigPanelLock();
        }

        private void OnSerialStateChanged(ConnectionStateChangedEventArgs e)
        {
            lblSerialStatus.Text = "State: " + StateText(e.NewState);
            bool connected = e.NewState == ConnectionState.Connected;
            bool busy = e.NewState == ConnectionState.Connecting || e.NewState == ConnectionState.Reconnecting;
            btnSerialConnect.Enabled = !connected && !busy;
            btnSerialDisconnect.Enabled = connected || e.NewState == ConnectionState.Reconnecting;
            btnSerialSend.Enabled = connected;
            Log($"[SERIAL] State: {StateText(e.OldState)} -> {StateText(e.NewState)}{(e.Message == null ? "" : " (" + e.Message + ")")}");
            UpdateConfigPanelLock();
        }

        private static string StateText(ConnectionState s) => s switch
        {
            ConnectionState.Disconnected => "Disconnected",
            ConnectionState.Connecting => "Connecting",
            ConnectionState.Connected => "Connected",
            ConnectionState.Reconnecting => "Reconnecting",
            ConnectionState.Disconnecting => "Disconnecting",
            ConnectionState.Error => "Error",
            _ => s.ToString()
        };

        // ---- TCP events ----
        private async void BtnTcpConnect_Click(object sender, EventArgs e)
        {
            if (!btnTcpConnect.Enabled) return;
            btnTcpConnect.Enabled = false; // Guard against a second click.
            try
            {
                if (!int.TryParse(txtPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
                {
                    Log("[TCP] Invalid port number.");
                    btnTcpConnect.Enabled = true;
                    return;
                }
                ApplyConfigFromUI(); // Push the panel values into the configuration before connecting.
                _tcp.Config.Host = txtIp.Text.Trim();
                _tcp.Config.Port = port;
                _tcp.Config.CloseMode = chkTcpAbortiveClose.Checked ? TcpCloseMode.Abortive : TcpCloseMode.Graceful;
                await _tcp.OpenAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[TCP] Connect exception: {ex.Message}");
            }
            // The final button state is corrected by the StateChanged event.
        }

        private async void BtnTcpDisconnect_Click(object sender, EventArgs e)
        {
            if (!btnTcpDisconnect.Enabled) return;
            if (MessageBox.Show("Disconnect the TCP connection?", "Confirm disconnect", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            btnTcpDisconnect.Enabled = false;
            try { await _tcp.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log($"[TCP] Disconnect exception: {ex.Message}"); }
        }

        private async void BtnTcpSend_Click(object sender, EventArgs e)
        {
            if (!btnTcpSend.Enabled) return;
            string input = txtTcpSend.Text;
            if (string.IsNullOrEmpty(input)) { Log("[TCP] Send content is empty."); return; }
            byte[] data;
            try
            {
                data = cboTcpType.SelectedItem?.ToString() == "Hex"
                    ? ByteArrayConverter.FromHex(input)
                    : ByteArrayConverter.FromText(input);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to parse the send content: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            btnTcpSend.Enabled = false; // Safety rule 2: disable the button while sending.
            try
            {
                bool ok = await _tcp.WriteAsync(data).ConfigureAwait(false);
                Log(ok ? $"[TCP TX] {FormatData(data)}" : "[TCP] Send failed (not connected or retries exhausted).");
            }
            catch (Exception ex)
            {
                Log($"[TCP] Send exception: {ex.Message}");
            }
            finally
            {
                if (_tcp.State == ConnectionState.Connected) btnTcpSend.Enabled = true;
            }
        }

        // ---- Serial events ----
        private async void BtnSerialConnect_Click(object sender, EventArgs e)
        {
            if (!btnSerialConnect.Enabled) return;
            btnSerialConnect.Enabled = false;
            try
            {
                if (cboPort.SelectedItem == null)
                {
                    Log("[SERIAL] No port selected.");
                    btnSerialConnect.Enabled = true;
                    return;
                }
                if (!int.TryParse(cboBaud.SelectedItem?.ToString(), out int baud))
                {
                    Log("[SERIAL] Invalid baud rate.");
                    btnSerialConnect.Enabled = true;
                    return;
                }
                ApplyConfigFromUI(); // Push the panel values into the configuration before connecting.
                _serial.Config.PortName = cboPort.SelectedItem.ToString();
                _serial.Config.BaudRate = baud;
                await _serial.OpenAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[SERIAL] Connect exception: {ex.Message}");
            }
        }

        private async void BtnSerialDisconnect_Click(object sender, EventArgs e)
        {
            if (!btnSerialDisconnect.Enabled) return;
            if (MessageBox.Show("Disconnect the serial port?", "Confirm disconnect", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            btnSerialDisconnect.Enabled = false;
            try { await _serial.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log($"[SERIAL] Disconnect exception: {ex.Message}"); }
        }

        private async void BtnSerialSend_Click(object sender, EventArgs e)
        {
            if (!btnSerialSend.Enabled) return;
            string input = txtSerialSend.Text;
            if (string.IsNullOrEmpty(input)) { Log("[SERIAL] Send content is empty."); return; }
            byte[] data;
            try
            {
                data = cboSerialType.SelectedItem?.ToString() == "Hex"
                    ? ByteArrayConverter.FromHex(input)
                    : ByteArrayConverter.FromText(input);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to parse the send content: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            btnSerialSend.Enabled = false;
            try
            {
                bool ok = await _serial.WriteAsync(data).ConfigureAwait(false);
                Log(ok ? $"[SERIAL TX] {FormatData(data)}" : "[SERIAL] Send failed (not connected or retries exhausted).");
            }
            catch (Exception ex)
            {
                Log($"[SERIAL] Send exception: {ex.Message}");
            }
            finally
            {
                if (_serial.State == ConnectionState.Connected) btnSerialSend.Enabled = true;
            }
        }

        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            // Safety rule 9: clearing (a delete-like action) requires confirmation.
            if (MessageBox.Show("Clear the receive log?", "Confirm clear", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _log.Clear();
            if (txtLog != null && !txtLog.IsDisposed) txtLog.Clear();
        }

        private void RefreshPortList()
        {
            // Safety rule 5: show empty and never crash when there is no data.
            try
            {
                cboPort.Items.Clear();
                foreach (string p in SerialPort.GetPortNames()) cboPort.Items.Add(p);
            }
            catch (Exception ex)
            {
                Log($"Failed to enumerate serial ports: {ex.Message}");
            }
            if (cboPort.Items.Count > 0) cboPort.SelectedIndex = 0;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Safety rule 6: detect and close device connections before exiting.
            try { if (_tcp != null && _tcp.State == ConnectionState.Connected) _ = _tcp.CloseAsync(); } catch { }
            try { if (_serial != null && _serial.State == ConnectionState.Connected) _ = _serial.CloseAsync(); } catch { }
            _tcp?.Dispose();
            _serial?.Dispose();
        }
    }
}
