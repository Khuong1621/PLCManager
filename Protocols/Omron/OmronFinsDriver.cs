// ============================================================
// File: Protocols/Omron/OmronFinsDriver.cs
// Description: Omron FINS over TCP/IP (FINS/TCP)
// Protocol: OMRON FINS (Factory Interface Network Service)
// Reference: Omron W227-E1 FINS Commands Reference Manual
// ============================================================

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PLCManager.Communication.PLC;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;

namespace PLCManager.Protocols.Omron
{
    public class OmronFinsDriver : PLCCommunicationBase
    {
        // ------------------------------------------------
        // Fields
        // ------------------------------------------------
        private readonly TcpConnectionConfig _config;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private byte _sid = 0;  // Service ID (sequence)

        // ------------------------------------------------
        // FINS Constants
        // ------------------------------------------------
        // Memory area codes (Omron CS/CJ series)
        private const byte AREA_CIO_WORD = 0xB0;
        private const byte AREA_CIO_BIT = 0x30;
        private const byte AREA_DM_WORD = 0x82;
        private const byte AREA_HR_WORD = 0xB2;
        private const byte AREA_AR_WORD = 0xB3;

        // FINS/TCP header
        private const string FINS_HEADER = "46494E53"; // "FINS" ASCII
        private const uint FINS_TCP_CMD_NODE_ADDR = 0x00000000;

        public OmronFinsDriver(TcpConnectionConfig config, IAppLogger logger)
            : base(logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            // Omron default port is 9600
            if (_config.Port == 5006) _config.Port = 9600;
        }

        public override string DeviceName => _config.Name;
        public override PLCBrand Brand => PLCBrand.Omron;
        public override CommunicationType CommType => CommunicationType.TCPIP;

        // ------------------------------------------------
        // Connect: FINS/TCP requires a handshake
        // ------------------------------------------------
        protected override async Task<bool> ConnectCoreAsync(CancellationToken ct)
        {
            _tcpClient = new TcpClient
            {
                SendTimeout = _config.TimeoutMs,
                ReceiveTimeout = _config.TimeoutMs
            };

            await _tcpClient.ConnectAsync(_config.IpAddress, _config.Port, ct);
            _stream = _tcpClient.GetStream();

            // FINS/TCP Node Address handshake
            await PerformNodeHandshakeAsync(ct);

            Logger.Info(DeviceName, $"FINS/TCP connected to {_config.IpAddress}:{_config.Port}");
            return true;
        }

