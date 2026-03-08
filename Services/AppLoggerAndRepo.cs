// ============================================================
// File: Services/AppLogger.cs
// Description: Thread-safe logger – Singleton pattern
//              Writes to in-memory ring buffer + file
// ============================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;

namespace PLCManager.Services
{
    public class AppLogger : IAppLogger, IDisposable
    {
        // Singleton
        private static readonly Lazy<AppLogger> _instance = new(() => new AppLogger());
        public static AppLogger Instance => _instance.Value;

        private readonly ConcurrentQueue<LogEntry> _buffer = new();
        private const int MaxBufferSize = 10_000;
        private readonly BlockingCollection<LogEntry> _writeQueue = new(new ConcurrentQueue<LogEntry>(), 5000);
        private readonly Task _writerTask;
        private StreamWriter? _fileWriter;
        private readonly CancellationTokenSource _cts = new();

        public LogLevel MinLevel { get; set; } = LogLevel.Debug;
        public string LogFilePath { get; private set; } = "";

        // Events for UI binding
        public event EventHandler<LogEntry>? LogAdded;

        private AppLogger()
        {
            // Background file writer task
            _writerTask = Task.Factory.StartNew(
                BackgroundWrite,
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void SetLogFile(string path)
        {
            LogFilePath = path;
            _fileWriter?.Dispose();
            _fileWriter = new StreamWriter(path, append: true) { AutoFlush = true };
        }

        private void BackgroundWrite()
        {
            foreach (var entry in _writeQueue.GetConsumingEnumerable(_cts.Token))
            {
                try { _fileWriter?.WriteLine(entry.ToString()); }
                catch { /* ignore file write errors */ }
            }
        }

        public void Log(LogLevel level, string source, string message, Exception? ex = null)
        {
            if (level < MinLevel) return;

            var entry = new LogEntry
            {
                Level = level,
                Source = source,
                Message = ex != null ? $"{message} | {ex.Message}" : message,
                Exception = ex
            };

            // Ring buffer
            _buffer.Enqueue(entry);
            while (_buffer.Count > MaxBufferSize && _buffer.TryDequeue(out _)) { }

            // Async file write
            _writeQueue.TryAdd(entry);

            // UI notification (on thread pool)
            LogAdded?.Invoke(this, entry);

            // Also to debug output
            System.Diagnostics.Debug.WriteLine(entry.ToString());
        }

        public void Debug(string source, string msg) => Log(LogLevel.Debug, source, msg);
        public void Info(string source, string msg) => Log(LogLevel.Info, source, msg);
        public void Warning(string source, string msg) => Log(LogLevel.Warning, source, msg);
        public void Error(string source, string msg, Exception? ex = null) => Log(LogLevel.Error, source, msg, ex);
        public void Critical(string source, string msg, Exception? ex = null) => Log(LogLevel.Critical, source, msg, ex);

        public IEnumerable<LogEntry> GetRecentLogs(int count = 100) =>
            _buffer.TakeLast(count);

        public void Dispose()
        {
            _cts.Cancel();
            _writeQueue.CompleteAdding();
            _writerTask.Wait(TimeSpan.FromSeconds(2));
            _fileWriter?.Dispose();
            _cts.Dispose();
        }
    }
}
