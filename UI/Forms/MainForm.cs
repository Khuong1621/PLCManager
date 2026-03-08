// ============================================================
// File: UI/Forms/MainForm.cs
// Description: Main WinForms Application Window
//              Senior Dev: MVP-like pattern, thread-safe UI updates
// ============================================================

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;
using PLCManager.Services;

namespace PLCManager.UI.Forms
{
    public partial class MainForm : Form
    {
        // ------------------------------------------------
        // Fields
        // ------------------------------------------------
        private IPLCCommunication? _plc;
        private ITagMonitor? _monitor;
        private readonly TagRepository _tagRepo;
        private readonly PLCConnectionFactory _factory;
        private readonly AppLogger _logger;
        private System.Windows.Forms.Timer _statsTimer = null!;

        // ------------------------------------------------
        // Constructor
        // ------------------------------------------------
        public MainForm()
        {
            _logger = AppLogger.Instance;
            _logger.SetLogFile("plc_manager.log");
            _tagRepo = new TagRepository();
            _tagRepo.LoadDefaults();
            _factory = new PLCConnectionFactory(_logger);

            InitializeComponent();

            // Wire logger to UI log list
            _logger.LogAdded += OnLogAdded;

            SetupStatsTimer();
            UpdateUIState(ConnectionState.Disconnected);
            _logger.Info("MainForm", "PLC Manager started");
        }

        // ------------------------------------------------
        // InitializeComponent (Designer-equivalent in code)
        // ------------------------------------------------
        private void InitializeComponent()
        {
            this.SuspendLayout();

            // Form properties
            this.Text = "PLC Manager ";
            this.Size = new Size(1280, 820);
            this.MinimumSize = new Size(1024, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;

            BuildLayout();

            this.ResumeLayout(false);
        }

        // ------------------------------------------------
        // Layout Builder
        // ------------------------------------------------
        private TabControl _tabMain = null!;
        private Panel _panelTop = null!;

        // Connection controls
        private ComboBox _cmbBrand = null!;
        private ComboBox _cmbCommType = null!;
        private TextBox _txtHost = null!;
        private NumericUpDown _nudPort = null!;
        private ComboBox _cmbComPort = null!;
        private NumericUpDown _nudBaud = null!;
        private Button _btnConnect = null!;
        private Button _btnDisconnect = null!;
        private Label _lblStatus = null!;

        // Tag grid
        private DataGridView _gridTags = null!;

        // Read/Write panel
        private ComboBox _cmbDevice = null!;
        private NumericUpDown _nudAddress = null!;
        private NumericUpDown _nudCount = null!;
        private TextBox _txtWriteValue = null!;
        private Button _btnRead = null!;
        private Button _btnWrite = null!;
        private RichTextBox _rtbResult = null!;

        // Log
        private ListBox _lstLog = null!;

        // Stats
        private Label _lblStats = null!;

        private void BuildLayout()
        {
            // ---- Top Panel (connection bar) ----
            _panelTop = CreatePanel(0, 0, ClientSize.Width, 90, Color.FromArgb(45, 45, 48));
            _panelTop.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            int x = 10;
            AddLabel(_panelTop, "Brand:", x, 15);
            _cmbBrand = AddComboBox(_panelTop, x + 45, 10, 110, new[] { "Mitsubishi", "Omron" });
            x += 170;

            AddLabel(_panelTop, "Channel:", x, 15);
            _cmbCommType = AddComboBox(_panelTop, x + 60, 10, 100, new[] { "TCP/IP", "RS-232" });
            _cmbCommType.SelectedIndexChanged += OnCommTypeChanged;
            x += 175;

            AddLabel(_panelTop, "IP/Host:", x, 15);
            _txtHost = AddTextBox(_panelTop, x + 60, 10, 130, "192.168.1.10");
            x += 205;

            AddLabel(_panelTop, "Port:", x, 15);
            _nudPort = AddNumeric(_panelTop, x + 40, 10, 70, 1, 65535, 5006);
            x += 125;

            AddLabel(_panelTop, "COM:", x, 15);
            _cmbComPort = AddComboBox(_panelTop, x + 40, 10, 80,
                System.IO.Ports.SerialPort.GetPortNames());
            _cmbComPort.Enabled = false;
            x += 135;

            AddLabel(_panelTop, "Baud:", x, 15);
            _nudBaud = AddNumeric(_panelTop, x + 40, 10, 80, 1200, 115200, 9600);
            _nudBaud.Enabled = false;
            x += 135;

            _btnConnect = AddButton(_panelTop, x, 8, 90, 35, "⚡ Connect", Color.FromArgb(0, 122, 204));
            _btnConnect.Click += OnConnectClick;
            x += 100;

            _btnDisconnect = AddButton(_panelTop, x, 8, 105, 35, "✖ Disconnect", Color.FromArgb(180, 0, 0));
            _btnDisconnect.Click += OnDisconnectClick;
            _btnDisconnect.Enabled = false;

            // Status label (second row)
            _lblStatus = new Label
            {
                Text = "● DISCONNECTED",
                ForeColor = Color.Gray,
                Font = new Font("Consolas", 10, FontStyle.Bold),
                Location = new Point(10, 55),
                Size = new Size(300, 25),
                BackColor = Color.Transparent
            };
            _panelTop.Controls.Add(_lblStatus);

            _lblStats = new Label
            {
                Text = "",
                ForeColor = Color.FromArgb(150, 200, 150),
                Font = new Font("Consolas", 8),
                Location = new Point(320, 55),
                Size = new Size(700, 25),
                BackColor = Color.Transparent
            };
            _panelTop.Controls.Add(_lblStats);

            this.Controls.Add(_panelTop);

            // ---- Tab Control ----
            _tabMain = new TabControl
            {
                Location = new Point(0, 95),
                Size = new Size(ClientSize.Width, ClientSize.Height - 95),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9)
            };

            _tabMain.TabPages.Add(BuildTagMonitorTab());
            _tabMain.TabPages.Add(BuildReadWriteTab());
            _tabMain.TabPages.Add(BuildLogTab());
            _tabMain.TabPages.Add(BuildAboutTab());

            this.Controls.Add(_tabMain);
        }

