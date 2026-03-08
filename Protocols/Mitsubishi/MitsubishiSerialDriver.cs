// ============================================================
// File: Protocols/Mitsubishi/MitsubishiSerialDriver.cs
// Description: Mitsubishi MC Protocol over RS-232 (1C/4C frame)
// Protocol: MC Protocol Format 1 (ASCII) - QnA compatible
// ============================================================

using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PLCManager.Communication.PLC;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;

namespace PLCManager.Protocols.Mitsubishi
{
    public class MitsubishiSerialDriver : PLCCommunicationBase
    {
        // ------------------------------------------------
        // Fields
        // ------------------------------------------------
        private readonly SerialConnectionConfig _config;
        private SerialPort? _port;
        private readonly object _portLock = new();

        // ------------------------------------------------
        // MC Protocol ASCII constants
        // ------------------------------------------------
        private const char ENQ = '\x05';   // Enquiry (start of message)
        private const char STX = '\x02';   // Start of text
        private const char ETX = '\x03';   // End of text
        private const char EOT = '\x04';   // End of transmission
        private const char ACK = '\x06';   // Acknowledge
        private const char NAK = '\x15';   // Negative acknowledge

        public MitsubishiSerialDriver(SerialConnectionConfig config, IAppLogger logger)
            : base(logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public override string DeviceName => _config.Name;
        public override PLCBrand Brand => PLCBrand.Mitsubishi;
        public override CommunicationType CommType => CommunicationType.RS232;

        // ------------------------------------------------
        // Connect
        // ------------------------------------------------
        protected override Task<bool> ConnectCoreAsync(CancellationToken ct)
        {
            _port = new SerialPort(
                _config.PortName,
                _config.BaudRate,
                _config.Parity,
                _config.DataBits,
                _config.StopBits)
            {
                ReadTimeout = _config.TimeoutMs,
                WriteTimeout = _config.TimeoutMs,
                Encoding = Encoding.ASCII
            };

            _port.Open();
            Logger.Info(DeviceName, $"Serial opened: {_config.PortName} @ {_config.BaudRate} baud");
            return Task.FromResult(true);
        }

        protected override Task DisconnectCoreAsync()
        {
            lock (_portLock)
            {
                if (_port?.IsOpen == true) _port.Close();
                _port?.Dispose();
                _port = null;
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------
        // Read Words via MC Protocol ASCII 1C frame
        // Format: ENQ | PC# | CPU Monitor | CMD | Subcommand | Data | BCC | CRLFhh
        // ------------------------------------------------
        protected override async Task<PLCResult<short[]>> ReadWordsCoreAsync(
            DeviceType device, int address, int count, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Command: "0401" (Batch Read Word), Subcommand: "0000" (Word)
                    string cmd = BuildMCReadCommand("0401", "0000", device, address, count);
                    string response = SendReceiveSerial(cmd);

                    if (!ParseMCResponse(response, out string data, out string err))
                        return PLCResult<short[]>.Fail(err);

                    short[] words = new short[count];
                    for (int i = 0; i < count; i++)
                    {
                        string hexWord = data.Substring(i * 4, 4);
                        words[i] = (short)Convert.ToUInt16(hexWord, 16);
                    }
                    return PLCResult<short[]>.Ok(words);
                }
                catch (Exception ex) { return PLCResult<short[]>.Fail(ex.Message); }
            }, ct);
        }

