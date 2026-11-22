using System.IO.Ports;
using System.Net.Sockets;

namespace ModbusHmiDemo;

/// <summary>上位机与设备之间的通信抽象：TCP 和串口实现同一套接口。</summary>
public interface IModbusClient : IDisposable
{
    string Name { get; }
    void Open();
    ushort[] ReadHolding(byte unit, ushort start, ushort count);
    void WriteSingle(byte unit, ushort address, ushort value);
}

/// <summary>Modbus RTU 的 CRC16（多项式 0xA001，初值 0xFFFF，低字节在前）。</summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}

/// <summary>Modbus TCP：MBAP 头（7 字节）+ PDU。没有 CRC，靠帧头里的长度字段分包。</summary>
public sealed class ModbusTcpClient : IModbusClient
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient _tcp;
    private NetworkStream _stream;
    private ushort _tid;

    public ModbusTcpClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public string Name => $"TCP {_host}:{_port}";

    public void Open()
    {
        Close();
        _tcp = new TcpClient { NoDelay = true };
        if (!_tcp.ConnectAsync(_host, _port).Wait(3000))
            throw new IOException($"连接 {_host}:{_port} 超时");
        _stream = _tcp.GetStream();
        _stream.ReadTimeout = 2000;
        _stream.WriteTimeout = 2000;
    }

    public ushort[] ReadHolding(byte unit, ushort start, ushort count)
    {
        if (_stream == null) throw new IOException("尚未连接");

        var req = new byte[12];
        ushort tid = ++_tid;
        req[0] = (byte)(tid >> 8); req[1] = (byte)tid;   // 事务号
        req[2] = 0; req[3] = 0;                          // 协议号固定 0
        req[4] = 0; req[5] = 6;                          // 后续字节数
        req[6] = unit;
        req[7] = 0x03;                                   // 读保持寄存器
        req[8] = (byte)(start >> 8); req[9] = (byte)start;
        req[10] = (byte)(count >> 8); req[11] = (byte)count;
        _stream.Write(req, 0, req.Length);

        var head = ReadExact(6);
        int len = (head[4] << 8) | head[5];
        var body = ReadExact(len);
        if ((body[1] & 0x80) != 0)
            throw new IOException($"Modbus 异常码 0x{body[2]:X2}");

        int n = body[2] / 2;
        var regs = new ushort[n];
        for (int i = 0; i < n; i++)
            regs[i] = (ushort)((body[3 + i * 2] << 8) | body[4 + i * 2]);  // 大端
        return regs;
    }

    public void WriteSingle(byte unit, ushort address, ushort value)
    {
        if (_stream == null) throw new IOException("尚未连接");

        var req = new byte[12];
        ushort tid = ++_tid;
        req[0] = (byte)(tid >> 8); req[1] = (byte)tid;
        req[4] = 0; req[5] = 6;
        req[6] = unit;
        req[7] = 0x06;                                   // 写单个寄存器
        req[8] = (byte)(address >> 8); req[9] = (byte)address;
        req[10] = (byte)(value >> 8); req[11] = (byte)value;
        _stream.Write(req, 0, req.Length);
        var head = ReadExact(6);
        int len = (head[4] << 8) | head[5];
        var body = ReadExact(len);                       // fc 06 的回显没有字节数字段，按长度读
        if ((body[1] & 0x80) != 0)
            throw new IOException($"Modbus 异常码 0x{body[2]:X2}");
    }

    private byte[] ReadExact(int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = _stream.Read(buf, off, n - off);
            if (r <= 0) throw new IOException("连接被对端关闭");
            off += r;
        }
        return buf;
    }

    public void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}

/// <summary>Modbus RTU：串口帧没有长度字段，靠 CRC 校验和帧间隔区分，所以读的时候要自己攒够字节。</summary>
public sealed class ModbusRtuClient : IModbusClient
{
    private readonly string _portName;
    private readonly int _baud;
    private SerialPort _sp;

    public ModbusRtuClient(string portName, int baud = 9600)
    {
        _portName = portName;
        _baud = baud;
    }

    public string Name => $"RTU {_portName}@{_baud}";

    public void Open()
    {
        Close();
        _sp = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1500,
            WriteTimeout = 1500
        };
        _sp.Open();
    }

    public ushort[] ReadHolding(byte unit, ushort start, ushort count)
    {
        if (_sp == null || !_sp.IsOpen) throw new IOException("串口未打开");

        var req = new byte[8];
        req[0] = unit;
        req[1] = 0x03;
        req[2] = (byte)(start >> 8); req[3] = (byte)start;
        req[4] = (byte)(count >> 8); req[5] = (byte)count;
        ushort crc = Crc16.Compute(req.AsSpan(0, 6));
        req[6] = (byte)(crc & 0xFF);
        req[7] = (byte)(crc >> 8);

        _sp.DiscardInBuffer();
        _sp.Write(req, 0, req.Length);

        int fc = _sp.ReadByte();
        if ((fc & 0x80) != 0)
        {
            int code = _sp.ReadByte();
            _sp.ReadByte();
            throw new IOException($"Modbus 异常码 0x{code:X2}");
        }

        int byteCount = _sp.ReadByte();
        var rest = new byte[byteCount + 2];              // 数据 + 2 字节 CRC
        int off = 0;
        while (off < rest.Length)
        {
            int r = _sp.Read(rest, off, rest.Length - off);
            if (r <= 0) throw new IOException("串口读取超时");
            off += r;
        }

        var frame = new byte[3 + rest.Length];
        frame[0] = unit;
        frame[1] = (byte)fc;
        frame[2] = (byte)byteCount;
        Array.Copy(rest, 0, frame, 3, rest.Length);

        ushort calc = Crc16.Compute(frame.AsSpan(0, frame.Length - 2));
        ushort recv = (ushort)(frame[^2] | (frame[^1] << 8));
        if (calc != recv) throw new IOException("CRC 校验失败");

        int n = byteCount / 2;
        var regs = new ushort[n];
        for (int i = 0; i < n; i++)
            regs[i] = (ushort)((frame[3 + i * 2] << 8) | frame[4 + i * 2]);
        return regs;
    }

    public void WriteSingle(byte unit, ushort address, ushort value)
    {
        if (_sp == null || !_sp.IsOpen) throw new IOException("串口未打开");

        var req = new byte[8];
        req[0] = unit;
        req[1] = 0x06;
        req[2] = (byte)(address >> 8); req[3] = (byte)address;
        req[4] = (byte)(value >> 8); req[5] = (byte)value;
        ushort crc = Crc16.Compute(req.AsSpan(0, 6));
        req[6] = (byte)(crc & 0xFF);
        req[7] = (byte)(crc >> 8);
        _sp.Write(req, 0, req.Length);
    }

    public void Close()
    {
        if (_sp != null)
        {
            try { if (_sp.IsOpen) _sp.Close(); } catch { }
            _sp.Dispose();
            _sp = null;
        }
    }

    public void Dispose() => Close();
}