        // ---- Tab: Tag Monitor ----
        private TabPage BuildTagMonitorTab()
        {
            var tab = new TabPage("📊 Tag Monitor") { BackColor = Color.FromArgb(37, 37, 38) };

            _gridTags = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(60, 60, 60),
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(37, 37, 38),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(0, 122, 204),
                    Font = new Font("Consolas", 9)
                },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(45, 45, 48),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold)
                },
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            _gridTags.Columns.Add("TagName", "Tag Name");
            _gridTags.Columns.Add("Device", "Device");
            _gridTags.Columns.Add("Address", "Address");
            _gridTags.Columns.Add("RawValue", "Raw Value");
            _gridTags.Columns.Add("EngValue", "Eng. Value");
            _gridTags.Columns.Add("Unit", "Unit");
            _gridTags.Columns.Add("LastUpdate", "Last Update");
            _gridTags.Columns.Add("Description", "Description");

            // Populate from repo
            foreach (var tag in _tagRepo.GetAll())
            {
                _gridTags.Rows.Add(
                    tag.TagName, tag.Device, tag.Address,
                    tag.Value ?? "-", tag.EngineeringValue.ToString("F2"),
                    tag.Unit, tag.LastUpdated.ToString("HH:mm:ss.fff"), tag.Description);
            }

            // Toolbar above grid
            var toolbar = new Panel { Height = 35, Dock = DockStyle.Top, BackColor = Color.FromArgb(45, 45, 48) };
            var btnStartMon = AddButton(toolbar, 5, 3, 130, 28, "▶ Start Monitor", Color.FromArgb(0, 150, 50));
            btnStartMon.Click += OnStartMonitorClick;
            var btnStopMon = AddButton(toolbar, 145, 3, 130, 28, "■ Stop Monitor", Color.FromArgb(150, 0, 0));
            btnStopMon.Click += OnStopMonitorClick;
            var btnRefresh = AddButton(toolbar, 285, 3, 100, 28, "↺ Refresh", Color.FromArgb(80, 80, 80));
            btnRefresh.Click += (s, e) => RefreshTagGrid();

            tab.Controls.Add(_gridTags);
            tab.Controls.Add(toolbar);
            return tab;
        }

        // ---- Tab: Read/Write ----
        private TabPage BuildReadWriteTab()
        {
            var tab = new TabPage("✏️ Read / Write") { BackColor = Color.FromArgb(37, 37, 38) };

            var panel = new Panel { Dock = DockStyle.Fill };
            tab.Controls.Add(panel);

            int y = 20;
            AddLabel(panel, "Device:", 20, y, Color.White);
            _cmbDevice = AddComboBox(panel, 80, y - 3, 80,
                Enum.GetNames(typeof(DeviceType)));
            _cmbDevice.SelectedIndex = 6; // D

            AddLabel(panel, "Address:", 180, y, Color.White);
            _nudAddress = AddNumeric(panel, 240, y - 3, 80, 0, 65535, 100);

            AddLabel(panel, "Count:", 340, y, Color.White);
            _nudCount = AddNumeric(panel, 390, y - 3, 60, 1, 960, 1);

            y += 50;
            AddLabel(panel, "Write Value:", 20, y, Color.White);
            _txtWriteValue = AddTextBox(panel, 110, y - 3, 200, "0");

            y += 50;
            _btnRead = AddButton(panel, 20, y, 120, 38, "📖 Read", Color.FromArgb(0, 122, 204));
            _btnRead.Click += OnReadClick;
            _btnWrite = AddButton(panel, 155, y, 120, 38, "✏ Write", Color.FromArgb(180, 90, 0));
            _btnWrite.Click += OnWriteClick;

            y += 65;
            AddLabel(panel, "Result:", 20, y, Color.White);
            _rtbResult = new RichTextBox
            {
                Location = new Point(20, y + 25),
                Size = new Size(800, 400),
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.LimeGreen,
                Font = new Font("Consolas", 10),
                ReadOnly = true,
                WordWrap = false
            };
            panel.Controls.Add(_rtbResult);

            return tab;
        }

        // ---- Tab: Log ----
        private TabPage BuildLogTab()
        {
            var tab = new TabPage("📋 Log") { BackColor = Color.FromArgb(37, 37, 38) };

            var toolbar = new Panel { Height = 35, Dock = DockStyle.Top, BackColor = Color.FromArgb(45, 45, 48) };
            var btnClear = AddButton(toolbar, 5, 3, 80, 28, "🗑 Clear", Color.FromArgb(80, 80, 80));
            btnClear.Click += (s, e) => _lstLog.Items.Clear();
            toolbar.Controls.Add(btnClear);

            _lstLog = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.LightGray,
                Font = new Font("Consolas", 9),
                HorizontalScrollbar = true,
                SelectionMode = SelectionMode.None
            };

            tab.Controls.Add(_lstLog);
            tab.Controls.Add(toolbar);
            return tab;
        }

        // ---- Tab: About ----
        private TabPage BuildAboutTab()
        {
            var tab = new TabPage("ℹ️ About") { BackColor = Color.FromArgb(37, 37, 38) };
            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10),
                ReadOnly = true,
                Text = @"
  ╔══════════════════════════════════════════════════════════════════╗
  ║          PLC MANAGER — Senior Dev C# WinForms Template          ║
  ╚══════════════════════════════════════════════════════════════════╝

  ARCHITECTURE PATTERNS IMPLEMENTED:
  ─────────────────────────────────────────────────────────────────
  ▸ OOP            : Inheritance, Polymorphism, Encapsulation
  ▸ SOLID          : Single Responsibility, Open/Closed, DI, ISP
  ▸ Design Patterns: Factory, Template Method, Observer, Singleton,
                     Strategy, Repository, Producer-Consumer
  ▸ Multi-threading: Task Parallel Library, CancellationToken,
                     SemaphoreSlim (mutex), BlockingCollection,
                     Interlocked, Thread-safe UI marshaling
  ▸ TCP/IP Socket  : TcpClient, NetworkStream, async I/O
  ▸ RS-232 Serial  : System.IO.Ports.SerialPort, full config

  PLC PROTOCOLS:
  ─────────────────────────────────────────────────────────────────
  ▸ Mitsubishi MELSEC SLMP  (3E Binary Frame, TCP/IP)
  ▸ Mitsubishi MC Protocol  (ASCII 1C Frame, RS-232)
  ▸ Omron FINS/TCP          (TCP/IP with node handshake)

  DEVICE SUPPORT:
  ─────────────────────────────────────────────────────────────────
  ▸ Mitsubishi Q-Series, iQ-R, iQ-F (FX5U)
  ▸ Omron CJ2, CS1, NX1, NJ

  TEAM LEAD PATTERNS:
  ─────────────────────────────────────────────────────────────────
  ▸ Interface-driven development (easy to swap drivers)
  ▸ Separation of Concerns (Core / Communication / UI / Services)
  ▸ Structured logging with levels (Debug/Info/Warning/Error)
  ▸ Result wrapper (PLCResult<T>) — no raw exceptions to UI
  ▸ Statistics & diagnostics built-in
  ▸ JSON configuration (tags, connections)
  ▸ Cancellation token everywhere (graceful shutdown)
  ▸ Async/Await throughout — no UI freezing
