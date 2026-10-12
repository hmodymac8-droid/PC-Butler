// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;

namespace PCButler.Core;

/// <summary>
/// The graphics card's temperature and fan speed, asked from Windows' graphics kernel:
/// the same figures Task Manager shows. It works for any maker's card (AMD, NVIDIA,
/// Intel) with a current driver, and needs no administrator rights and no extra library.
/// </summary>
public static class GpuSensors
{
    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters
    {
        public uint Count;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters adapters);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryInfo query);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter adapter);

    // D3DKMT_ADAPTERINFO is 20 bytes with the handle first. D3DKMT_ADAPTER_PERFDATA is 64 bytes:
    // fan speed (RPM) at 48 and temperature (tenths of a degree Celsius) at 56.
    private const int MaxAdapters = 16, AdapterInfoSize = 20, PerfDataSize = 64, FanOffset = 48, TemperatureOffset = 56;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    /// <summary>The hottest adapter's temperature in °C and its fan's RPM; null for what the driver doesn't report.</summary>
    public static (int? Temperature, int? Fan) Read()
    {
        var list = Marshal.AllocHGlobal(MaxAdapters * AdapterInfoSize);
        var data = Marshal.AllocHGlobal(PerfDataSize);
        int? temperature = null, fan = null;
        try
        {
            var adapters = new EnumAdapters { Count = MaxAdapters, Adapters = list };
            if (D3DKMTEnumAdapters2(ref adapters) != 0)
                return (null, null);
            // A card is listed once per output, and Windows' software renderer is listed too (it refuses the question).
            for (var index = 0; index < adapters.Count; index++)
            {
                var handle = (uint)Marshal.ReadInt32(list, index * AdapterInfoSize);
                for (var offset = 0; offset < PerfDataSize; offset += 4)
                    Marshal.WriteInt32(data, offset, 0);
                var query = new QueryInfo { Adapter = handle, Type = KMTQAITYPE_ADAPTERPERFDATA, Data = data, Size = PerfDataSize };
                if (D3DKMTQueryAdapterInfo(ref query) == 0)
                {
                    var degrees = Marshal.ReadInt32(data, TemperatureOffset) / 10;
                    if (degrees is > 0 and < 150 && (temperature is null || degrees > temperature))
                    {
                        temperature = degrees;
                        var speed = Marshal.ReadInt32(data, FanOffset);
                        fan = speed is > 0 and < 20000 ? speed : null;
                    }
                }
                var close = new CloseAdapter { Adapter = handle };
                D3DKMTCloseAdapter(ref close);
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            // An older Windows without this question.
        }
        finally
        {
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(data);
        }
        return (temperature, fan);
    }
}
