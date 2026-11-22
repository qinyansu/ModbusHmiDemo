namespace ModbusHmiDemo;

/// <summary>
/// 点表：上位机里最核心的一张配置表——协议地址、工程量换算、报警阈值都在这里。
/// 真实项目里这张表通常来自 Excel / 数据库，代码里不要写死。
/// </summary>
public sealed class PointDef
{
    public string Name { get; init; } = "";
    public ushort Address { get; init; }
    public string Unit { get; init; } = "";
    public double Scale { get; init; } = 1.0;      // 工程量 = 原始值 × Scale
    public bool Signed { get; init; }               // 是否按 INT16 解析（能读负数）
    public double HighLimit { get; init; } = double.MaxValue;
    public double LowLimit { get; init; } = double.MinValue;

    public double Decode(ushort raw)
    {
        double v = Signed ? (short)raw : raw;
        return v * Scale;
    }

    public string Format(double v) => Scale < 0.01 ? v.ToString("F3") : (Scale < 1 ? v.ToString("F1") : v.ToString("F0"));

    public bool IsHighAlarm(double v) => v > HighLimit;
    public bool IsLowAlarm(double v) => v < LowLimit;
}

public static class DevicePoints
{
    /// <summary>演示点表：功能码 03，起始地址 0，共 6 个寄存器，大端。</summary>
    public static readonly PointDef[] All =
    {
        new() { Name = "冷库温度",   Address = 0, Unit = "℃",   Scale = 0.1,   Signed = true,  HighLimit = -12.0 },
        new() { Name = "吸气压力",   Address = 1, Unit = "MPa", Scale = 0.001, Signed = false },
        new() { Name = "排气压力",   Address = 2, Unit = "MPa", Scale = 0.001, Signed = false, HighLimit = 1.80 },
        new() { Name = "压缩机电流", Address = 3, Unit = "A",   Scale = 0.1,   Signed = false },
        new() { Name = "压缩机状态", Address = 4, Unit = "",    Scale = 1,     Signed = false },
        new() { Name = "冷凝风机",   Address = 5, Unit = "",    Scale = 1,     Signed = false },
    };
}
