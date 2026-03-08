// ============================================================
// File: Core/Models/PLCModels.cs
// Description: Data Transfer Objects & Domain Models
// ============================================================

using System;
using System.Collections.Generic;
using PLCManager.Core.Enums;

namespace PLCManager.Core.Models
{
    // --------------------------------------------------------
    // Connection Configuration
    // --------------------------------------------------------

    /// <summary>Base connection settings shared by all channel types</summary>
    public abstract class ConnectionConfig
    {
        public string Name { get; set; } = "Default";
        public CommunicationType Type { get; protected set; }
        public PLCBrand Brand { get; set; } = PLCBrand.Mitsubishi;
        public int TimeoutMs { get; set; } = 3000;
        public int RetryCount { get; set; } = 3;
        public bool AutoReconnect { get; set; } = true;
        public int ReconnectIntervalMs { get; set; } = 5000;
    }

    /// <summary>Ethernet TCP/IP connection settings</summary>
    public class TcpConnectionConfig : ConnectionConfig
    {
        public TcpConnectionConfig() => Type = CommunicationType.TCPIP;
        public string IpAddress { get; set; } = "192.168.1.1";
        public int Port { get; set; } = 5006;  // Mitsubishi default
        public int NetworkNo { get; set; } = 0;
        public int PcNo { get; set; } = 255;
        public int UnitIoNo { get; set; } = 0x03FF;
        public int UnitNo { get; set; } = 0;
    }

    /// <summary>RS-232 / RS-485 Serial connection settings</summary>
    public class SerialConnectionConfig : ConnectionConfig
    {
        public SerialConnectionConfig() => Type = CommunicationType.RS232;
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.Even;
        public int DataBits { get; set; } = 7;
        public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
        public int StationNo { get; set; } = 0;      // PLC station number
        public int NetworkNo { get; set; } = 0;
        public int PcNo { get; set; } // Added the missing property

    }

    // --------------------------------------------------------
    // PLC Device / Tag
    // --------------------------------------------------------

    /// <summary>Represents one PLC tag (device address)</summary>
    public class PLCTag
    {
        public string TagName { get; set; } = "";
        public DeviceType Device { get; set; } = DeviceType.D;
        public int Address { get; set; } = 0;
        public int Length { get; set; } = 1;          // word count
        public bool IsBit { get; set; } = false;
        public object? Value { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public string Description { get; set; } = "";
        public string Unit { get; set; } = "";
        public double Scale { get; set; } = 1.0;
        public double Offset { get; set; } = 0.0;

        /// <summary>Scaled engineering value</summary>
        public double EngineeringValue
        {
            get
            {
                if (Value is int i) return i * Scale + Offset;
                if (Value is short s) return s * Scale + Offset;
                if (Value is ushort us) return us * Scale + Offset;
                if (Value is bool b) return b ? 1.0 : 0.0;
                return 0;
            }
        }

        public override string ToString() =>
            $"{Device}{Address} [{TagName}] = {Value} ({LastUpdated:HH:mm:ss.fff})";
    }

    // --------------------------------------------------------
    // Operation Result
    // --------------------------------------------------------

    /// <summary>Generic result wrapper for all PLC operations</summary>
    public class PLCResult<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public string ErrorMessage { get; set; } = "";
        public int ErrorCode { get; set; } = 0;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public long ElapsedMs { get; set; }

        public static PLCResult<T> Ok(T data, long elapsedMs = 0) =>
            new() { Success = true, Data = data, ElapsedMs = elapsedMs };

        public static PLCResult<T> Fail(string msg, int code = -1) =>
            new() { Success = false, ErrorMessage = msg, ErrorCode = code };
    }

    // --------------------------------------------------------
    // Monitor / Polling
    // --------------------------------------------------------

    /// <summary>Defines a polling group for periodic tag reads</summary>
    public class MonitorGroup
    {
        public string GroupName { get; set; } = "";
        public List<PLCTag> Tags { get; set; } = new();
        public int PollIntervalMs { get; set; } = 500;
        public bool IsActive { get; set; } = true;
        public int Priority { get; set; } = 0;   // 0 = normal, higher = more priority
    }

    // --------------------------------------------------------
    // Log Entry
    // --------------------------------------------------------

    public class LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public LogLevel Level { get; set; }
        public string Source { get; set; } = "";
        public string Message { get; set; } = "";
        public Exception? Exception { get; set; }

        public override string ToString() =>
            $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level,-8}] [{Source}] {Message}";
    }

    // --------------------------------------------------------
    // Alarm
    // --------------------------------------------------------

    public class AlarmItem
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public PLCTag? SourceTag { get; set; }
        public DateTime OccurredAt { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public DateTime? ClearedAt { get; set; }
        public bool IsActive => ClearedAt == null;
        public bool IsAcknowledged => AcknowledgedAt != null;
    }

    // --------------------------------------------------------
    // Statistics
    // --------------------------------------------------------

    public class CommunicationStats
    {
        public long TotalRequests { get; set; }
        public long SuccessRequests { get; set; }
        public long FailedRequests { get; set; }
        public long TotalBytesRead { get; set; }
        public long TotalBytesWritten { get; set; }
        public double AverageResponseMs { get; set; }
        public double MinResponseMs { get; set; } = double.MaxValue;
        public double MaxResponseMs { get; set; }
        public DateTime ConnectedSince { get; set; }
        public double SuccessRate => TotalRequests > 0
            ? (double)SuccessRequests / TotalRequests * 100 : 0;
    }
}
