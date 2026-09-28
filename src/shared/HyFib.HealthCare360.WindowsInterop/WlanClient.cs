using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HyFib.HealthCare360.WindowsInterop;

/// <summary>Security posture of a WiFi association, simplified for alerting.</summary>
public enum WifiSecurityLevel
{
    Unknown = 0,
    Open,
    Wep,
    Personal,
    Enterprise,
    Wpa3Personal,
    Wpa3Enterprise,
}

/// <summary>Snapshot of one wireless interface.</summary>
public sealed record WifiConnectionInfo(
    Guid InterfaceId,
    string InterfaceDescription,
    bool IsConnected,
    string? Ssid,
    string? Bssid,
    int SignalQuality,
    int? RssiDbm,
    double? ReceiveRateMbps,
    double? TransmitRateMbps,
    string? PhyType,
    string? AuthAlgorithm,
    string? CipherAlgorithm,
    WifiSecurityLevel Security,
    bool DetailsAvailable = true,
    string? Note = null)
{
    /// <summary>True for networks HC360 should not trust for clinical traffic (open, WEP).</summary>
    public bool IsInsecure => DetailsAvailable && Security is WifiSecurityLevel.Open or WifiSecurityLevel.Wep;

    public const string LocationPermissionNote =
        "Windows hides WiFi details from apps until Location access is allowed (Settings → Privacy & security → Location → Let desktop apps access your location).";
}

/// <summary>
/// Reads the current WiFi association via the Native WiFi API (wlanapi.dll). Works from a Windows
/// Service. On Windows 11 24H2+ the SSID/BSSID may be withheld unless location access is allowed for
/// desktop apps; signal and rates are still reported.
/// </summary>
public static class WlanClient
{
    private const uint ClientVersion = 2;
    private const uint ErrorSuccess = 0;

    /// <summary>Returns every wireless interface (empty when WiFi hardware/service is unavailable).</summary>
    public static IReadOnlyList<WifiConnectionInfo> GetInterfaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return QueryWindows();
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>The first connected interface, if any.</summary>
    public static WifiConnectionInfo? GetPrimaryConnection() =>
        GetInterfaces().FirstOrDefault(i => i.IsConnected) ?? GetInterfaces().FirstOrDefault();

    [SupportedOSPlatform("windows")]
    private static List<WifiConnectionInfo> QueryWindows()
    {
        var results = new List<WifiConnectionInfo>();
        if (NativeMethods.WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle) != ErrorSuccess)
        {
            return results;
        }

