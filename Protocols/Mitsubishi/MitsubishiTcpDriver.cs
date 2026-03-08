// ============================================================
// File: Protocols/Mitsubishi/MitsubishiTcpDriver.cs
// Description: Mitsubishi MELSEC SLMP (3E frame) over TCP/IP
// Protocol: SLMP (Seamless Message Protocol) QnA compatible
// Reference: Mitsubishi MELSEC Communication Protocol Reference
// ============================================================

using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PLCManager.Communication.PLC;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;

namespace PLCManager.Protocols.Mitsubishi
{
    public class MitsubishiTcpDriver : PLCCommunicationBase
    {
        // ------------------------------------------------
        // Fields
        // ------------------------------------------------
        private readonly TcpConnectionConfig _config;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private ushort _serialNo = 0;

        // ------------------------------------------------
        // SLMP Frame Constants (3E Binary Frame)
        // ------------------------------------------------
        private const ushort SUBHEADER = 0x5000;      // 3E frame subheader
        private const byte RESERVED = 0x00;
        private const ushort CMD_READ_WORD = 0x0401;  // Batch read in word units
        private const ushort CMD_READ_BIT = 0x0401;   // same cmd, different sub
        private const ushort CMD_WRITE_WORD = 0x1401;
        private const ushort CMD_WRITE_BIT = 0x1401;
        private const ushort SUBCOMMAND_WORD = 0x0000;
        private const ushort SUBCOMMAND_BIT = 0x0001;

