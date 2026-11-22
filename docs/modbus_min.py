#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
手写 Modbus TCP 帧，不依赖任何库 —— 20 行看懂协议。

用法：
    python modbus_min.py                 # 连本机模拟器 127.0.0.1:15020
    python modbus_min.py 192.168.1.10 502

前提：先在 demo 里点「启动模拟器」（或直接跑 ModbusHmiDemo.exe --demo）。
"""

import socket
import struct
import sys

HOST, PORT = ("127.0.0.1", 15020)
if len(sys.argv) >= 3:
    HOST, PORT = sys.argv[1], int(sys.argv[2])

UNIT = 1          # 从站地址
START, COUNT = 0, 6   # 起始地址 0，读 6 个保持寄存器


def recv_exact(sock, n):
    """TCP 是字节流，一次 recv 可能只给半个帧，必须循环读满。"""
    buf = b""
    while len(buf) < n:
        part = sock.recv(n - len(buf))
        if not part:
            raise ConnectionError("连接被对端关闭")
        buf += part
    return buf


def main():
    with socket.create_connection((HOST, PORT), timeout=3) as sock:
        # 12 字节请求：事务号 协议号 长度 单元号 功能码 起始地址 数量
        req = struct.pack(">HHHBBHH", 1, 0, 6, UNIT, 0x03, START, COUNT)
        print("发送:", req.hex(" ").upper())
        sock.sendall(req)

        head = recv_exact(sock, 6)                 # 先读 6 字节帧头
        _, _, length = struct.unpack(">HHH", head)
        body = recv_exact(sock, length)            # 再按长度读满剩余部分
        frame = head + body
        print("接收:", frame.hex(" ").upper())

        fc = body[1]
        if fc & 0x80:                              # 最高位为 1 表示异常响应
            raise RuntimeError(f"设备返回异常码 0x{body[2]:02X}")

        nbytes = body[2]
        regs = struct.unpack(">" + "H" * (nbytes // 2), body[3:3 + nbytes])
        print("寄存器:", regs)

        # 温度是 INT16 × 0.1，需要按有符号解析
        raw = regs[0] - 65536 if regs[0] > 32767 else regs[0]
        print(f"冷库温度 = {raw * 0.1:.1f} ℃")
        print(f"吸气压力 = {regs[1] * 0.001:.3f} MPa")
        print(f"排气压力 = {regs[2] * 0.001:.3f} MPa")
        print(f"压缩机电流 = {regs[3] * 0.1:.1f} A")
        print(f"压缩机状态 = {regs[4]}, 冷凝风机 = {regs[5]}")


if __name__ == "__main__":
    main()