        private async Task PerformNodeHandshakeAsync(CancellationToken ct)
        {
            // Send: FINS header + Command=0x00000000 (Node address data send)
            byte[] handshake = new byte[20];
            // "FINS" magic
            handshake[0] = 0x46; handshake[1] = 0x49;
            handshake[2] = 0x4E; handshake[3] = 0x53;
            // Length: 12 bytes of body
            handshake[4] = 0; handshake[5] = 0; handshake[6] = 0; handshake[7] = 12;
            // Command: 0x00000000
            Array.Clear(handshake, 8, 4);
            // Error code: 0
            Array.Clear(handshake, 12, 4);
            // Client node: 0 (auto-assign)
            Array.Clear(handshake, 16, 4);

            await _stream!.WriteAsync(handshake, ct);
            byte[] resp = new byte[24];
            await _stream.ReadAsync(resp, ct);
            // resp[19] = server-assigned node address
            byte myNode = resp[19];
            Logger.Info(DeviceName, $"FINS handshake OK. My node: {myNode}");
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
        // Read Words: FINS Memory Area Read (0101)
        // ------------------------------------------------
        protected override async Task<PLCResult<short[]>> ReadWordsCoreAsync(
            DeviceType device, int address, int count, CancellationToken ct)
        {
            try
            {
                byte areaCode = GetAreaCode(device, false);
                byte[] finsData = BuildReadCommand(areaCode, address, 0, count);
                byte[] frame = WrapFinsFrame(finsData);

                await _stream!.WriteAsync(frame, ct);
                byte[] response = await ReadFinsResponseAsync(ct);

                if (!ValidateFinsResponse(response, out string err))
                    return PLCResult<short[]>.Fail(err);

                // Data starts at offset 30 (20 FINS/TCP hdr + 10 FINS hdr response)
                int dataOffset = 30;
                short[] words = new short[count];
                for (int i = 0; i < count; i++)
                {
                    if (dataOffset + 1 >= response.Length) break;
                    // Omron is Big-Endian
                    words[i] = (short)((response[dataOffset] << 8) | response[dataOffset + 1]);
                    dataOffset += 2;
                }
                return PLCResult<short[]>.Ok(words);
            }
            catch (Exception ex) { return PLCResult<short[]>.Fail(ex.Message); }
        }

        protected override async Task<PLCResult<bool[]>> ReadBitsCoreAsync(
            DeviceType device, int address, int count, CancellationToken ct)
        {
            try
            {
                byte areaCode = GetAreaCode(device, true);
                byte[] finsData = BuildReadCommand(areaCode, address, 0, count);
                byte[] frame = WrapFinsFrame(finsData);

                await _stream!.WriteAsync(frame, ct);
                byte[] response = await ReadFinsResponseAsync(ct);

                if (!ValidateFinsResponse(response, out string err))
                    return PLCResult<bool[]>.Fail(err);

                bool[] bits = new bool[count];
                int dataOffset = 30;
                for (int i = 0; i < count; i++)
                {
                    if (dataOffset >= response.Length) break;
                    bits[i] = (response[dataOffset] & 0x01) != 0;
                    dataOffset++;
                }
                return PLCResult<bool[]>.Ok(bits);
            }
            catch (Exception ex) { return PLCResult<bool[]>.Fail(ex.Message); }
        }

        protected override async Task<PLCResult<bool>> WriteWordsCoreAsync(
            DeviceType device, int address, short[] data, CancellationToken ct)
        {
            try
            {
                byte areaCode = GetAreaCode(device, false);
                byte[] writeData = new byte[data.Length * 2];
                for (int i = 0; i < data.Length; i++)
                {
                    // Big-endian
                    writeData[i * 2] = (byte)(data[i] >> 8);
                    writeData[i * 2 + 1] = (byte)(data[i] & 0xFF);
                }
                byte[] finsData = BuildWriteCommand(areaCode, address, 0, data.Length, writeData);
                byte[] frame = WrapFinsFrame(finsData);

                await _stream!.WriteAsync(frame, ct);
                byte[] response = await ReadFinsResponseAsync(ct);
                return ValidateFinsResponse(response, out string err)
                    ? PLCResult<bool>.Ok(true)
                    : PLCResult<bool>.Fail(err);
            }
            catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
        }

        protected override async Task<PLCResult<bool>> WriteBitsCoreAsync(
            DeviceType device, int address, bool[] data, CancellationToken ct)
        {
            try
            {
                byte areaCode = GetAreaCode(device, true);
                byte[] bitData = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                    bitData[i] = data[i] ? (byte)0x01 : (byte)0x00;

                byte[] finsData = BuildWriteCommand(areaCode, address, 0, data.Length, bitData);
                byte[] frame = WrapFinsFrame(finsData);

                await _stream!.WriteAsync(frame, ct);
                byte[] response = await ReadFinsResponseAsync(ct);
                return ValidateFinsResponse(response, out string err)
                    ? PLCResult<bool>.Ok(true)
                    : PLCResult<bool>.Fail(err);
            }
            catch (Exception ex) { return PLCResult<bool>.Fail(ex.Message); }
        }

        // ------------------------------------------------
        // FINS Frame Builders
        // ------------------------------------------------

        private byte[] BuildReadCommand(byte memArea, int address, byte bitPos, int count)
        {
            // MRC=01, SRC=01 (Memory Area Read)
            return new byte[]
            {
                0x01, 0x01,         // MRC, SRC
                memArea,
                (byte)(address >> 8), (byte)(address & 0xFF),
                bitPos,
                (byte)(count >> 8), (byte)(count & 0xFF)
            };
        }

        private byte[] BuildWriteCommand(byte memArea, int address, byte bitPos, int count, byte[] data)
        {
            // MRC=01, SRC=02 (Memory Area Write)
            byte[] header = new byte[]
            {
                0x01, 0x02,         // MRC, SRC
                memArea,
                (byte)(address >> 8), (byte)(address & 0xFF),
                bitPos,
                (byte)(count >> 8), (byte)(count & 0xFF)
            };
            byte[] result = new byte[header.Length + data.Length];
            Array.Copy(header, result, header.Length);
            Array.Copy(data, 0, result, header.Length, data.Length);
            return result;
        }

        private byte[] WrapFinsFrame(byte[] commandData)
        {
            // FINS Header (10 bytes)
            byte[] finsHeader = new byte[]
            {
                0x80,               // ICF: command, no response required=0
                0x00,               // RSV
                0x02,               // GCT: gateway count
                0x00,               // DNA: destination network
                0x01,               // DA1: destination node (PLC)
                0x00,               // DA2: destination unit (CPU)
                0x00,               // SNA: source network
                0x00,               // SA1: source node
                0x00,               // SA2: source unit
                _sid++              // SID: service ID
            };

            byte[] finsBody = new byte[finsHeader.Length + commandData.Length];
            Array.Copy(finsHeader, finsBody, finsHeader.Length);
            Array.Copy(commandData, 0, finsBody, finsHeader.Length, commandData.Length);

            // FINS/TCP wrapper
            byte[] tcpFrame = new byte[20 + finsBody.Length];
            // "FINS"
            tcpFrame[0] = 0x46; tcpFrame[1] = 0x49; tcpFrame[2] = 0x4E; tcpFrame[3] = 0x53;
            // Length (big-endian): body = 12 + finsBody
            int len = 12 + finsBody.Length;
            tcpFrame[4] = (byte)(len >> 24); tcpFrame[5] = (byte)(len >> 16);
            tcpFrame[6] = (byte)(len >> 8); tcpFrame[7] = (byte)(len & 0xFF);
            // Command: 0x00000002 (FINS data send)
            tcpFrame[8] = 0; tcpFrame[9] = 0; tcpFrame[10] = 0; tcpFrame[11] = 2;
            // Error code: 0
            tcpFrame[12] = 0; tcpFrame[13] = 0; tcpFrame[14] = 0; tcpFrame[15] = 0;
            // Client/Server node
            tcpFrame[16] = 0; tcpFrame[17] = 0; tcpFrame[18] = 0; tcpFrame[19] = 0;

            Array.Copy(finsBody, 0, tcpFrame, 20, finsBody.Length);
            return tcpFrame;
        }

        private async Task<byte[]> ReadFinsResponseAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_config.TimeoutMs);
            int n = await _stream!.ReadAsync(buffer, cts.Token);
            byte[] result = new byte[n];
            Array.Copy(buffer, result, n);
            return result;
        }

