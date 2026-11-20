// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>
/// The processor's own temperature, Intel or AMD. A processor only gives it to code running
/// inside Windows' kernel, so it takes a driver: PC Butler uses PawnIO, a free, signed one
/// that the user installs from its own site (PC Butler never installs a driver), and reads
/// through the LibreHardwareMonitor library (MPL 2.0). Only the administrator helper calls
/// Read; without PawnIO, or without administrator rights, it returns null.
/// </summary>
public static class CpuSensor
{
    public const string DriverPage = "https://pawnio.eu";

    private static readonly object Gate = new();
    private static Computer? computer;
    private static bool failed;

    /// <summary>True when PawnIO is installed on this PC. Needs no special rights.</summary>
    public static bool DriverInstalled
    {
        get
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                return key is not null;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Which of a processor's temperature readings stands for the whole chip: the package
    /// (Intel), Tctl/Tdie (AMD), an average, and failing those the hottest core. Readings of
    /// "distance to the limit" are not temperatures and are left out.
    /// </summary>
    public static double? Choose(IEnumerable<(string Name, float? Value)> readings)
    {
        var real = readings.Where(reading => reading.Value is > 0 and < 150 && !reading.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var wanted in new[] { "Package", "Tctl/Tdie", "Tdie", "Tctl", "Average" })
        {
            var match = real.FirstOrDefault(reading => reading.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (match.Value is { } degrees)
                return Math.Round(degrees, 1);
        }
        return real.Count > 0 ? Math.Round(real.Max(reading => reading.Value!.Value), 1) : null;
    }

    /// <summary>The processor's temperature now, or null when it can't be read.</summary>
    public static double? Read()
    {
        lock (Gate)
        {
            if (failed || !DriverInstalled)
                return null;
            try
            {
                if (computer is null)
                {
                    computer = new Computer { IsCpuEnabled = true };
                    computer.Open();
                }
                var readings = new List<(string, float?)>();
                foreach (var hardware in computer.Hardware.Where(hardware => hardware.HardwareType == HardwareType.Cpu))
                {
                    hardware.Update();
                    readings.AddRange(hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature).Select(sensor => (sensor.Name, sensor.Value)));
                }
                return Choose(readings);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A library reaching into hardware can fail in ways of its own; the helper must go on without it.
                Log.Warning($"The processor's temperature could not be read: {error.Message}");
                failed = true;
                return null;
            }
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            try
            {
                computer?.Close();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
            }
            computer = null;
        }
    }
}
