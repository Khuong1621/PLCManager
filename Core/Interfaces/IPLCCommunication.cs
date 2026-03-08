// ============================================================
// File: Core/Interfaces/IPLCCommunication.cs
// Description: SOLID Interface contracts for PLC communication
// Pattern: Interface Segregation, Dependency Inversion
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PLCManager.Core.Enums;
using PLCManager.Core.Models;

namespace PLCManager.Core.Interfaces
{
    // --------------------------------------------------------
    // Primary communication interface (all PLCs implement this)
    // --------------------------------------------------------

    public interface IPLCCommunication : IDisposable
    {
        string DeviceName { get; }
        PLCBrand Brand { get; }
        CommunicationType CommType { get; }
        ConnectionState State { get; }

        // Events
        event EventHandler<ConnectionState> ConnectionStateChanged;
        event EventHandler<string> ErrorOccurred;

        // Lifecycle
        Task<bool> ConnectAsync(CancellationToken ct = default);
        Task DisconnectAsync();

        // Read
        Task<PLCResult<short[]>> ReadWordsAsync(DeviceType device, int address, int count, CancellationToken ct = default);
        Task<PLCResult<bool[]>> ReadBitsAsync(DeviceType device, int address, int count, CancellationToken ct = default);

        // Write
        Task<PLCResult<bool>> WriteWordsAsync(DeviceType device, int address, short[] data, CancellationToken ct = default);
        Task<PLCResult<bool>> WriteBitsAsync(DeviceType device, int address, bool[] data, CancellationToken ct = default);

        // Diagnostics
        Task<PLCResult<bool>> PingAsync(CancellationToken ct = default);
        CommunicationStats GetStatistics();
    }

    // --------------------------------------------------------
    // Extended: batch / random read (optional capability)
    // --------------------------------------------------------

    public interface IBatchReadable
    {
        Task<PLCResult<Dictionary<PLCTag, object>>> ReadBatchAsync(
            IEnumerable<PLCTag> tags, CancellationToken ct = default);
    }

    // --------------------------------------------------------
    // Monitoring / Subscription interface
    // --------------------------------------------------------

    public interface ITagMonitor : IDisposable
    {
        event EventHandler<TagChangedEventArgs> TagValueChanged;
        event EventHandler<AlarmItem> AlarmRaised;
        event EventHandler<AlarmItem> AlarmCleared;

        void AddGroup(MonitorGroup group);
        void RemoveGroup(string groupName);
        void StartMonitoring();
        void StopMonitoring();
        bool IsMonitoring { get; }
    }

    public class TagChangedEventArgs : EventArgs
    {
        public PLCTag Tag { get; set; } = null!;
        public object? OldValue { get; set; }
        public object? NewValue { get; set; }
        public DateTime ChangedAt { get; set; } = DateTime.Now;
    }

    // --------------------------------------------------------
    // Logger interface (for DI & testability)
    // --------------------------------------------------------

    public interface IAppLogger
    {
        void Log(LogLevel level, string source, string message, Exception? ex = null);
        void Debug(string source, string msg);
        void Info(string source, string msg);
        void Warning(string source, string msg);
        void Error(string source, string msg, Exception? ex = null);
        void Critical(string source, string msg, Exception? ex = null);
        IEnumerable<LogEntry> GetRecentLogs(int count = 100);
    }

    // --------------------------------------------------------
    // Protocol-level interface (used internally by adapters)
    // --------------------------------------------------------

    public interface IProtocolFrame
    {
        byte[] Serialize();
        bool Validate(byte[] responseFrame);
        string ToHexString(byte[] data);
    }

    // --------------------------------------------------------
    // Factory pattern for creating PLC connections
    // --------------------------------------------------------

    public interface IPLCConnectionFactory
    {
        IPLCCommunication Create(ConnectionConfig config);
    }

    // --------------------------------------------------------
    // Repository pattern for tag configuration
    // --------------------------------------------------------

    public interface ITagRepository
    {
        IEnumerable<PLCTag> GetAll();
        PLCTag? GetByName(string name);
        void Save(PLCTag tag);
        void Delete(string name);
        void LoadFromFile(string path);
        void SaveToFile(string path);
    }
}
