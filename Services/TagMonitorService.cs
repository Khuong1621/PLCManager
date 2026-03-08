
// ============================================================
// File: Services/TagMonitorService.cs
// Description: Multi-threaded tag polling with priority groups
// Patterns: Producer-Consumer, Observer, Thread Pool
// ============================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;

namespace PLCManager.Services
{
    public class TagMonitorService : ITagMonitor
    {
        // ------------------------------------------------
        // Events
        // ------------------------------------------------
        public event EventHandler<TagChangedEventArgs>? TagValueChanged;
        public event EventHandler<AlarmItem>? AlarmRaised;
        public event EventHandler<AlarmItem>? AlarmCleared;

        // ------------------------------------------------
        // Fields
        // ------------------------------------------------
        private readonly IPLCCommunication _plc;
        private readonly IAppLogger _logger;
        private readonly ConcurrentDictionary<string, MonitorGroup> _groups = new();
        private readonly ConcurrentDictionary<string, object?> _lastValues = new();
        private readonly List<AlarmItem> _activeAlarms = new();
        private CancellationTokenSource? _cts;
        private readonly List<Task> _pollingTasks = new();
        private bool _disposed;

        public bool IsMonitoring { get; private set; }

        public TagMonitorService(IPLCCommunication plc, IAppLogger logger)
        {
            _plc = plc;
            _logger = logger;
        }

        // ------------------------------------------------
        // Group Management
        // ------------------------------------------------
        public void AddGroup(MonitorGroup group)
        {
            _groups[group.GroupName] = group;
            _logger.Info("Monitor", $"Group added: {group.GroupName} ({group.Tags.Count} tags, {group.PollIntervalMs}ms)");
        }

        public void RemoveGroup(string groupName)
        {
            _groups.TryRemove(groupName, out _);
        }

        // ------------------------------------------------
        // Start / Stop Monitoring
        // ------------------------------------------------
        public void StartMonitoring()
        {
            if (IsMonitoring) return;

            _cts = new CancellationTokenSource();
            IsMonitoring = true;
            _logger.Info("Monitor", "Starting tag monitoring...");

            // Each group gets its own polling task (priority groups on dedicated threads)
            foreach (var group in _groups.Values.Where(g => g.IsActive))
            {
                var capturedGroup = group;
                var task = group.Priority > 0
                    ? Task.Factory.StartNew(
                        () => PollGroupAsync(capturedGroup, _cts.Token).GetAwaiter().GetResult(),
                        _cts.Token,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)
                    : Task.Run(
                        () => PollGroupAsync(capturedGroup, _cts.Token),
                        _cts.Token);
                _pollingTasks.Add(task);
            }

            _logger.Info("Monitor", $"Monitoring {_pollingTasks.Count} groups");
        }

        public void StopMonitoring()
        {
            if (!IsMonitoring) return;
            _cts?.Cancel();
            Task.WaitAll(_pollingTasks.ToArray(), TimeSpan.FromSeconds(5));
            _pollingTasks.Clear();
            IsMonitoring = false;
            _logger.Info("Monitor", "Monitoring stopped");
        }

        // ------------------------------------------------
        // Core Poll Loop (per group, async)
        // ------------------------------------------------
        private async Task PollGroupAsync(MonitorGroup group, CancellationToken ct)
        {
            _logger.Debug("Monitor", $"Poll task started: {group.GroupName}");
            var sw = new Stopwatch();

            while (!ct.IsCancellationRequested)
            {
                sw.Restart();
                try
                {
                    await PollGroupOnceAsync(group, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Error("Monitor", $"Poll error [{group.GroupName}]: {ex.Message}", ex);
                }

                sw.Stop();
                int delay = Math.Max(0, group.PollIntervalMs - (int)sw.ElapsedMilliseconds);
                if (delay > 0)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            _logger.Debug("Monitor", $"Poll task ended: {group.GroupName}");
        }

        private async Task PollGroupOnceAsync(MonitorGroup group, CancellationToken ct)
        {
            foreach (var tag in group.Tags)
            {
                if (ct.IsCancellationRequested) break;

                object? newValue = null;

                if (tag.IsBit)
                {
                    var result = await _plc.ReadBitsAsync(tag.Device, tag.Address, tag.Length, ct);
                    if (result.Success && result.Data != null)
                        newValue = result.Data.Length == 1 ? (object)result.Data[0] : result.Data;
                }
                else
                {
                    var result = await _plc.ReadWordsAsync(tag.Device, tag.Address, tag.Length, ct);
                    if (result.Success && result.Data != null)
                        newValue = result.Data.Length == 1 ? (object)result.Data[0] : result.Data;
                }

                if (newValue != null)
                {
                    _lastValues.TryGetValue(tag.TagName, out var oldValue);

                    if (!Equals(oldValue, newValue))
                    {
                        tag.Value = newValue;
                        tag.LastUpdated = DateTime.Now;
                        _lastValues[tag.TagName] = newValue;

                        // Fire on thread pool (don't block poll loop)
                        var args = new TagChangedEventArgs
                        {
                            Tag = tag,
                            OldValue = oldValue,
                            NewValue = newValue
                        };
                        _ = Task.Run(() => TagValueChanged?.Invoke(this, args), ct);
                    }
                }
            }
        }

        // ------------------------------------------------
        // IDisposable
        // ------------------------------------------------
        public void Dispose()
        {
            if (_disposed) return;
            StopMonitoring();
            _cts?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
