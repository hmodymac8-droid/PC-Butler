// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>What the sensors helper last read.</summary>
public sealed class SensorReading
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    /// <summary>Degrees Celsius of the hottest thermal zone the PC reports, or null if it reports none.</summary>
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    /// <summary>Fan speeds in RPM, for the fans the PC reports (often none).</summary>
    [JsonPropertyName("fans")] public List<int> Fans { get; set; } = [];
}

/// <summary>
/// Temperature and fan speeds. Windows only hands these to a program running as
/// administrator, and PC Butler itself never does. So on request it starts a small helper,
/// a second copy of itself with administrator rights (Windows asks first), whose only job
/// is to read the sensors every few seconds into a file the main program reads. The helper
/// stops when the main program closes.
///
/// What a PC reports varies: many give one motherboard temperature, some none, and few
/// report fan speeds at all. Nothing is installed and no driver is loaded.
/// </summary>
public static class Sensors
{
    public static string ReadingFile => Path.Combine(AppInfo.DataDir, "sensors.json");
    private const int IntervalSeconds = 3;

    /// <summary>The newest reading, or null if the helper isn't running (its file is missing or stale).</summary>
    public static SensorReading? Latest()
    {
        try
        {
            var reading = JsonSerializer.Deserialize<SensorReading>(File.ReadAllText(ReadingFile));
            return reading is not null && (DateTime.Now - reading.Time).TotalSeconds < IntervalSeconds * 4 ? reading : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Start the helper with administrator rights. Windows shows its permission prompt;
    /// returns false if the user says no.
    /// </summary>
    public static bool StartHelper()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--sensors");
            start.ArgumentList.Add(ReadingFile);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the permission prompt was declined
        }
    }

    /// <summary>The helper's whole life: read, write, repeat until the main program is gone.</summary>
    public static void RunHelper(string file, int parentId)
    {
        while (ParentAlive(parentId))
        {
            var reading = Read();
            try
            {
                var temp = file + ".new";
                File.WriteAllText(temp, JsonSerializer.Serialize(reading));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            Thread.Sleep(TimeSpan.FromSeconds(IntervalSeconds));
        }
        try
        {
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool ParentAlive(int parentId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            return !parent.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>One reading from Windows' own instrumentation (WMI).</summary>
    public static SensorReading Read()
    {
        var reading = new SensorReading { Time = DateTime.Now };
        try
        {
            // ACPI thermal zones report tenths of a kelvin.
            using var zones = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var zone in zones.Get())
            {
                var celsius = Convert.ToDouble(zone["CurrentTemperature"]) / 10 - 273.15;
                if (celsius is > 0 and < 150 && (reading.Temperature is null || celsius > reading.Temperature))
                    reading.Temperature = Math.Round(celsius, 1);
            }
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException
                                          or System.Runtime.InteropServices.COMException)
        {
        }
        try
        {
            using var fans = new ManagementObjectSearcher("SELECT DesiredSpeed FROM Win32_Fan");
            foreach (var fan in fans.Get())
            {
                if (fan["DesiredSpeed"] is { } speed && Convert.ToInt32(speed) > 0)
                    reading.Fans.Add(Convert.ToInt32(speed));
            }
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException
                                          or System.Runtime.InteropServices.COMException)
        {
        }
        return reading;
    }
}