        // ------------------------------------------------
        // Constructor
        // ------------------------------------------------
        public MitsubishiTcpDriver(TcpConnectionConfig config, IAppLogger logger)
            : base(logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // ------------------------------------------------
        // Identity
        // ------------------------------------------------
        public override string DeviceName => _config.Name;
        public override PLCBrand Brand => PLCBrand.Mitsubishi;
        public override CommunicationType CommType => CommunicationType.TCPIP;

        // ------------------------------------------------
        // Connect / Disconnect
        // ------------------------------------------------
        protected override async Task<bool> ConnectCoreAsync(CancellationToken ct)
        {
            _tcpClient = new TcpClient
            {
                SendTimeout = _config.TimeoutMs,
                ReceiveTimeout = _config.TimeoutMs
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_config.TimeoutMs);

            await _tcpClient.ConnectAsync(_config.IpAddress, _config.Port, cts.Token);
            _stream = _tcpClient.GetStream();
            Logger.Info(DeviceName, $"TCP connected to {_config.IpAddress}:{_config.Port}");
            return true;
        }

        protected override async Task DisconnectCoreAsync()
        {
            _stream?.Close();
            _tcpClient?.Close();
            _stream = null;
            _tcpClient = null;
            await Task.CompletedTask;
        }

        // ------------------------------------------------
        // Read Words (SLMP Batch Read Word)
        // ------------------------------------------------
        protected override async Task<PLCResult<short[]>> ReadWordsCoreAsync(
            DeviceType device, int address, int count, CancellationToken ct)
        {
            try
            {
                // Build SLMP 3E request frame
                byte[] request = BuildReadWordFrame(device, address, count);

                await SendFrameAsync(request, ct);
                byte[] response = await ReceiveFrameAsync(ct);

                // Parse response
                if (!ValidateResponse(response, out string errMsg))
                    return PLCResult<short[]>.Fail(errMsg);

                // Extract data: response data starts at byte 11
                // (9 bytes header + 2 bytes end code)
                short[] words = new short[count];
                int dataOffset = 11;
                for (int i = 0; i < count; i++)
                {
                    if (dataOffset + 1 >= response.Length)
                        return PLCResult<short[]>.Fail("Response too short");
                    words[i] = BitConverter.ToInt16(response, dataOffset);
                    dataOffset += 2;
                }

                return PLCResult<short[]>.Ok(words);
            }
            catch (Exception ex) { return PLCResult<short[]>.Fail(ex.Message); }
        }

        // ------------------------------------------------
        // Read Bits (SLMP Batch Read Bit)
        // ------------------------------------------------
        protected override async Task<PLCResult<bool[]>> ReadBitsCoreAsync(
            DeviceType device, int address, int count, CancellationToken ct)
        {
            try
            {
                byte[] request = BuildReadBitFrame(device, address, count);
                await SendFrameAsync(request, ct);
                byte[] response = await ReceiveFrameAsync(ct);

                if (!ValidateResponse(response, out string errMsg))
                    return PLCResult<bool[]>.Fail(errMsg);

                // Bit values: 1 nibble per bit in ASCII-like encoding
                bool[] bits = new bool[count];
                int dataOffset = 11;
                for (int i = 0; i < count; i++)
                {
                    if (dataOffset >= response.Length) break;
                    bits[i] = (response[dataOffset] & 0x10) != 0 || response[dataOffset] == 0x01;
                    if (i % 2 == 0)
                        bits[i] = (response[dataOffset] & 0xF0) != 0;
                    else
                    {
                        bits[i] = (response[dataOffset] & 0x0F) != 0;
                        dataOffset++;
                    }
                }

                return PLCResult<bool[]>.Ok(bits);
            }
            catch (Exception ex) { return PLCResult<bool[]>.Fail(ex.Message); }
        }

        // ------------------------------------------------
        // Write Words
        // ------------------------------------------------
        protected override async Task<PLCResult<bool>> WriteWordsCoreAsync(
            DeviceType device, int address, short[] data, CancellationToken ct)
        {
            try
            {
                byte[] request = BuildWriteWordFrame(device, address, data);
                await SendFrameAsync(request, ct);
                byte[] response = await ReceiveFrameAsync(ct);

                if (!ValidateResponse(response, out string errMsg))
                    return PLCResult<bool>.Fail(errMsg);

                return PLCResult<bool>.Ok(true);
            }
            catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
        }

        // ------------------------------------------------
        // Write Bits
        // ------------------------------------------------
        protected override async Task<PLCResult<bool>> WriteBitsCoreAsync(
            DeviceType device, int address, bool[] data, CancellationToken ct)
        {
            try
            {
                byte[] request = BuildWriteBitFrame(device, address, data);
                await SendFrameAsync(request, ct);
                byte[] response = await ReceiveFrameAsync(ct);

                if (!ValidateResponse(response, out string errMsg))
                    return PLCResult<bool>.Fail(errMsg);

                return PLCResult<bool>.Ok(true);
            }
            catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
        }

        // ================================================
        // SLMP Frame Builders (3E Binary)
        // ================================================

        private byte[] BuildReadWordFrame(DeviceType device, int address, int count)
        {
            // Command data: 10 bytes
            // [CMD(2)][SUBCMD(2)][ADDR(3)][DEVCODE(1)][COUNT(2)]
            byte[] cmdData = new byte[10];
            var cmdSpan = cmdData.AsSpan();
            BitConverter.TryWriteBytes(cmdSpan[0..2], CMD_READ_WORD);
            BitConverter.TryWriteBytes(cmdSpan[2..4], SUBCOMMAND_WORD);
            // 3-byte little-endian address
            cmdData[4] = (byte)(address & 0xFF);
            cmdData[5] = (byte)((address >> 8) & 0xFF);
            cmdData[6] = (byte)((address >> 16) & 0xFF);
            cmdData[7] = GetDeviceCode(device);
            BitConverter.TryWriteBytes(cmdSpan[8..10], (ushort)count);

            return BuildSLMPFrame(cmdData);
        }

        private byte[] BuildReadBitFrame(DeviceType device, int address, int count)
        {
            byte[] cmdData = new byte[10];
            var cmdSpan = cmdData.AsSpan();
            BitConverter.TryWriteBytes(cmdSpan[0..2], CMD_READ_BIT);
            BitConverter.TryWriteBytes(cmdSpan[2..4], SUBCOMMAND_BIT);
            cmdData[4] = (byte)(address & 0xFF);
            cmdData[5] = (byte)((address >> 8) & 0xFF);
            cmdData[6] = (byte)((address >> 16) & 0xFF);
            cmdData[7] = GetDeviceCode(device);
            BitConverter.TryWriteBytes(cmdSpan[8..10], (ushort)count);
            return BuildSLMPFrame(cmdData);
        }

        private byte[] BuildWriteWordFrame(DeviceType device, int address, short[] data)
        {
            // cmd(2) + subcmd(2) + addr(3) + devcode(1) + count(2) + data(2*n)
            int payloadLen = 10 + data.Length * 2;
            byte[] cmdData = new byte[payloadLen];
            var span = cmdData.AsSpan();
            BitConverter.TryWriteBytes(span[0..2], CMD_WRITE_WORD);
            BitConverter.TryWriteBytes(span[2..4], SUBCOMMAND_WORD);
            cmdData[4] = (byte)(address & 0xFF);
            cmdData[5] = (byte)((address >> 8) & 0xFF);
            cmdData[6] = (byte)((address >> 16) & 0xFF);
            cmdData[7] = GetDeviceCode(device);
            BitConverter.TryWriteBytes(span[8..10], (ushort)data.Length);
            for (int i = 0; i < data.Length; i++)
                BitConverter.TryWriteBytes(span[(10 + i * 2)..], data[i]);
            return BuildSLMPFrame(cmdData);
        }

        private byte[] BuildWriteBitFrame(DeviceType device, int address, bool[] data)
        {
            int byteCount = (data.Length + 1) / 2;
            byte[] bitBytes = new byte[byteCount];
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i])
                    bitBytes[i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
            }