        protected override async Task<PLCResult<bool[]>> ReadBitsCoreAsync(
            DeviceType device, int address, int count, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Subcommand "0001" = bit units
                    string cmd = BuildMCReadCommand("0401", "0001", device, address, count);
                    string response = SendReceiveSerial(cmd);

                    if (!ParseMCResponse(response, out string data, out string err))
                        return PLCResult<bool[]>.Fail(err);

                    bool[] bits = new bool[count];
                    for (int i = 0; i < count; i++)
                        bits[i] = data[i] != '0';

                    return PLCResult<bool[]>.Ok(bits);
                }
                catch (Exception ex) { return PLCResult<bool[]>.Fail(ex.Message); }
            }, ct);
        }

        protected override async Task<PLCResult<bool>> WriteWordsCoreAsync(
            DeviceType device, int address, short[] data, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var w in data) sb.Append(((ushort)w).ToString("X4"));
                    string cmd = BuildMCWriteCommand("1401", "0000", device, address, data.Length, sb.ToString());
                    string response = SendReceiveSerial(cmd);

                    return ParseMCResponse(response, out _, out string err)
                        ? PLCResult<bool>.Ok(true)
                        : PLCResult<bool>.Fail(err);
                }
                catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
            }, ct);
        }

        protected override async Task<PLCResult<bool>> WriteBitsCoreAsync(
            DeviceType device, int address, bool[] data, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var b in data) sb.Append(b ? '1' : '0');
                    string cmd = BuildMCWriteCommand("1401", "0001", device, address, data.Length, sb.ToString());
                    string response = SendReceiveSerial(cmd);

                    return ParseMCResponse(response, out _, out string err)
                        ? PLCResult<bool>.Ok(true)
                        : PLCResult<bool>.Fail(err);
                }
                catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
            }, ct);
        }

        // ------------------------------------------------
        // MC Protocol Frame Builders (ASCII, 1C/4C format)
        // ------------------------------------------------

        private string BuildMCReadCommand(string cmd, string subcmd, DeviceType device, int address, int count)
        {
            // PC# + Monitor timer + Command + Subcommand + Head device + DeviceCode + Count
            string pcNo = _config.PcNo.ToString("X2");
            string networkNo = _config.NetworkNo.ToString("X2");
            string timer = "0010"; // 250ms
            string devAddr = address.ToString("X6");
            string devCode = GetDeviceCodeAscii(device);
            string pointCount = count.ToString("X4");

            string dataBlock = timer + cmd + subcmd + devAddr + devCode + pointCount;
            string frame = networkNo + pcNo + "03FF" + "00" + dataBlock;
            byte bcc = CalcBCC(frame);

            return $"{STX}{frame}{ETX}{bcc:X2}";
        }

        private string BuildMCWriteCommand(string cmd, string subcmd,
            DeviceType device, int address, int count, string hexData)
        {
            string pcNo = _config.PcNo.ToString("X2");
            string networkNo = _config.NetworkNo.ToString("X2");
            string timer = "0010";
            string devAddr = address.ToString("X6");
            string devCode = GetDeviceCodeAscii(device);
            string pointCount = count.ToString("X4");

            string dataBlock = timer + cmd + subcmd + devAddr + devCode + pointCount + hexData;
            string frame = networkNo + pcNo + "03FF" + "00" + dataBlock;
            byte bcc = CalcBCC(frame);

            return $"{STX}{frame}{ETX}{bcc:X2}";
        }

        // ------------------------------------------------
        // Serial Send/Receive
        // ------------------------------------------------
        private string SendReceiveSerial(string command)
        {
            lock (_portLock)
            {
                if (_port == null || !_port.IsOpen)
                    throw new InvalidOperationException("Serial port not open");

                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                Logger.Debug(DeviceName, $"TX: {command}");
                _port.Write(command);

                // Read until CR/LF or timeout
                var sb = new StringBuilder();
                DateTime deadline = DateTime.Now.AddMilliseconds(_config.TimeoutMs);
                while (DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        char c = (char)_port.ReadByte();
                        sb.Append(c);
                        // Frame ends with BCC (2 chars) after ETX
                        string s = sb.ToString();
                        if (s.Contains(ETX) && s.Length >= s.IndexOf(ETX) + 3)
                            break;
                    }
                    else Thread.Sleep(5);
                }

                string response = sb.ToString();
                Logger.Debug(DeviceName, $"RX: {response}");
                return response;
            }
        }

        // ------------------------------------------------
        // Parse MC Protocol response
        // ------------------------------------------------
        private static bool ParseMCResponse(string response, out string data, out string error)
        {
            data = "";
            error = "";

            if (string.IsNullOrEmpty(response))
            { error = "Empty response"; return false; }

            int stxIdx = response.IndexOf(STX);
            int etxIdx = response.IndexOf(ETX);

            if (stxIdx < 0 || etxIdx < 0 || etxIdx <= stxIdx)
            { error = "Invalid frame structure"; return false; }

            string payload = response.Substring(stxIdx + 1, etxIdx - stxIdx - 1);

            // End code is at offset 8 (after NetworkNo+PcNo+UnitIo+UnitNo)
            if (payload.Length < 12) { error = "Frame too short"; return false; }

            string endCodeHex = payload.Substring(8, 4);
            if (endCodeHex != "0000")
            {
                error = $"PLC End Code: {endCodeHex}";
                return false;
            }

            data = payload.Substring(12); // remaining = read data
            return true;
        }

        // ------------------------------------------------
        // Utilities
        // ------------------------------------------------
        private static byte CalcBCC(string text)
        {
            byte bcc = 0;
            foreach (char c in text) bcc += (byte)c;
            return bcc;
        }

        private static string GetDeviceCodeAscii(DeviceType device) =>
            device switch
            {
                DeviceType.X => "X*",
                DeviceType.Y => "Y*",
                DeviceType.M => "M*",
                DeviceType.L => "L*",
                DeviceType.F => "F*",
                DeviceType.B => "B*",
                DeviceType.D => "D*",
                DeviceType.W => "W*",
                DeviceType.R => "R*",
                DeviceType.TN => "TN",
                DeviceType.CN => "CN",
                _ => "D*"
            };
    }
}
