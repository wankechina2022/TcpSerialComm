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
    /// 测试主窗体：仅用于验证两个通信类的读写/重连。
    /// 严格落实安全约定：单实例、防二次连接、发送防重复点击、退出前关闭设备、清空等危险操作确认。
    /// 新增「连接参数」面板：常用配置在界面可见可改，连接后锁定。
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

        // 公共参数面板控件
        private GroupBox gbParam;
        private CheckBox chkAutoReconnect, chkHeartbeat;
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
            Text = "TCP / 串口 读写测试工具";
            ClientSize = new System.Drawing.Size(660, 540);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // ---- TCP 组 ----
            var gbTcp = new GroupBox { Text = "TCP 客户端", Location = new System.Drawing.Point(12, 12), Size = new System.Drawing.Size(300, 150) };
            gbTcp.Controls.Add(new Label { Text = "IP:", Location = new System.Drawing.Point(12, 24), AutoSize = true });
            txtIp = new TextBox { Text = "127.0.0.1", Location = new System.Drawing.Point(48, 21), Size = new System.Drawing.Size(120, 23) };
            gbTcp.Controls.Add(txtIp);
            gbTcp.Controls.Add(new Label { Text = "端口:", Location = new System.Drawing.Point(178, 24), AutoSize = true });
            txtPort = new TextBox { Text = "502", Location = new System.Drawing.Point(220, 21), Size = new System.Drawing.Size(60, 23) };
            gbTcp.Controls.Add(txtPort);

            btnTcpConnect = new Button { Text = "连接", Location = new System.Drawing.Point(12, 56), Size = new System.Drawing.Size(80, 28) };
            btnTcpDisconnect = new Button { Text = "断开", Location = new System.Drawing.Point(100, 56), Size = new System.Drawing.Size(80, 28), Enabled = false };
            lblTcpStatus = new Label { Text = "状态: 未连接", Location = new System.Drawing.Point(188, 60), AutoSize = true };
            gbTcp.Controls.Add(btnTcpConnect);
            gbTcp.Controls.Add(btnTcpDisconnect);
            gbTcp.Controls.Add(lblTcpStatus);

            gbTcp.Controls.Add(new Label { Text = "发送:", Location = new System.Drawing.Point(12, 96), AutoSize = true });
            txtTcpSend = new TextBox { Location = new System.Drawing.Point(48, 93), Size = new System.Drawing.Size(160, 23) };
            cboTcpType = new ComboBox { Location = new System.Drawing.Point(216, 93), Size = new System.Drawing.Size(70, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboTcpType.Items.AddRange(new object[] { "Text", "Hex" });
            cboTcpType.SelectedIndex = 0;
            gbTcp.Controls.Add(txtTcpSend);
            gbTcp.Controls.Add(cboTcpType);
            btnTcpSend = new Button { Text = "发送", Location = new System.Drawing.Point(12, 124), Size = new System.Drawing.Size(80, 28), Enabled = false };
            gbTcp.Controls.Add(btnTcpSend);

            // ---- 串口组 ----
            var gbSer = new GroupBox { Text = "串口", Location = new System.Drawing.Point(324, 12), Size = new System.Drawing.Size(300, 150) };
            gbSer.Controls.Add(new Label { Text = "端口:", Location = new System.Drawing.Point(12, 24), AutoSize = true });
            cboPort = new ComboBox { Location = new System.Drawing.Point(48, 21), Size = new System.Drawing.Size(100, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            gbSer.Controls.Add(cboPort);
            gbSer.Controls.Add(new Label { Text = "波特率:", Location = new System.Drawing.Point(156, 24), AutoSize = true });
            cboBaud = new ComboBox { Location = new System.Drawing.Point(206, 21), Size = new System.Drawing.Size(80, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboBaud.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200" });
            cboBaud.SelectedIndex = 0;
            gbSer.Controls.Add(cboBaud);

            btnSerialConnect = new Button { Text = "连接", Location = new System.Drawing.Point(12, 56), Size = new System.Drawing.Size(80, 28) };
            btnSerialDisconnect = new Button { Text = "断开", Location = new System.Drawing.Point(100, 56), Size = new System.Drawing.Size(80, 28), Enabled = false };
            lblSerialStatus = new Label { Text = "状态: 未连接", Location = new System.Drawing.Point(188, 60), AutoSize = true };
            gbSer.Controls.Add(btnSerialConnect);
            gbSer.Controls.Add(btnSerialDisconnect);
            gbSer.Controls.Add(lblSerialStatus);

            gbSer.Controls.Add(new Label { Text = "发送:", Location = new System.Drawing.Point(12, 96), AutoSize = true });
            txtSerialSend = new TextBox { Location = new System.Drawing.Point(48, 93), Size = new System.Drawing.Size(150, 23) };
            cboSerialType = new ComboBox { Location = new System.Drawing.Point(206, 93), Size = new System.Drawing.Size(70, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboSerialType.Items.AddRange(new object[] { "Text", "Hex" });
            cboSerialType.SelectedIndex = 0;
            gbSer.Controls.Add(txtSerialSend);
            gbSer.Controls.Add(cboSerialType);
            btnSerialSend = new Button { Text = "发送", Location = new System.Drawing.Point(12, 124), Size = new System.Drawing.Size(80, 28), Enabled = false };
            gbSer.Controls.Add(btnSerialSend);

            // ---- 公共连接参数面板 ----
            gbParam = new GroupBox { Text = "连接参数（连接前可改，连接后自动锁定）", Location = new System.Drawing.Point(12, 170), Size = new System.Drawing.Size(636, 150) };

            chkAutoReconnect = new CheckBox { Text = "自动重连", Location = new System.Drawing.Point(12, 20), AutoSize = true, Checked = true };
            gbParam.Controls.Add(chkAutoReconnect);
            gbParam.Controls.Add(new Label { Text = "重连上限", Location = new System.Drawing.Point(110, 24), AutoSize = true });
            txtMaxRetry = new TextBox { Text = "0", Location = new System.Drawing.Point(170, 20), Size = new System.Drawing.Size(42, 23) };
            gbParam.Controls.Add(txtMaxRetry);
            gbParam.Controls.Add(new Label { Text = "次 (0=无限)", Location = new System.Drawing.Point(216, 24), AutoSize = true });
            gbParam.Controls.Add(new Label { Text = "重连间隔", Location = new System.Drawing.Point(300, 24), AutoSize = true });
            txtReconnectBase = new TextBox { Text = "1000", Location = new System.Drawing.Point(360, 20), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtReconnectBase);
            gbParam.Controls.Add(new Label { Text = "最大", Location = new System.Drawing.Point(415, 24), AutoSize = true });
            txtReconnectMax = new TextBox { Text = "30000", Location = new System.Drawing.Point(455, 20), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtReconnectMax);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(510, 24), AutoSize = true });

            gbParam.Controls.Add(new Label { Text = "帧结束符", Location = new System.Drawing.Point(12, 56), AutoSize = true });
            cboDelimiter = new ComboBox { Location = new System.Drawing.Point(75, 52), Size = new System.Drawing.Size(96, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cboDelimiter.Items.AddRange(new object[] { "CRLF (\\r\\n)", "LF (\\n)", "无" });
            cboDelimiter.SelectedIndex = 0;
            gbParam.Controls.Add(cboDelimiter);
            gbParam.Controls.Add(new Label { Text = "最小写间隔", Location = new System.Drawing.Point(185, 56), AutoSize = true });
            txtWriteMin = new TextBox { Text = "20", Location = new System.Drawing.Point(255, 52), Size = new System.Drawing.Size(42, 23) };
            gbParam.Controls.Add(txtWriteMin);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(302, 56), AutoSize = true });
            gbParam.Controls.Add(new Label { Text = "写重试", Location = new System.Drawing.Point(340, 56), AutoSize = true });
            txtWriteRetry = new TextBox { Text = "3", Location = new System.Drawing.Point(388, 52), Size = new System.Drawing.Size(36, 23) };
            gbParam.Controls.Add(txtWriteRetry);
            gbParam.Controls.Add(new Label { Text = "次 / 间隔", Location = new System.Drawing.Point(428, 56), AutoSize = true });
            txtWriteRetryInt = new TextBox { Text = "30", Location = new System.Drawing.Point(490, 52), Size = new System.Drawing.Size(42, 23) };
            gbParam.Controls.Add(txtWriteRetryInt);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(537, 56), AutoSize = true });

            chkHeartbeat = new CheckBox { Text = "心跳", Location = new System.Drawing.Point(12, 82), AutoSize = true, Checked = false };
            gbParam.Controls.Add(chkHeartbeat);
            gbParam.Controls.Add(new Label { Text = "间隔", Location = new System.Drawing.Point(70, 86), AutoSize = true });
            txtHeartbeatInt = new TextBox { Text = "30000", Location = new System.Drawing.Point(105, 82), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtHeartbeatInt);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(160, 86), AutoSize = true });
            gbParam.Controls.Add(new Label { Text = "静默超时", Location = new System.Drawing.Point(215, 86), AutoSize = true });
            txtSilence = new TextBox { Text = "15000", Location = new System.Drawing.Point(280, 82), Size = new System.Drawing.Size(50, 23) };
            gbParam.Controls.Add(txtSilence);
            gbParam.Controls.Add(new Label { Text = "ms", Location = new System.Drawing.Point(335, 86), AutoSize = true });

            // ---- 日志组 ----
            var gbLog = new GroupBox { Text = "接收 / 日志", Location = new System.Drawing.Point(12, 330), Size = new System.Drawing.Size(636, 180) };
            txtLog = new TextBox
            {
                Location = new System.Drawing.Point(12, 22),
                Size = new System.Drawing.Size(612, 120),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new System.Drawing.Font("Consolas", 9f)
            };
            gbLog.Controls.Add(txtLog);
            btnClearLog = new Button { Text = "清空日志", Location = new System.Drawing.Point(534, 148), Size = new System.Drawing.Size(90, 28) };
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
            _tcp.SyncContext = SynchronizationContext.Current; // 事件回到 UI 线程
            _tcp.StateChanged += (s, e) => OnTcpStateChanged(e);
            _tcp.DataReceived += (s, e) => Log("[TCP 收到] " + FormatData(e.Data));
            _tcp.Error += (s, e) => Log($"[TCP 错误/{e.Operation}] {e.Exception?.GetType().Name}: {e.Exception?.Message}");

            _serial = new SerialPortCommunicator(new SerialPortCommunicatorConfig
            {
                PortName = "",
                BaudRate = 9600,
                Framing = FramingMode.Delimiter,
                WriteMinIntervalMs = 20
            });
            _serial.SyncContext = SynchronizationContext.Current;
            _serial.StateChanged += (s, e) => OnSerialStateChanged(e);
            _serial.DataReceived += (s, e) => Log("[串口 收到] " + FormatData(e.Data));
            _serial.Error += (s, e) => Log($"[串口 错误/{e.Operation}] {e.Exception?.GetType().Name}: {e.Exception?.Message}");
        }

        /// <summary>把参数面板上的值写入两个通信类的配置（连接前调用）</summary>
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
            return new byte[0]; // 无结束符 -> Raw
        }

        /// <summary>任一设备处于活动/过渡状态时锁定参数面板，全部断开后解锁</summary>
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
            if (data == null || data.Length == 0) return "(空)";
            bool printable = true;
            foreach (byte b in data)
            {
                if (b < 0x20 && b != (byte)'\r' && b != (byte)'\n' && b != (byte)'\t') { printable = false; break; }
            }
            return printable ? ByteArrayConverter.ToText(data) : ByteArrayConverter.ToHex(data);
        }

        private void OnTcpStateChanged(ConnectionStateChangedEventArgs e)
        {
            lblTcpStatus.Text = "状态: " + StateText(e.NewState);
            bool connected = e.NewState == ConnectionState.Connected;
            bool busy = e.NewState == ConnectionState.Connecting || e.NewState == ConnectionState.Reconnecting;
            btnTcpConnect.Enabled = !connected && !busy;
            btnTcpDisconnect.Enabled = connected || e.NewState == ConnectionState.Reconnecting;
            btnTcpSend.Enabled = connected;
            Log($"[TCP] 状态: {StateText(e.OldState)} -> {StateText(e.NewState)}{(e.Message == null ? "" : " (" + e.Message + ")")}");
            UpdateConfigPanelLock();
        }

        private void OnSerialStateChanged(ConnectionStateChangedEventArgs e)
        {
            lblSerialStatus.Text = "状态: " + StateText(e.NewState);
            bool connected = e.NewState == ConnectionState.Connected;
            bool busy = e.NewState == ConnectionState.Connecting || e.NewState == ConnectionState.Reconnecting;
            btnSerialConnect.Enabled = !connected && !busy;
            btnSerialDisconnect.Enabled = connected || e.NewState == ConnectionState.Reconnecting;
            btnSerialSend.Enabled = connected;
            Log($"[串口] 状态: {StateText(e.OldState)} -> {StateText(e.NewState)}{(e.Message == null ? "" : " (" + e.Message + ")")}");
            UpdateConfigPanelLock();
        }

        private static string StateText(ConnectionState s) => s switch
        {
            ConnectionState.Disconnected => "未连接",
            ConnectionState.Connecting => "连接中",
            ConnectionState.Connected => "已连接",
            ConnectionState.Reconnecting => "重连中",
            ConnectionState.Disconnecting => "断开中",
            ConnectionState.Error => "错误",
            _ => s.ToString()
        };

        // ---- TCP 事件 ----
        private async void BtnTcpConnect_Click(object sender, EventArgs e)
        {
            if (!btnTcpConnect.Enabled) return;
            btnTcpConnect.Enabled = false; // 防二次点击
            try
            {
                if (!int.TryParse(txtPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
                {
                    Log("[TCP] 端口号非法");
                    btnTcpConnect.Enabled = true;
                    return;
                }
                ApplyConfigFromUI(); // 连接前把面板参数写入配置
                _tcp.Config.Host = txtIp.Text.Trim();
                _tcp.Config.Port = port;
                await _tcp.OpenAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[TCP] 连接异常: {ex.Message}");
            }
            // 按钮最终状态由 StateChanged 事件修正
        }

        private async void BtnTcpDisconnect_Click(object sender, EventArgs e)
        {
            if (!btnTcpDisconnect.Enabled) return;
            if (MessageBox.Show("确定要断开 TCP 连接吗？", "确认断开", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            btnTcpDisconnect.Enabled = false;
            try { await _tcp.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log($"[TCP] 断开异常: {ex.Message}"); }
        }

        private async void BtnTcpSend_Click(object sender, EventArgs e)
        {
            if (!btnTcpSend.Enabled) return;
            string input = txtTcpSend.Text;
            if (string.IsNullOrEmpty(input)) { Log("[TCP] 发送内容为空"); return; }
            byte[] data;
            try
            {
                data = cboTcpType.SelectedItem?.ToString() == "Hex"
                    ? ByteArrayConverter.FromHex(input)
                    : ByteArrayConverter.FromText(input);
            }
            catch (Exception ex)
            {
                MessageBox.Show("发送内容解析失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            btnTcpSend.Enabled = false; // 安全约定2：发送过程中禁用
            try
            {
                bool ok = await _tcp.WriteAsync(data).ConfigureAwait(false);
                Log(ok ? $"[TCP 发送] {FormatData(data)}" : "[TCP] 发送失败（未连接或重试耗尽）");
            }
            catch (Exception ex)
            {
                Log($"[TCP] 发送异常: {ex.Message}");
            }
            finally
            {
                if (_tcp.State == ConnectionState.Connected) btnTcpSend.Enabled = true;
            }
        }

        // ---- 串口事件 ----
        private async void BtnSerialConnect_Click(object sender, EventArgs e)
        {
            if (!btnSerialConnect.Enabled) return;
            btnSerialConnect.Enabled = false;
            try
            {
                if (cboPort.SelectedItem == null)
                {
                    Log("[串口] 未选择端口");
                    btnSerialConnect.Enabled = true;
                    return;
                }
                if (!int.TryParse(cboBaud.SelectedItem?.ToString(), out int baud))
                {
                    Log("[串口] 波特率非法");
                    btnSerialConnect.Enabled = true;
                    return;
                }
                ApplyConfigFromUI(); // 连接前把面板参数写入配置
                _serial.Config.PortName = cboPort.SelectedItem.ToString();
                _serial.Config.BaudRate = baud;
                await _serial.OpenAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[串口] 连接异常: {ex.Message}");
            }
        }

        private async void BtnSerialDisconnect_Click(object sender, EventArgs e)
        {
            if (!btnSerialDisconnect.Enabled) return;
            if (MessageBox.Show("确定要断开串口吗？", "确认断开", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            btnSerialDisconnect.Enabled = false;
            try { await _serial.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log($"[串口] 断开异常: {ex.Message}"); }
        }

        private async void BtnSerialSend_Click(object sender, EventArgs e)
        {
            if (!btnSerialSend.Enabled) return;
            string input = txtSerialSend.Text;
            if (string.IsNullOrEmpty(input)) { Log("[串口] 发送内容为空"); return; }
            byte[] data;
            try
            {
                data = cboSerialType.SelectedItem?.ToString() == "Hex"
                    ? ByteArrayConverter.FromHex(input)
                    : ByteArrayConverter.FromText(input);
            }
            catch (Exception ex)
            {
                MessageBox.Show("发送内容解析失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            btnSerialSend.Enabled = false;
            try
            {
                bool ok = await _serial.WriteAsync(data).ConfigureAwait(false);
                Log(ok ? $"[串口 发送] {FormatData(data)}" : "[串口] 发送失败（未连接或重试耗尽）");
            }
            catch (Exception ex)
            {
                Log($"[串口] 发送异常: {ex.Message}");
            }
            finally
            {
                if (_serial.State == ConnectionState.Connected) btnSerialSend.Enabled = true;
            }
        }

        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            // 安全约定9：清空（类删除操作）需确认
            if (MessageBox.Show("确定要清空接收日志吗？", "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _log.Clear();
            if (txtLog != null && !txtLog.IsDisposed) txtLog.Clear();
        }

        private void RefreshPortList()
        {
            // 安全约定5：无数据时显示空，不崩溃
            try
            {
                cboPort.Items.Clear();
                foreach (string p in SerialPort.GetPortNames()) cboPort.Items.Add(p);
            }
            catch (Exception ex)
            {
                Log($"枚举串口失败: {ex.Message}");
            }
            if (cboPort.Items.Count > 0) cboPort.SelectedIndex = 0;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 安全约定6：退出前检测并关闭设备连接
            try { if (_tcp != null && _tcp.State == ConnectionState.Connected) _ = _tcp.CloseAsync(); } catch { }
            try { if (_serial != null && _serial.State == ConnectionState.Connected) _ = _serial.CloseAsync(); } catch { }
            _tcp?.Dispose();
            _serial?.Dispose();
        }
    }
}
