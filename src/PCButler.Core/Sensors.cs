// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
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
    /// <summary>Degrees Celsius of the processor itself, Intel or AMD. Only with the PawnIO driver installed (see CpuSensor).</summary>
    [JsonPropertyName("cpu_temperature")] public double? CpuTemperature { get; set; }
    /// <summary>Fan speeds in RPM, for the fans the PC reports (often none).</summary>
    [JsonPropertyName("fans")] public List<int> Fans { get; set; } = [];
}

/// <summary>
/// Temperature and fan speeds. Windows only hands these to a program running as
/// administrator, and PC Butler itself never does. So on request it starts a small helper,
/// a second copy of itself with administrator rights (Windows asks first), whose only job
/// is to read the sensors every few seconds into shared memory the main program reads
/// (see LiveSlot; nothing goes to the disk). The helper stops when the main program closes.
///
/// What a PC reports varies: many give one motherboard temperature, some none, and few
/// report fan speeds at all. Nothing is installed and no driver is loaded.
/// </summary>
public static class Sensors
{
    public static string Slot => LiveSlot.Name("sensors");
    private const int IntervalSeconds = 3;

    /// <summary>The newest reading, or null if the helper isn't running (nothing written yet, or stale).</summary>
    public static SensorReading? Latest(string? slot = null)
    {
        try
        {
            var reading = LiveSlot.Read(slot ?? Slot) is { } text ? JsonSerializer.Deserialize<SensorReading>(text) : null;
            return reading is not null && (DateTime.Now - reading.Time).TotalSeconds < IntervalSeconds * 4 ? reading : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string App, double? Cpu, List<int> Fans)? monitorApp;
    private static DateTime monitorAppRead = DateTime.MinValue;
    private static int monitorAppReading;

    /// <summary>
    /// Readings from a hardware-monitor program, if one is running. A processor's own
    /// temperature can only be read by a driver, which PC Butler does not install;
    /// LibreHardwareMonitor and OpenHardwareMonitor have one and publish what they read.
    /// Returns the last answer at once and refreshes it in the background every few seconds.
    /// </summary>
    public static (string App, double? Cpu, List<int> Fans)? MonitorApp()
    {
        if ((DateTime.Now - monitorAppRead).TotalSeconds > IntervalSeconds * 2 && Interlocked.Exchange(ref monitorAppReading, 1) == 0)
        {
            Task.Run(() =>
            {
                try
                {
                    monitorApp = ReadMonitorApp();
                }
                finally
                {
                    monitorAppRead = DateTime.Now;
                    monitorAppReading = 0;
                }
            });
        }
        return monitorApp;
    }

    private static (string App, double? Cpu, List<int> Fans)? ReadMonitorApp()
    {
        foreach (var app in new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" })
        {
            try
            {
                double? cpu = null, package = null;
                var fans = new List<int>();
                using var sensors = new ManagementObjectSearcher($@"root\{app}", "SELECT Name, Value, SensorType, Identifier FROM Sensor");
                foreach (var sensor in sensors.Get())
                {
                    var (type, name, id) = (sensor["SensorType"]?.ToString(), sensor["Name"]?.ToString() ?? "", sensor["Identifier"]?.ToString() ?? "");
                    var value = Convert.ToDouble(sensor["Value"]);
                    if (type == "Fan" && value is > 0 and < 20000)
                        fans.Add((int)value);
                    if (type != "Temperature" || !id.Contains("cpu", StringComparison.OrdinalIgnoreCase) || value is <= 0 or >= 150)
                        continue;
                    // The whole processor's figure when there is one, otherwise the hottest core.
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                        package = value;
                    cpu = cpu is null || value > cpu ? value : cpu;
                }
                if ((package ?? cpu) is not null || fans.Count > 0)
                    return (app, (package ?? cpu) is { } degrees ? Math.Round(degrees, 1) : null, fans);
            }
            catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or FormatException
                                              or InvalidCastException or System.Runtime.InteropServices.COMException)
            {
                // Not running: its namespace doesn't exist.
            }
        }
        return null;
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
            // Made here, before the helper exists: what a program with administrator rights makes, this one could not open.
            LiveSlot.Prepare(Slot);
            LiveSlot.Prepare(FpsMeter.Slot);
            LiveSlot.Prepare(NetMeter.Slot);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--sensors");
            start.ArgumentList.Add(AppInfo.DataDir);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the permission prompt was declined
        }
    }

    /// <summary>
    /// The helper's whole life: read, hand over, repeat until the main program is gone.
    /// "directory" is the data folder, where requests come in (see AdminHelper).
    /// </summary>
    public static void RunHelper(string directory, int parentId)
    {
        LiveSlot.Owner = parentId;
        // Readings went through files here before; leave none behind.
        foreach (var old in new[] { "sensors.json", "sensors.json.new", "fps.json", "fps.json.new" })
        {
            try
            {
                File.Delete(Path.Combine(directory, old));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        var stopped = new List<string>(); // services paused for Boost (see AdminHelper)
        for (var tick = 0; ParentAlive(parentId); tick++)
        {
            AdminHelper.Poll(directory, stopped);
            if (tick % IntervalSeconds != 0)
            {
                Thread.Sleep(1000);
                continue;
            }
            LiveSlot.Write(Slot, JsonSerializer.Serialize(Read()));
            Thread.Sleep(1000);
        }
        AdminHelper.ResumeAll(stopped); // the main program is gone: nothing stays paused
        CpuSensor.Close();
        FpsMeter.Stop();
        NetMeter.Stop();
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
        var reading = new SensorReading { Time = DateTime.Now, CpuTemperature = CpuSensor.Read() };
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
