using System.Net;
using System.Net.Sockets;

namespace ModbusHmiDemo;

/// <summary>
/// 本机 Modbus TCP 从站模拟器：没有 PLC 也能演示"连设备—读数—写入"的完整链路。
/// 面试时这是加分项：说明你理解从站一侧要做什么。
/// </summary>
public sealed class SlaveSimulator : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ushort[] _regs = new ushort[6];
    private readonly System.Threading.Timer _ticker;
    private readonly List<TcpClient> _clients = new();
    private int _tick;

    public int Port { get; }
    public bool Running { get; private set; }

    public SlaveSimulator(int port = 15020)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _ticker = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    private void Tick()
    {
        int t = Interlocked.Increment(ref _tick);
        // 温度在 -18 ℃ 附近缓慢波动，偶尔越过 -12 ℃ 触发报警，方便演示
        double temp = -18.0 + 3.0 * Math.Sin(t / 20.0) + (t % 120 > 100 ? 6.0 : 0);
        short tempRaw = (short)Math.Round(temp / 0.1);
        _regs[0] = (ushort)tempRaw;
        _regs[1] = (ushort)Math.Round((0.12 + 0.005 * Math.Sin(t / 7.0)) / 0.001);
        _regs[2] = (ushort)Math.Round((1.45 + 0.02 * Math.Sin(t / 11.0)) / 0.001);
        _regs[3] = (ushort)Math.Round((12.3 + 0.4 * Math.Sin(t / 5.0)) / 0.1);
        _regs[4] = 1;
        _regs[5] = (ushort)((t / 15) % 2);
    }

    public void Start()
    {
        if (Running) return;
        Tick();                     // 先算一次，避免刚启动读到全 0
        try { _ticker.Change(1000, 1000); } catch (ObjectDisposedException) { }
        _listener.Start();
        Running = true;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (Running)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            lock (_clients) _clients.Add(client);
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var head = new byte[6];
            try
            {
                while (true)
                {
                    if (!ReadExact(stream, head)) return;
                    int len = (head[4] << 8) | head[5];
                    var body = new byte[len];
                    if (!ReadExact(stream, body)) return;
                    var resp = Handle(head, body);
                    if (resp == null) return;
                    stream.Write(resp, 0, resp.Length);
                }
            }
            catch { }
        }
    }

    private byte[] Handle(byte[] head, byte[] body)
    {
        byte unit = body[0];
        byte fc = body[1];

        if (fc == 0x03)
        {
            ushort start = (ushort)((body[2] << 8) | body[3]);
            ushort count = (ushort)((body[4] << 8) | body[5]);
            if (start + count > _regs.Length) return Exception(head, unit, fc, 0x02);

            var data = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                ushort v = _regs[start + i];
                data[i * 2] = (byte)(v >> 8);
                data[i * 2 + 1] = (byte)v;
            }
            return Frame(head, unit, fc, data);
        }

        if (fc == 0x06)
        {
            ushort addr = (ushort)((body[2] << 8) | body[3]);
            ushort val = (ushort)((body[4] << 8) | body[5]);
            if (addr < _regs.Length) _regs[addr] = val;
            return Echo(head, unit, fc, addr, val);      // fc 06 的回显没有字节数字段
        }

        return Exception(head, unit, fc, 0x01);
    }

    private static byte[] Frame(byte[] head, byte unit, byte fc, byte[] data)
    {
        int len = 3 + data.Length;
        var f = new byte[6 + len];
        f[0] = head[0]; f[1] = head[1];      // 回显事务号，客户端靠它配对
        f[4] = (byte)(len >> 8);
        f[5] = (byte)len;
        f[6] = unit;
        f[7] = fc;
        f[8] = (byte)data.Length;
        Array.Copy(data, 0, f, 9, data.Length);
        return f;
    }

    private static byte[] Echo(byte[] head, byte unit, byte fc, ushort addr, ushort val)
    {
        var f = new byte[12];
        f[0] = head[0]; f[1] = head[1];
        f[5] = 6;
        f[6] = unit;
        f[7] = fc;
        f[8] = (byte)(addr >> 8);
        f[9] = (byte)addr;
        f[10] = (byte)(val >> 8);
        f[11] = (byte)val;
        return f;
    }

    private static byte[] Exception(byte[] head, byte unit, byte fc, byte code)
    {
        var f = new byte[9];
        f[0] = head[0]; f[1] = head[1];
        f[5] = 3;
        f[6] = unit;
        f[7] = (byte)(fc | 0x80);
        f[8] = code;
        return f;
    }

    private static bool ReadExact(NetworkStream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = s.Read(buf, off, buf.Length - off);
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }

    public void Stop()
    {
        Running = false;
        try { _listener.Stop(); } catch { }
        try { _ticker.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }

        // 关键：停止监听不会自动断开已建立的连接，必须主动关掉
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                try { c.Close(); } catch { }
            }
            _clients.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        _ticker.Dispose();
    }
}
