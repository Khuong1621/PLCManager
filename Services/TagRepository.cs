

// ============================================================
// File: Services/TagRepository.cs
// Description: JSON-based tag configuration storage
// ============================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;

namespace PLCManager.Services
{
    public class TagRepository : ITagRepository
    {
        private readonly ConcurrentDictionary<string, PLCTag> _tags = new();
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public IEnumerable<PLCTag> GetAll() => _tags.Values;

        public PLCTag? GetByName(string name) =>
            _tags.TryGetValue(name, out var tag) ? tag : null;

        public void Save(PLCTag tag) => _tags[tag.TagName] = tag;

        public void Delete(string name) => _tags.TryRemove(name, out _);

        public void LoadFromFile(string path)
        {
            if (!File.Exists(path)) return;
            string json = File.ReadAllText(path);
            var tags = JsonSerializer.Deserialize<List<PLCTag>>(json, _jsonOpts);
            _tags.Clear();
            if (tags != null)
                foreach (var t in tags)
                    _tags[t.TagName] = t;
        }

        public void SaveToFile(string path)
        {
            string json = JsonSerializer.Serialize(_tags.Values, _jsonOpts);
            File.WriteAllText(path, json);
        }

        /// <summary>Create default demo tags for testing</summary>
        public void LoadDefaults()
        {
            var defaults = new[]
            {
                new PLCTag { TagName = "Machine_Speed",  Device = DeviceType.D, Address = 100, Description = "Motor speed (rpm)", Unit = "rpm", Scale = 0.1 },
                new PLCTag { TagName = "Temperature",    Device = DeviceType.D, Address = 200, Description = "Process temperature", Unit = "°C", Scale = 0.1 },
                new PLCTag { TagName = "Pressure",       Device = DeviceType.D, Address = 201, Description = "Line pressure", Unit = "bar", Scale = 0.01 },
                new PLCTag { TagName = "Counter_Parts",  Device = DeviceType.D, Address = 300, Description = "Parts count today" },
                new PLCTag { TagName = "Run_Signal",     Device = DeviceType.M, Address = 0,   IsBit = true, Description = "Machine running" },
                new PLCTag { TagName = "Alarm_E-Stop",  Device = DeviceType.M, Address = 100, IsBit = true, Description = "E-Stop activated" },
                new PLCTag { TagName = "Output_Valve1", Device = DeviceType.Y, Address = 0,   IsBit = true, Description = "Valve 1 output" },
            };
            foreach (var t in defaults) Save(t);
        }
    }
}
