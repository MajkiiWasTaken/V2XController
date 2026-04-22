using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/**********************************************************************************************************
 * V2X Controller - TestExtract.cs
 * Author: Michal Švrček
 * Version: 1.8.7 
 * Description: Provides helper methods for Modbus RTU communication, including CRC calculation, frame 
 *              validation, and exception code explanation. Designed for use in V2X Controller application 
 *              for handling Modbus RTU messages in real-time.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

public static class ModbusRtuHelpers
{
    public static ushort Crc16Modbus(byte[] data, int offset, int length)
    {
        const ushort poly = 0xA001;
        ushort crc = 0xFFFF;

        for (int i = 0; i < length; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++)
            {
                bool lsb = (crc & 0x0001) != 0;
                crc >>= 1;
                if (lsb) crc ^= poly;
            }
        }
        return crc;
    }

    public static string ToHex(byte[] bytes, int count)
    {
        var sb = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static async Task ReadExactAsync(SerialPort sp, byte[] buffer, int offset, int length, CancellationToken ct)
    {
        int read = 0;
        while (read < length)
        {
            int n = await sp.BaseStream.ReadAsync(buffer, offset + read, length - read, ct).ConfigureAwait(false);
            if (n <= 0) throw new TimeoutException("RTU read returned 0 bytes.");
            read += n;
        }
    }

    public static void ValidateCrcOrThrow(byte[] frame)
    {
        if (frame == null || frame.Length < 4) throw new InvalidOperationException("RTU frame too short.");

        int payloadLen = frame.Length - 2;
        ushort crc = Crc16Modbus(frame, 0, payloadLen);

        byte crcLo = (byte)(crc & 0xFF);
        byte crcHi = (byte)((crc >> 8) & 0xFF);

        byte gotLo = frame[frame.Length - 2];
        byte gotHi = frame[frame.Length - 1];

        if (crcLo != gotLo || crcHi != gotHi)
        {
            throw new InvalidOperationException(
                "Bad CRC. Got=" + gotLo.ToString("X2") + " " + gotHi.ToString("X2") +
                " Expected=" + crcLo.ToString("X2") + " " + crcHi.ToString("X2"));
        }
    }

    public static string ExplainExceptionCode(byte code)
    {
        // Modbus standard exceptions
        switch (code)
        {
            case 0x01: return "Illegal Function";
            case 0x02: return "Illegal Data Address";
            case 0x03: return "Illegal Data Value";
            case 0x04: return "Slave Device Failure";
            case 0x05: return "Acknowledge";
            case 0x06: return "Slave Device Busy";
            case 0x08: return "Memory Parity Error";
            case 0x0A: return "Gateway Path Unavailable";
            case 0x0B: return "Gateway Target Failed to Respond";
            default: return "Unknown (" + code.ToString("X2") + ")";
        }
    }
}