            int payloadLen = 10 + bitBytes.Length;
            byte[] cmdData = new byte[payloadLen];
            var span = cmdData.AsSpan();
            BitConverter.TryWriteBytes(span[0..2], CMD_WRITE_BIT);
            BitConverter.TryWriteBytes(span[2..4], SUBCOMMAND_BIT);
            cmdData[4] = (byte)(address & 0xFF);
            cmdData[5] = (byte)((address >> 8) & 0xFF);
            cmdData[6] = (byte)((address >> 16) & 0xFF);
            cmdData[7] = GetDeviceCode(device);
            BitConverter.TryWriteBytes(span[8..10], (ushort)data.Length);
            Array.Copy(bitBytes, 0, cmdData, 10, bitBytes.Length);
            return BuildSLMPFrame(cmdData);
        }

        /// <summary>Wrap command data in SLMP 3E binary header</summary>
        private byte[] BuildSLMPFrame(byte[] commandData)
        {
            // 3E Frame structure:
            // [Subheader:2][NetworkNo:1][PCNo:1][UnitIO:2][UnitNo:1][DataLen:2][Timer:2][CmdData:n]
            int totalLen = 9 + commandData.Length;
            byte[] frame = new byte[totalLen];
            var span = frame.AsSpan();

            BitConverter.TryWriteBytes(span[0..2], SUBHEADER);
            frame[2] = (byte)_config.NetworkNo;
            frame[3] = (byte)_config.PcNo;
            BitConverter.TryWriteBytes(span[4..6], (ushort)_config.UnitIoNo);
            frame[6] = (byte)_config.UnitNo;

            ushort dataLen = (ushort)(2 + commandData.Length); // timer(2) + cmd
            BitConverter.TryWriteBytes(span[7..9], dataLen);

            // Timer: 0x0010 = 250ms wait
            frame[9] = 0x10;
            frame[10] = 0x00;
            Array.Copy(commandData, 0, frame, 11, commandData.Length);

            _serialNo = (ushort)((_serialNo + 1) % 0xFFFF);
            return frame;
        }

        private static byte GetDeviceCode(DeviceType device) =>
            device switch
            {
                DeviceType.X => 0x9C,
                DeviceType.Y => 0x9D,
                DeviceType.M => 0x90,
                DeviceType.L => 0x92,
                DeviceType.F => 0x93,
                DeviceType.B => 0xA0,
                DeviceType.D => 0xA8,
                DeviceType.W => 0xB4,
                DeviceType.R => 0xAF,
                DeviceType.TN => 0xC2,
                DeviceType.CN => 0xC5,
                _ => 0xA8   // Default D
            };

        // ------------------------------------------------
        // Network I/O
        // ------------------------------------------------
        private async Task SendFrameAsync(byte[] frame, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");
            Logger.Debug(DeviceName, $"TX [{frame.Length}]: {ToHex(frame)}");
            await _stream.WriteAsync(frame, ct);
        }

        private async Task<byte[]> ReceiveFrameAsync(CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            byte[] buffer = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_config.TimeoutMs);

            int bytesRead = await _stream.ReadAsync(buffer, cts.Token);
            byte[] result = new byte[bytesRead];
            Array.Copy(buffer, result, bytesRead);
            Logger.Debug(DeviceName, $"RX [{bytesRead}]: {ToHex(result)}");
            return result;
        }

        private static bool ValidateResponse(byte[] resp, out string error)
        {
            error = "";
            if (resp == null || resp.Length < 11)
            { error = "Response too short"; return false; }

            // End code at bytes 9-10
            ushort endCode = BitConverter.ToUInt16(resp, 9);
            if (endCode != 0x0000)
            {
                error = $"PLC Error Code: 0x{endCode:X4} ({GetErrorDescription(endCode)})";
                return false;
            }
            return true;
        }

        private static string GetErrorDescription(ushort code) =>
            code switch
            {
                0x0055 => "Command not supported",
                0x00C0 => "No response from CPU",
                0x4000 => "Request data error",
                0xC051 => "Device address out of range",
                0xC056 => "Read/Write point count error",
                _ => "Unknown error"
            };

        private static string ToHex(byte[] data) =>
            BitConverter.ToString(data).Replace("-", " ");
    }
}
