// ============================================================
// File: Services/PLCConnectionFactory.cs
// Description: Factory Pattern – creates correct driver by config
// ============================================================

using System;
using PLCManager.Core.Enums;
using PLCManager.Core.Interfaces;
using PLCManager.Core.Models;
using PLCManager.Protocols.Mitsubishi;
using PLCManager.Protocols.Omron;

namespace PLCManager.Services
{
    public class PLCConnectionFactory : IPLCConnectionFactory
    {
        private readonly IAppLogger _logger;

        public PLCConnectionFactory(IAppLogger logger)
        {
            _logger = logger;
        }

        public IPLCCommunication Create(ConnectionConfig config)
        {
            return config switch
            {
                TcpConnectionConfig tcp when tcp.Brand == PLCBrand.Mitsubishi
                    => new MitsubishiTcpDriver(tcp, _logger),

                SerialConnectionConfig serial when serial.Brand == PLCBrand.Mitsubishi
                    => new MitsubishiSerialDriver(serial, _logger),

                TcpConnectionConfig tcp when tcp.Brand == PLCBrand.Omron
                    => new OmronFinsDriver(tcp, _logger),

                _ => throw new NotSupportedException(
                    $"No driver for {config.Brand} / {config.Type}")
            };
        }
    }
}