        private static bool ValidateFinsResponse(byte[] resp, out string error)
        {
            error = "";
            if (resp == null || resp.Length < 30)
            { error = "Response too short"; return false; }

            // FINS response: end code at bytes 28-29
            byte mres = resp[28];
            byte sres = resp[29];
            if (mres != 0 || sres != 0)
            {
                error = $"FINS Error: MRES=0x{mres:X2} SRES=0x{sres:X2} ({GetFinsError(mres, sres)})";
                return false;
            }
            return true;
        }

        private static string GetFinsError(byte mres, byte sres) =>
            (mres, sres) switch
            {
                (0x01, 0x01) => "Local node not in network",
                (0x02, 0x01) => "Destination node not in network",
                (0x03, 0x01) => "Unit hardware error",
                (0x04, 0x01) => "Command too long",
                (0x05, 0x01) => "Command too short",
                (0x05, 0x02) => "Area classification mismatch",
                (0x22, 0x01) => "Read not possible",
                (0x22, 0x02) => "Write not possible",
                _ => "Unknown FINS error"
            };

        private static byte GetAreaCode(DeviceType device, bool isBit) =>
            device switch
            {
                DeviceType.CIO => isBit ? AREA_CIO_BIT : AREA_CIO_WORD,
                DeviceType.DM => AREA_DM_WORD,
                DeviceType.HR => AREA_HR_WORD,
                DeviceType.AR => AREA_AR_WORD,
                _ => AREA_DM_WORD
            };
    }
}