"
            };
            tab.Controls.Add(rtb);
            return tab;
        }

        // ================================================
        // Event Handlers
        // ================================================

        private async void OnConnectClick(object? sender, EventArgs e)
        {
            try
            {
                _btnConnect.Enabled = false;
                var config = BuildConnectionConfig();
                _plc?.Dispose();
                _plc = _factory.Create(config);
                _plc.ConnectionStateChanged += (s, state) => UpdateUIState(state);
                _plc.ErrorOccurred += (s, err) => ShowError(err);

                bool ok = await _plc.ConnectAsync();
                if (ok)
                {
                    _btnDisconnect.Enabled = true;
                    AppendResult($"[{DateTime.Now:HH:mm:ss}] Connected to {config.Name}");
                }
                else
                {
                    _btnConnect.Enabled = true;
                    ShowError("Connection failed");
                }
            }
            catch (Exception ex)
            {
                _btnConnect.Enabled = true;
                ShowError(ex.Message);
            }
        }

        private async void OnDisconnectClick(object? sender, EventArgs e)
        {
            if (_plc == null) return;
            _monitor?.StopMonitoring();
            await _plc.DisconnectAsync();
            _btnConnect.Enabled = true;
            _btnDisconnect.Enabled = false;
        }

        private void OnCommTypeChanged(object? sender, EventArgs e)
        {
            bool isTcp = _cmbCommType.SelectedItem?.ToString() == "TCP/IP";
            _txtHost.Enabled = isTcp;
            _nudPort.Enabled = isTcp;
            _cmbComPort.Enabled = !isTcp;
            _nudBaud.Enabled = !isTcp;
        }

        private async void OnReadClick(object? sender, EventArgs e)
        {
            if (_plc == null) { ShowError("Not connected"); return; }
            if (!Enum.TryParse<DeviceType>(_cmbDevice.SelectedItem?.ToString(), out var dev))
                return;

            int addr = (int)_nudAddress.Value;
            int count = (int)_nudCount.Value;

            var cts = new CancellationTokenSource(3000);
            var result = await _plc.ReadWordsAsync(dev, addr, count, cts.Token);

            if (result.Success && result.Data != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[READ] {dev}{addr} x{count} — OK ({result.ElapsedMs}ms)");
                sb.AppendLine($"DEC: {string.Join(", ", result.Data)}");
                sb.AppendLine($"HEX: {string.Join(", ", Array.ConvertAll(result.Data, w => $"0x{(ushort)w:X4}"))}");
                sb.AppendLine($"BIN: {string.Join(" ", Array.ConvertAll(result.Data, w => Convert.ToString(w, 2).PadLeft(16, '0')))}");
                AppendResult(sb.ToString());
            }
            else
            {
                AppendResult($"[READ ERROR] {result.ErrorMessage}");
            }
        }

        private async void OnWriteClick(object? sender, EventArgs e)
        {
            if (_plc == null) { ShowError("Not connected"); return; }
            if (!Enum.TryParse<DeviceType>(_cmbDevice.SelectedItem?.ToString(), out var dev))
                return;

            if (!short.TryParse(_txtWriteValue.Text, out short val))
            {
                ShowError("Invalid write value"); return;
            }

            int addr = (int)_nudAddress.Value;
            var cts = new CancellationTokenSource(3000);
            var result = await _plc.WriteWordsAsync(dev, addr, new[] { val }, cts.Token);

            AppendResult(result.Success
                ? $"[WRITE] {dev}{addr} = {val} — OK"
                : $"[WRITE ERROR] {result.ErrorMessage}");
        }

        private void OnStartMonitorClick(object? sender, EventArgs e)
        {
            if (_plc == null) { ShowError("Not connected first"); return; }

            _monitor?.Dispose();
            _monitor = new TagMonitorService(_plc, _logger);
            _monitor.TagValueChanged += OnTagValueChanged;

            var group = new MonitorGroup
            {
                GroupName = "Main",
                PollIntervalMs = 500,
                Tags = new System.Collections.Generic.List<PLCTag>(_tagRepo.GetAll())
            };
            _monitor.AddGroup(group);
            _monitor.StartMonitoring();
            _logger.Info("UI", "Tag monitoring started");
        }

        private void OnStopMonitorClick(object? sender, EventArgs e)
        {
            _monitor?.StopMonitoring();
            _logger.Info("UI", "Tag monitoring stopped");
        }

        private void OnTagValueChanged(object? sender, TagChangedEventArgs e)
        {
            // Cross-thread UI update
            if (_gridTags.InvokeRequired)
            {
                _gridTags.BeginInvoke(() => OnTagValueChanged(sender, e));
                return;
            }

            foreach (DataGridViewRow row in _gridTags.Rows)
            {
                if (row.Cells["TagName"].Value?.ToString() == e.Tag.TagName)
                {
                    row.Cells["RawValue"].Value = e.NewValue;
                    row.Cells["EngValue"].Value = e.Tag.EngineeringValue.ToString("F2");
                    row.Cells["LastUpdate"].Value = e.ChangedAt.ToString("HH:mm:ss.fff");
                    // Flash changed row
                    row.DefaultCellStyle.BackColor = Color.FromArgb(0, 60, 0);
                    break;
                }
            }
        }

        private void OnLogAdded(object? sender, LogEntry entry)
        {
            if (_lstLog.InvokeRequired)
            {
                _lstLog.BeginInvoke(() => OnLogAdded(sender, entry));
                return;
            }

            Color color = entry.Level switch
            {
                LogLevel.Error or LogLevel.Critical => Color.Red,
                LogLevel.Warning => Color.Yellow,
                LogLevel.Debug => Color.Gray,
                _ => Color.LightGray
            };

            // ListBox doesn't support per-item color natively, so we just add text
            _lstLog.Items.Insert(0, entry.ToString());
            if (_lstLog.Items.Count > 2000)
                _lstLog.Items.RemoveAt(_lstLog.Items.Count - 1);
        }

        // ================================================
        // UI Helpers
        // ================================================

        private void UpdateUIState(ConnectionState state)
        {
            if (_lblStatus.InvokeRequired)
            {
                _lblStatus.BeginInvoke(() => UpdateUIState(state));
                return;
            }

            (_lblStatus.Text, _lblStatus.ForeColor) = state switch
            {
                ConnectionState.Connected => ("● CONNECTED", Color.LimeGreen),
                ConnectionState.Connecting => ("◌ CONNECTING...", Color.Yellow),
                ConnectionState.Reconnecting => ("↺ RECONNECTING...", Color.Orange),
                ConnectionState.Error => ("✖ ERROR", Color.Red),
                _ => ("● DISCONNECTED", Color.Gray)
            };
        }

        private void RefreshTagGrid()
        {
            _gridTags.Rows.Clear();
            foreach (var tag in _tagRepo.GetAll())
                _gridTags.Rows.Add(tag.TagName, tag.Device, tag.Address,
                    tag.Value ?? "-", tag.EngineeringValue.ToString("F2"),
                    tag.Unit, tag.LastUpdated.ToString("HH:mm:ss.fff"), tag.Description);
        }

        private ConnectionConfig BuildConnectionConfig()
        {
            string brand = _cmbBrand.SelectedItem?.ToString() ?? "Mitsubishi";
            bool isTcp = _cmbCommType.SelectedItem?.ToString() == "TCP/IP";
            PLCBrand plcBrand = Enum.Parse<PLCBrand>(brand);

            if (isTcp)
                return new TcpConnectionConfig
                {
                    Name = $"{brand}-TCP",
                    Brand = plcBrand,
                    IpAddress = _txtHost.Text.Trim(),
                    Port = (int)_nudPort.Value,
                    TimeoutMs = 3000
                };
            else
                return new SerialConnectionConfig
                {
                    Name = $"{brand}-Serial",
                    Brand = plcBrand,
                    PortName = _cmbComPort.SelectedItem?.ToString() ?? "COM1",
                    BaudRate = (int)_nudBaud.Value
                };
        }

        private void AppendResult(string text)
        {
            if (_rtbResult.InvokeRequired)
            { _rtbResult.BeginInvoke(() => AppendResult(text)); return; }
            _rtbResult.AppendText(text + "\n");
            _rtbResult.ScrollToCaret();
        }

        private void ShowError(string msg)
        {
            if (InvokeRequired) { BeginInvoke(() => ShowError(msg)); return; }
            _logger.Error("UI", msg);
            MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SetupStatsTimer()
        {
            _statsTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _statsTimer.Tick += (s, e) =>
            {
                if (_plc == null) return;
                var st = _plc.GetStatistics();
                _lblStats.Text =
                    $"Req: {st.TotalRequests} | OK: {st.SuccessRequests} | " +
                    $"Fail: {st.FailedRequests} | Rate: {st.SuccessRate:F1}% | " +
                    $"Avg: {st.AverageResponseMs:F1}ms | " +
                    $"RX: {st.TotalBytesRead / 1024.0:F1}KB | TX: {st.TotalBytesWritten / 1024.0:F1}KB";
            };
            _statsTimer.Start();
        }

        // ================================================
        // WinForms Control Factory Helpers
        // ================================================
        private static Label AddLabel(Control parent, string text, int x, int y,
            Color? color = null)
        {
            var lbl = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = color ?? Color.LightGray,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9)
            };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private static ComboBox AddComboBox(Control parent, int x, int y, int w, string[] items)
        {
            var cb = new ComboBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            cb.Items.AddRange(items);
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
            parent.Controls.Add(cb);
            return cb;
        }

        private static TextBox AddTextBox(Control parent, int x, int y, int w, string text)
        {
            var tb = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 25),
                Text = text,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9)
            };
            parent.Controls.Add(tb);
            return tb;
        }

        private static NumericUpDown AddNumeric(Control parent, int x, int y, int w,
            decimal min, decimal max, decimal val)
        {
            var n = new NumericUpDown
            {
                Location = new Point(x, y),
                Size = new Size(w, 25),
                Minimum = min,
                Maximum = max,
                Value = val,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Font = new Font("Consolas", 9)
            };
            parent.Controls.Add(n);
            return n;
        }

        private static Button AddButton(Control parent, int x, int y, int w, int h,
            string text, Color backColor)
        {
            var btn = new Button
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                Text = text,
                BackColor = backColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            parent.Controls.Add(btn);
            return btn;
        }

        private static Panel CreatePanel(int x, int y, int w, int h, Color back)
        {
            return new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = back
            };
        }

        // ------------------------------------------------
        // Cleanup on close
        // ------------------------------------------------
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _monitor?.Dispose();
            _plc?.Dispose();
            _logger.Dispose();
            base.OnFormClosing(e);
        }
    }
}
