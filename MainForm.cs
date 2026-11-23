using System.Diagnostics;
using System.IO.Ports;

namespace ModbusHmiDemo;

public sealed class MainForm : Form
{
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
    private readonly TextBox _host = new() { Text = "127.0.0.1", Width = 100 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 15020, Width = 90 };
    private readonly ComboBox _com = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly Button _btnConnect = new() { Text = "连接", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _btnDisconnect = new() { Text = "断开", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3), Enabled = false };
    private readonly Button _btnSim = new() { Text = "启动模拟器", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _btnExport = new() { Text = "导出 CSV", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Label _lblState = new() { AutoSize = true, Text = "未连接", ForeColor = Color.DimGray };

    private readonly DataGridView _grid = new();
    private readonly TrendPanel _trend = new();
    private readonly ToolStripStatusLabel _lblStat = new("等待连接");

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly SampleStore _store;
    private readonly SlaveSimulator _sim = new();

    private IModbusClient _client;
    private bool _busy;

    private readonly bool _autoDemo;

    public MainForm(bool autoDemo = false)
    {
        _autoDemo = autoDemo;
        Text = "冷库监控上位机 · Modbus 采集 Demo";

        // 屏幕小的时候按工作区收窄，避免窗口超出屏幕导致控件被挤没
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size(Math.Min(1180, wa.Width - 40), Math.Min(720, wa.Height - 40));
        MinimumSize = new Size(720, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("微软雅黑", 9F);

        _store = new SampleStore(Path.Combine(AppContext.BaseDirectory, "AppData", "hmi.db"));

        _mode.Items.AddRange(new object[] { "Modbus TCP", "Modbus RTU（串口）" });
        _mode.SelectedIndex = 0;
        _com.Items.AddRange(SerialPort.GetPortNames());
        if (_com.Items.Count > 0) _com.SelectedIndex = 0;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true
        };
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "接入方式", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _mode,
            new Label { Text = "IP", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, _host,
            new Label { Text = "端口", AutoSize = true, Padding = new Padding(6, 6, 0, 0) }, _port,
            new Label { Text = "串口", AutoSize = true, Padding = new Padding(6, 6, 0, 0) }, _com,
            _btnConnect, _btnDisconnect, _btnSim, _btnExport, _lblState
        });
        foreach (Control c in top.Controls) c.Margin = new Padding(2, 0, 2, 0);

        BuildGrid();

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 250F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _grid.Dock = DockStyle.Fill;
        _trend.Dock = DockStyle.Fill;
        body.Controls.Add(_grid, 0, 0);
        body.Controls.Add(_trend, 0, 1);

        var status = new StatusStrip();
        status.Items.Add(_lblStat);
        status.Items.Add(new ToolStripStatusLabel { Spring = true });
        status.Items.Add(new ToolStripStatusLabel($"数据库：{_store.DbPath}"));

        Controls.Add(body);
        Controls.Add(top);
        Controls.Add(status);

        Shown += (_, _) =>
        {
            if (_autoDemo)
            {
                _btnSim.PerformClick();
                _btnConnect.PerformClick();
            }
        };

        _btnConnect.Click += OnConnect;
        _btnDisconnect.Click += OnDisconnect;
        _btnSim.Click += OnToggleSim;
        _btnExport.Click += OnExport;
        _timer.Tick += OnTick;
        FormClosing += (_, _) => { _timer.Stop(); _client?.Dispose(); _sim.Dispose(); _store.Dispose(); };
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.Columns.Add("name", "点位名称");
        _grid.Columns.Add("addr", "协议地址");
        _grid.Columns.Add("value", "数值");
        _grid.Columns.Add("unit", "单位");
        _grid.Columns.Add("state", "状态");
        _grid.Columns["name"].FillWeight = 34;
        _grid.Columns["addr"].FillWeight = 16;
        _grid.Columns["value"].FillWeight = 20;
        _grid.Columns["unit"].FillWeight = 12;
        _grid.Columns["state"].FillWeight = 14;

        foreach (var p in DevicePoints.All)
            _grid.Rows.Add(p.Name, p.Address, "--", p.Unit, "");
    }

    private void OnConnect(object sender, EventArgs e)
    {
        try
        {
            _client?.Dispose();
            _client = _mode.SelectedIndex == 0
                ? new ModbusTcpClient(_host.Text.Trim(), (int)_port.Value)
                : new ModbusRtuClient(_com.Text, 9600);
            _client.Open();

            _timer.Start();
            _btnConnect.Enabled = false;
            _btnDisconnect.Enabled = true;
            _lblState.Text = "连接中…";
            _lblState.ForeColor = Color.Green;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "连接失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _client?.Dispose();
            _client = null;
        }
    }

    private void OnDisconnect(object sender, EventArgs e)
    {
        _timer.Stop();
        _client?.Dispose();
        _client = null;
        _btnConnect.Enabled = true;
        _btnDisconnect.Enabled = false;
        _lblState.Text = "已断开";
        _lblState.ForeColor = Color.DimGray;
        SetQuality("--", Color.Black, "");
    }

    private void OnToggleSim(object sender, EventArgs e)
    {
        if (_sim.Running)
        {
            _sim.Stop();
            _btnSim.Text = "启动模拟器";
            _lblStat.Text = "模拟器已停止";
        }
        else
        {
            try
            {
                _sim.Start();
                _btnSim.Text = "停止模拟器";
                _port.Value = _sim.Port;
                _lblStat.Text = $"模拟器已启动：127.0.0.1:{_sim.Port}（功能码 03，起始地址 0，6 个寄存器）";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动失败（端口可能被占用）：" + ex.Message, "错误");
            }
        }
    }

    private void OnExport(object sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"采集记录_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            int n = _store.ExportCsv(dlg.FileName);
            MessageBox.Show(this, $"已导出 {n} 条记录", "完成");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "错误");
        }
    }

    private async void OnTick(object sender, EventArgs e)
    {
        if (_client == null || _busy) return;
        _busy = true;
        var sw = Stopwatch.StartNew();
        var client = _client;                 // 快照：避免 await 期间被"断开"置 null
        try
        {
            var regs = await Task.Run(() => client.ReadHolding(1, 0, (ushort)DevicePoints.All.Length));
            var batch = new List<(string, double, string)>();

            for (int i = 0; i < DevicePoints.All.Length; i++)
            {
                var p = DevicePoints.All[i];
                double v = p.Decode(regs[i]);
                bool alarm = p.IsHighAlarm(v) || p.IsLowAlarm(v);
                _grid.Rows[i].Cells["value"].Value = p.Format(v);
                _grid.Rows[i].Cells["state"].Value = alarm ? "报警" : "正常";
                _grid.Rows[i].DefaultCellStyle.BackColor = alarm ? Color.MistyRose : Color.White;
                batch.Add((p.Name, v, alarm ? "Alarm" : "Good"));

                if (p.Name == "冷库温度") _trend.Push(v);
            }

            try { _store.InsertMany(batch); }
            catch (Exception dbEx) { _lblStat.Text = "写库失败：" + dbEx.Message; }

            _lblState.Text = $"已连接 · {client.Name}";
            _lblState.ForeColor = Color.Green;
            _lblStat.Text = $"采集耗时 {sw.ElapsedMilliseconds} ms　|　记录数 {_store.Count()}　|　质量 Good";
        }
        catch (Exception ex)
        {
            // 这一段以前在 catch 里裸奔：写库再失败就会炸出 async void，程序弹出异常框并卡住
            try
            {
                _store.InsertMany(new[] { ("通信状态", 0.0, "Bad") });
            }
            catch { }

            try
            {
                string friendly = ex is System.IO.IOException
                    ? "设备已断开（连接被对端关闭），请重新点连接"
                    : ex.Message;
                _lblState.Text = "通信异常：" + friendly;
                _lblState.ForeColor = Color.Firebrick;
                _lblStat.Text = $"采集失败 {sw.ElapsedMilliseconds} ms　|　请检查设备或模拟器";
                SetQuality("--", Color.Gainsboro, "Bad");
            }
            catch { }
        }
        finally
        {
            _busy = false;
        }
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        // 
        // MainForm
        // 
        ClientSize = new Size(284, 261);
        Name = "MainForm";
        ResumeLayout(false);

    }

    private void SetQuality(string value, Color back, string state)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells["value"].Value = value;
            row.Cells["state"].Value = state;
            row.DefaultCellStyle.BackColor = back;
        }
    }
}

