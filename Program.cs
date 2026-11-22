namespace ModbusHmiDemo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            SelfCheck.Run();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Contains("--demo")));
    }
}

/// <summary>
/// 无界面自检：起一个本机从站模拟器，用真实的 TCP 客户端读一遍、写一遍，再落一条库。
/// 用来证明"协议实现"和"存储"是通的，不依赖 PLC 和屏幕。
/// 运行：dotnet run -- --selftest
/// </summary>
internal static class SelfCheck
{
    public static void Run()
    {
        var log = new List<string>();
        int code = 0;
        try
        {
            using var sim = new SlaveSimulator(15021);
            sim.Start();
            Thread.Sleep(400);

            using var client = new ModbusTcpClient("127.0.0.1", 15021);
            client.Open();

            var regs = client.ReadHolding(1, 0, 6);
            log.Add($"读出寄存器：{string.Join(", ", regs)}");

            client.WriteSingle(1, 4, 0);
            var after = client.ReadHolding(1, 0, 6);
            log.Add($"写入后 reg4 = {after[4]}");
            if (after[4] != 0) throw new Exception("写单个寄存器没有生效");

            var temp = DevicePoints.All[0].Decode(regs[0]);
            log.Add($"温度工程量 = {temp:F1} ℃");

            var db = Path.Combine(Path.GetTempPath(), "hmi_selftest.db");
            if (File.Exists(db)) File.Delete(db);
            using (var store = new SampleStore(db))
            {
                store.InsertMany(new[] { ("冷库温度", temp, "Good"), ("排气压力", 1.45, "Good") });
                log.Add($"数据库记录数 = {store.Count()}");
                var csv = Path.Combine(Path.GetTempPath(), "hmi_selftest.csv");
                store.ExportCsv(csv);
                log.Add($"CSV 行数 = {File.ReadAllLines(csv).Length - 1}");
            }

            log.Add("RESULT: PASS");
        }
        catch (Exception ex)
        {
            log.Add("RESULT: FAIL - " + ex);
            code = 1;
        }

        var outFile = Path.Combine(AppContext.BaseDirectory, "selftest.txt");
        File.WriteAllLines(outFile, log);
        Environment.ExitCode = code;
    }
}