        try
        {
            if (NativeMethods.WlanEnumInterfaces(handle, IntPtr.Zero, out var listPtr) != ErrorSuccess)
            {
                return results;
            }

            try
            {
                var count = Marshal.ReadInt32(listPtr);
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
                var first = listPtr + 8; // dwNumberOfItems + dwIndex
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(first + (i * itemSize));
                    results.Add(QueryInterface(handle, info));
                }
            }
            finally
            {
                NativeMethods.WlanFreeMemory(listPtr);
            }
        }
        finally
        {
            NativeMethods.WlanCloseHandle(handle, IntPtr.Zero);
        }

        return results;
    }

    [SupportedOSPlatform("windows")]
    private static WifiConnectionInfo QueryInterface(IntPtr handle, WlanInterfaceInfo info)
    {
        var interfaceId = info.InterfaceGuid;
        var connected = info.State == WlanInterfaceState.Connected;
        if (!connected)
        {
            return new WifiConnectionInfo(interfaceId, info.Description, false, null, null, 0, null, null, null, null, null, null, WifiSecurityLevel.Unknown);
        }

        var status = NativeMethods.WlanQueryInterface(handle, ref interfaceId, WlanIntfOpcode.CurrentConnection, IntPtr.Zero, out _, out var dataPtr, out _);
        if (status != ErrorSuccess)
        {
            // Typically ERROR_ACCESS_DENIED (location privacy, Windows 11 24H2+). Fall back to the link speed
            // that the standard network stack still reports.
            var speed = LinkSpeedMbps(info.Description);
            return new WifiConnectionInfo(interfaceId, info.Description, true, null, null, 0, null, speed, speed, null, null, null,
                WifiSecurityLevel.Unknown, DetailsAvailable: false, Note: status == 5 ? WifiConnectionInfo.LocationPermissionNote : $"WLAN query failed ({status}).");
        }

        WlanConnectionAttributes attributes;
        try
        {
            attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(dataPtr);
        }
        finally
        {
            NativeMethods.WlanFreeMemory(dataPtr);
        }

        var association = attributes.Association;
        var ssid = association.Ssid.Length is > 0 and <= 32
            ? Encoding.UTF8.GetString(association.Ssid.Bytes, 0, (int)association.Ssid.Length)
            : null;
        var bssid = association.Bssid is { Length: 6 } mac && mac.Any(b => b != 0)
            ? string.Join(':', mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))
            : null;

        int? rssi = null;
        if (NativeMethods.WlanQueryInterface(handle, ref interfaceId, WlanIntfOpcode.Rssi, IntPtr.Zero, out var rssiSize, out var rssiPtr, out _) == ErrorSuccess)
        {
            try
            {
                if (rssiSize >= 4)
                {
                    rssi = Marshal.ReadInt32(rssiPtr);
                }
            }
            finally
            {
                NativeMethods.WlanFreeMemory(rssiPtr);
            }
        }

        var security = attributes.Security;
        return new WifiConnectionInfo(
            interfaceId,
            info.Description,
            true,
            ssid,
            bssid,
            (int)Math.Clamp(association.SignalQuality, 0u, 100u),
            rssi,
            association.RxRateKbps / 1000.0,
            association.TxRateKbps / 1000.0,
            association.PhyType.ToString(),
            security.AuthAlgorithm.ToString(),
            security.CipherAlgorithm.ToString(),
            Classify(security));
    }

    private static double? LinkSpeedMbps(string interfaceDescription)
    {
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                                 && string.Equals(n.Description, interfaceDescription, StringComparison.OrdinalIgnoreCase));
        return nic is { Speed: > 0 } ? nic.Speed / 1_000_000.0 : null;
    }

    private static WifiSecurityLevel Classify(WlanSecurityAttributes security)
    {
        if (!security.SecurityEnabled)
        {
            return WifiSecurityLevel.Open;
        }

        return security.AuthAlgorithm switch
        {
            Dot11AuthAlgorithm.Open when security.CipherAlgorithm is Dot11CipherAlgorithm.Wep40 or Dot11CipherAlgorithm.Wep104 or Dot11CipherAlgorithm.Wep => WifiSecurityLevel.Wep,
            Dot11AuthAlgorithm.Open => WifiSecurityLevel.Open,
            Dot11AuthAlgorithm.SharedKey => WifiSecurityLevel.Wep,
            Dot11AuthAlgorithm.Wpa or Dot11AuthAlgorithm.Rsna => WifiSecurityLevel.Enterprise,
            Dot11AuthAlgorithm.WpaPsk or Dot11AuthAlgorithm.RsnaPsk => WifiSecurityLevel.Personal,
            Dot11AuthAlgorithm.Wpa3Enterprise192 or Dot11AuthAlgorithm.Wpa3Enterprise => WifiSecurityLevel.Wpa3Enterprise,
            Dot11AuthAlgorithm.Wpa3Sae or Dot11AuthAlgorithm.Owe => WifiSecurityLevel.Wpa3Personal,
            _ => WifiSecurityLevel.Unknown,
        };
    }

    private static class NativeMethods
    {
        [DllImport("wlanapi.dll")]
        public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanQueryInterface(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            WlanIntfOpcode opCode,
            IntPtr pReserved,
            out uint pdwDataSize,
            out IntPtr ppData,
            out uint pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(IntPtr pMemory);
    }

    private enum WlanIntfOpcode : uint
    {
        CurrentConnection = 7,
        Rssi = 0x10000102,
    }

    private enum WlanInterfaceState : uint
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Discovering = 6,
        Authenticating = 7,
    }

    private enum Dot11PhyType : uint
    {
        Unknown = 0,
        Fhss = 1,
        Dsss = 2,
        IrBaseband = 3,
        Ofdm = 4,
        Hrdsss = 5,
        Erp = 6,
        Ht = 7,
        Vht = 8,
        Dmg = 9,
        He = 10,
        Eht = 11,
    }

    private enum Dot11AuthAlgorithm : uint
    {
        Open = 1,
        SharedKey = 2,
        Wpa = 3,
        WpaPsk = 4,
        WpaNone = 5,
        Rsna = 6,
        RsnaPsk = 7,
        Wpa3Enterprise192 = 8,
        Wpa3Sae = 9,
        Owe = 10,
        Wpa3Enterprise = 11,
    }

    private enum Dot11CipherAlgorithm : uint
    {
        None = 0x00,
        Wep40 = 0x01,
        Tkip = 0x02,
        Ccmp = 0x04,
        Wep104 = 0x05,
        Bip = 0x06,
        Gcmp = 0x08,
        Gcmp256 = 0x09,
        Ccmp256 = 0x0a,
        WpaUseGroup = 0x100,
        Wep = 0x101,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        public WlanInterfaceState State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid Ssid;
        public uint BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;

        public Dot11PhyType PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRateKbps;
        public uint TxRateKbps;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool SecurityEnabled;

        [MarshalAs(UnmanagedType.Bool)]
        public bool OneXEnabled;

        public Dot11AuthAlgorithm AuthAlgorithm;
        public Dot11CipherAlgorithm CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public WlanInterfaceState State;
        public uint ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public WlanAssociationAttributes Association;
        public WlanSecurityAttributes Security;
    }
}