/// <summary>用 GDI+ 画的实时趋势：比 Chart 控件轻，也更容易讲清"怎么把点连成线"。</summary>
public sealed class TrendPanel : Control
{
    private readonly List<double> _values = new();
    private readonly int _cap;
    private readonly double _limit;

    public TrendPanel(int cap = 120, double limit = -12.0)
    {
        _cap = cap;
        _limit = limit;
        DoubleBuffered = true;
        BackColor = Color.White;
    }

    public void Push(double v)
    {
        if (double.IsNaN(v)) return;
        _values.Add(v);
        if (_values.Count > _cap) _values.RemoveAt(0);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var area = new Rectangle(56, 34, Width - 76, Height - 76);
        using var titleFont = new Font("微软雅黑", 10F, FontStyle.Bold);
        using var font = new Font("微软雅黑", 8.5F);
        g.DrawString($"温度趋势（最近 {_cap} 秒）", titleFont, Brushes.Black, 12, 8);

        if (area.Width <= 10 || area.Height <= 10) return;

        double min = _limit - 4, max = _limit + 6;
        if (_values.Count > 1)
        {
            min = Math.Min(_values.Min(), _limit) - 2;
            max = Math.Max(_values.Max(), _limit) + 2;
        }

        using var axisPen = new Pen(Color.Gainsboro, 1);
        using var gridFont = new Font("微软雅黑", 8F);
        for (int i = 0; i <= 4; i++)
        {
            int y = area.Top + area.Height * i / 4;
            g.DrawLine(axisPen, area.Left, y, area.Right, y);
            double val = max - (max - min) * i / 4;
            g.DrawString(val.ToString("F1"), gridFont, Brushes.Gray, 8, y - 8);
        }

        using var limitPen = new Pen(Color.Firebrick, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        int yLimit = area.Top + (int)((max - _limit) / (max - min) * area.Height);
        g.DrawLine(limitPen, area.Left, yLimit, area.Right, yLimit);
        g.DrawString($"上限 {_limit:F1} ℃", gridFont, Brushes.Firebrick, area.Right - 72, yLimit - 16);

        if (_values.Count < 2)
        {
            g.DrawString("等待采集…", font, Brushes.Gray, area.Left + 8, area.Top + 8);
            return;
        }

        var pts = new PointF[_values.Count];
        double span = max - min == 0 ? 1 : max - min;
        for (int i = 0; i < _values.Count; i++)
        {
            float x = area.Left + area.Width * i / (float)(_cap - 1);
            float y = area.Top + (float)((max - _values[i]) / span * area.Height);
            pts[i] = new PointF(x, y);
        }

        using var linePen = new Pen(Color.RoyalBlue, 2);
        g.DrawLines(linePen, pts);

        double last = _values[^1];
        g.DrawString($"当前 {last:F1} ℃", new Font("微软雅黑", 9F, FontStyle.Bold),
            last > _limit ? Brushes.Firebrick : Brushes.RoyalBlue, area.Left, Height - 32);
    }
}
