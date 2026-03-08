// ============================================================
// File: Core/Enums/PLCEnums.cs
// Description: All Enumerations for PLC Communication System
// Author: Senior Dev Template
// ============================================================

namespace PLCManager.Core.Enums
{
    /// <summary>PLC Manufacturer / Brand</summary>
    public enum PLCBrand
    {
        Mitsubishi,
        Omron,
        Siemens,
        Allen_Bradley
    }

    /// <summary>Communication Channel Type</summary>
    public enum CommunicationType
    {
        TCPIP,      // Ethernet TCP/IP (e.g. MELSEC-E, FINS/TCP)
        RS232,      // Serial RS-232 (COM port)
        RS485,      // Serial RS-485 (COM port, multi-drop)
        USB         // USB (future)
    }

    /// <summary>Current connection state</summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Error,
        Timeout,
        Reconnecting
    }

    /// <summary>PLC data device types (Mitsubishi Q/iQ-R naming)</summary>
    public enum DeviceType
    {
        // Bit devices
        X,   // Input relay
        Y,   // Output relay
        M,   // Internal relay
        L,   // Latch relay
        F,   // Annunciator
        B,   // Link relay
        S,   // Step relay

        // Word devices
        D,   // Data register
        W,   // Link register
        R,   // File register
        ZR,  // Extended file register
        TN,  // Timer current value
        CN,  // Counter current value

        // Omron specific
        CIO, // Omron CIO area
        HR,  // Holding relay
        AR,  // Auxiliary relay
        DM   // Data memory
    }

    /// <summary>Log severity levels</summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
        Critical
    }

    /// <summary>Read/Write operation type</summary>
    public enum OperationType
    {
        Read,
        Write,
        Monitor,
        Subscribe
    }
}
