using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace FTX1Control.Models;

[SupportedOSPlatform("windows")]
public static class ComPortScanner
{
    private static readonly Regex PortNamePattern = new(@"\bCOM\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<ComPortOption> Scan()
    {
        var availablePortNames = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var ports = new Dictionary<string, ComPortOption>(StringComparer.OrdinalIgnoreCase);

        using var usbDevices = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbDevices is null)
        {
            return Array.Empty<ComPortOption>();
        }

        foreach (var vendorKeyName in usbDevices.GetSubKeyNames())
        {
            if (!vendorKeyName.StartsWith("VID_10C4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var vendorKey = usbDevices.OpenSubKey(vendorKeyName);
            if (vendorKey is null)
            {
                continue;
            }

            foreach (var deviceKeyName in vendorKey.GetSubKeyNames())
            {
                using var deviceKey = vendorKey.OpenSubKey(deviceKeyName);
                if (deviceKey is null)
                {
                    continue;
                }

                var friendlyName = deviceKey.GetValue("FriendlyName") as string;
                var deviceDescription = deviceKey.GetValue("DeviceDesc") as string;
                var deviceName = friendlyName ?? deviceDescription ?? string.Empty;
                var portName = GetPortName(deviceKey, deviceName);
                if (portName is null || !availablePortNames.Contains(portName))
                {
                    continue;
                }

                var searchableName = $"{friendlyName} {deviceDescription}";
                var isSiliconLabs = searchableName.Contains("Silicon Labs", StringComparison.OrdinalIgnoreCase);
                var isEnhanced = searchableName.Contains("Enhanced", StringComparison.OrdinalIgnoreCase);
                var isStandard = searchableName.Contains("Standard", StringComparison.OrdinalIgnoreCase);
                if (!isSiliconLabs || (!isEnhanced && !isStandard))
                {
                    continue;
                }

                var displayName = GetDisplayName(deviceName, portName);
                ports[portName] = new ComPortOption(portName, displayName, isStandard && !isEnhanced);
            }
        }

        return new List<ComPortOption>(ports.Values);
    }

    private static string? GetPortName(RegistryKey deviceKey, string deviceName)
    {
        using var deviceParameters = deviceKey.OpenSubKey("Device Parameters");
        var portName = deviceParameters?.GetValue("PortName") as string
            ?? deviceKey.GetValue("PortName") as string;
        if (!string.IsNullOrWhiteSpace(portName))
        {
            return portName;
        }

        var match = PortNamePattern.Match(deviceName);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string GetDisplayName(string deviceName, string portName)
    {
        var separatorIndex = deviceName.LastIndexOf(';');
        var displayName = separatorIndex >= 0
            ? deviceName[(separatorIndex + 1)..].Trim()
            : deviceName.Trim();
        return PortNamePattern.IsMatch(displayName) ? displayName : $"{displayName} ({portName})";
    }
}
