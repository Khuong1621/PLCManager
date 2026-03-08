// ============================================================
// File: Communication/PLC/PLCCommunicationBase.cs
// Description: Abstract base – Template Method pattern
//              All PLC drivers inherit from this.
// Patterns: Template Method, Observer (events), Strategy
// ============================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;

namespace PLCManager.Communication.PLC
{
    public abstract class PLCCommunicationBase : IPLCCommunication
    {
        // ------------------------------------------------
        // Fields
        // ------------------------------------------------
        private ConnectionState _state = ConnectionState.Disconnected;
        private readonly CommunicationStats _stats = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);  // serial access to bus
        private bool _disposed;

        protected readonly IAppLogger Logger;

        // ------------------------------------------------
        // Constructor
        // ------------------------------------------------
        protected PLCCommunicationBase(IAppLogger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // ------------------------------------------------
        // Properties (IPLCCommunication)
        // ------------------------------------------------
        public abstract string DeviceName { get; }
        public abstract PLCBrand Brand { get; }
        public abstract CommunicationType CommType { get; }

        public ConnectionState State
        {
            get => _state;
            protected set
            {
                if (_state == value) return;
                var old = _state;
                _state = value;
                Logger.Info(DeviceName, $"State: {old} → {value}");
                ConnectionStateChanged?.Invoke(this, value);
            }
        }

        // ------------------------------------------------
        // Events
        // ------------------------------------------------
        public event EventHandler<ConnectionState>? ConnectionStateChanged;
        public event EventHandler<string>? ErrorOccurred;

        // ------------------------------------------------
        // Template Method: Connect (shared retry logic)
        // ------------------------------------------------
        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            if (State == ConnectionState.Connected) return true;

            State = ConnectionState.Connecting;
            Logger.Info(DeviceName, $"Connecting via {CommType}...");

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    bool ok = await ConnectCoreAsync(ct);
                    if (ok)
                    {
                        State = ConnectionState.Connected;
                        _stats.ConnectedSince = DateTime.Now;
                        Logger.Info(DeviceName, $"Connected (attempt {attempt})");
                        return true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.Warning(DeviceName, $"Connect attempt {attempt} failed: {ex.Message}");
                    if (attempt < 3)
                        await Task.Delay(1000 * attempt, ct);
                }
            }

            State = ConnectionState.Error;
            RaiseError("Failed to connect after 3 attempts");
            return false;
        }

        public async Task DisconnectAsync()
        {
            if (State == ConnectionState.Disconnected) return;
            try
            {
                await DisconnectCoreAsync();
                Logger.Info(DeviceName, "Disconnected");
            }
            finally { State = ConnectionState.Disconnected; }
        }

        // ------------------------------------------------
        // Read Words (with timing, stats, lock)
        // ------------------------------------------------
        public async Task<PLCResult<short[]>> ReadWordsAsync(
            DeviceType device, int address, int count, CancellationToken ct = default)
        {
            if (State != ConnectionState.Connected)
                return PLCResult<short[]>.Fail("Not connected");

            var sw = Stopwatch.StartNew();
            await _sendLock.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await ReadWordsCoreAsync(device, address, count, ct);
                sw.Stop();
                UpdateStats(result.Success, count * 2, 0, sw.ElapsedMilliseconds);
                return result;
            }
            catch (OperationCanceledException) { return PLCResult<short[]>.Fail("Cancelled"); }
            catch (Exception ex)
            {
                RaiseError(ex.Message);
                return PLCResult<short[]>.Fail(ex.Message);
            }
            finally { _sendLock.Release(); }
        }

        public async Task<PLCResult<bool[]>> ReadBitsAsync(
            DeviceType device, int address, int count, CancellationToken ct = default)
        {
            if (State != ConnectionState.Connected)
                return PLCResult<bool[]>.Fail("Not connected");

            await _sendLock.WaitAsync(ct);
            try
            {
                return await ReadBitsCoreAsync(device, address, count, ct);
            }
            catch (Exception ex) { return PLCResult<bool[]>.Fail(ex.Message); }
            finally { _sendLock.Release(); }
        }

        public async Task<PLCResult<bool>> WriteWordsAsync(
            DeviceType device, int address, short[] data, CancellationToken ct = default)
        {
            if (State != ConnectionState.Connected)
                return PLCResult<bool>.Fail("Not connected");

            await _sendLock.WaitAsync(ct);
            try
            {
                return await WriteWordsCoreAsync(device, address, data, ct);
            }
            catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
            finally { _sendLock.Release(); }
        }

        public async Task<PLCResult<bool>> WriteBitsAsync(
            DeviceType device, int address, bool[] data, CancellationToken ct = default)
        {
            if (State != ConnectionState.Connected)
                return PLCResult<bool>.Fail("Not connected");

            await _sendLock.WaitAsync(ct);
            try
            {
                return await WriteBitsCoreAsync(device, address, data, ct);
            }
            catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
            finally { _sendLock.Release(); }
        }

        // ------------------------------------------------
        // Ping
        // ------------------------------------------------
        public virtual async Task<PLCResult<bool>> PingAsync(CancellationToken ct = default)
        {
            try
            {
                // Default: read 1 word from D0 to check comms
                var result = await ReadWordsAsync(DeviceType.D, 0, 1, ct);
                return PLCResult<bool>.Ok(result.Success);
            }
            catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
        }

        public CommunicationStats GetStatistics() => _stats;

        // ------------------------------------------------
        // Abstract hooks (implemented by subclasses)
        // ------------------------------------------------
        protected abstract Task<bool> ConnectCoreAsync(CancellationToken ct);
        protected abstract Task DisconnectCoreAsync();
        protected abstract Task<PLCResult<short[]>> ReadWordsCoreAsync(DeviceType device, int address, int count, CancellationToken ct);
        protected abstract Task<PLCResult<bool[]>> ReadBitsCoreAsync(DeviceType device, int address, int count, CancellationToken ct);
        protected abstract Task<PLCResult<bool>> WriteWordsCoreAsync(DeviceType device, int address, short[] data, CancellationToken ct);
        protected abstract Task<PLCResult<bool>> WriteBitsCoreAsync(DeviceType device, int address, bool[] data, CancellationToken ct);

        // ------------------------------------------------
        // Helpers
        // ------------------------------------------------
        protected void RaiseError(string message)
        {
            Logger.Error(DeviceName, message);
            ErrorOccurred?.Invoke(this, message);
        }

        private void UpdateStats(bool success, int bytesRead, int bytesWritten, long elapsedMs)
        {
            _stats.TotalRequests++; // Removed Interlocked.Increment here
            if (success)
                _stats.SuccessRequests++;
            else
                _stats.FailedRequests++;

            _stats.TotalBytesRead += bytesRead;
            _stats.TotalBytesWritten += bytesWritten;
            _stats.MinResponseMs = Math.Min(_stats.MinResponseMs, elapsedMs);
            _stats.MaxResponseMs = Math.Max(_stats.MaxResponseMs, elapsedMs);
            _stats.AverageResponseMs = (_stats.AverageResponseMs * (_stats.TotalRequests - 1) + elapsedMs)
                                       / _stats.TotalRequests;
        }

        // ------------------------------------------------
        // IDisposable
        // ------------------------------------------------
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                DisconnectAsync().GetAwaiter().GetResult();
                _sendLock.Dispose();
            }
            _disposed = true;
        }
    }
}
