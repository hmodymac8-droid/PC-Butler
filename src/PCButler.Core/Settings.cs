// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>What Boost does, as chosen on the Booster page.</summary>
public sealed class BoostSettings
{
    /// <summary>Raise the chosen program's priority and lower the background programs'.</summary>
    [JsonPropertyName("priorities")] public bool Priorities { get; set; } = true;
    /// <summary>Switch to the high-performance power plan while Boost is on.</summary>
    [JsonPropertyName("power_plan")] public bool PowerPlan { get; set; } = true;
    /// <summary>Push background programs' memory out to the page file. Off by default: they reload it later.</summary>
    [JsonPropertyName("trim_memory")] public bool TrimMemory { get; set; }
    /// <summary>Pause a few Windows services while Boost is on. Only works while the administrator helper runs.</summary>
    [JsonPropertyName("services")] public bool Services { get; set; }
    /// <summary>Program file names (game.exe) that switch Boost on while they are running.</summary>
    [JsonPropertyName("auto_apps")] public List<string> AutoApps { get; set; } = [];
}

/// <summary>The performance overlay, as set on the Benchmark page.</summary>
public sealed class OverlaySettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>Start the administrator helper by itself when a game is detected, so frames are counted without a visit to the page.</summary>
    [JsonPropertyName("auto_helper")] public bool AutoHelper { get; set; }
    /// <summary>Show it all the time, not only while a game is in front.</summary>
    [JsonPropertyName("always")] public bool Always { get; set; }
    /// <summary>0 top left, 1 top right, 2 bottom left, 3 bottom right.</summary>
    [JsonPropertyName("corner")] public int Corner { get; set; }
    [JsonPropertyName("fps")] public bool Fps { get; set; } = true;
    [JsonPropertyName("cpu")] public bool Cpu { get; set; } = true;
    [JsonPropertyName("gpu")] public bool Gpu { get; set; } = true;
    [JsonPropertyName("memory")] public bool Memory { get; set; } = true;
    [JsonPropertyName("temperatures")] public bool Temperatures { get; set; } = true;
    /// <summary>The graphics card's own memory in use.</summary>
    [JsonPropertyName("video_memory")] public bool VideoMemory { get; set; } = true;
    /// <summary>Milliseconds per frame, next to the frames per second.</summary>
    [JsonPropertyName("frame_time")] public bool FrameTime { get; set; }
    [JsonPropertyName("fan")] public bool Fan { get; set; }
    /// <summary>Download and upload speed of the whole PC.</summary>
    [JsonPropertyName("network")] public bool Network { get; set; }
    [JsonPropertyName("clock")] public bool Clock { get; set; }
    /// <summary>The readings in the order the user put them; any left out follow in the usual order.</summary>
    [JsonPropertyName("order")] public List<string> Order { get; set; } = [];
    /// <summary>All readings on one line instead of in a column.</summary>
    [JsonPropertyName("horizontal")] public bool Horizontal { get; set; }
    /// <summary>Size in percent of the usual one.</summary>
    [JsonPropertyName("scale")] public int Scale { get; set; } = 100;
    /// <summary>Pixels from the side and from the top or bottom of the screen, counted from the chosen corner.</summary>
    [JsonPropertyName("offset_x")] public int OffsetX { get; set; } = 16;
    [JsonPropertyName("offset_y")] public int OffsetY { get; set; } = 16;
    /// <summary>Colours as #RRGGBB: the frames per second, the other numbers, the panel behind them.</summary>
    [JsonPropertyName("accent")] public string Accent { get; set; } = DefaultAccent;
    [JsonPropertyName("text")] public string Text { get; set; } = DefaultText;
    [JsonPropertyName("background")] public string Background { get; set; } = DefaultBackground;
    /// <summary>How solid the panel is, 0 (only the numbers) to 100.</summary>
    [JsonPropertyName("opacity")] public int Opacity { get; set; } = 35;

    // The program's own colours: its green, its soft white, its card. The panel is mostly see-through by default.
    public const string DefaultAccent = "#42D8A5", DefaultText = "#E8EBF0", DefaultBackground = "#101216";

    /// <summary>Every reading the overlay can show, in the order it comes in.</summary>
    public static readonly string[] Rows = ["fps", "frame_time", "cpu", "gpu", "vram", "fan", "ram", "net", "clock"];

    /// <summary>The readings in the user's order: what they arranged first, then whatever that list does not mention.</summary>
    public List<string> OrderedRows() => Order.Where(Rows.Contains).Distinct().Concat(Rows.Where(row => !Order.Contains(row))).ToList();

    public bool Shows(string row) => row switch
    {
        "fps" => Fps, "frame_time" => FrameTime, "cpu" => Cpu, "gpu" => Gpu, "vram" => VideoMemory, "fan" => Fan, "ram" => Memory, "net" => Network, "clock" => Clock,
        _ => false,
    };

    public void SetShown(string row, bool on)
    {
        switch (row)
        {
            case "fps": Fps = on; break;
            case "frame_time": FrameTime = on; break;
            case "cpu": Cpu = on; break;
            case "gpu": Gpu = on; break;
            case "vram": VideoMemory = on; break;
            case "fan": Fan = on; break;
            case "ram": Memory = on; break;
            case "net": Network = on; break;
            case "clock": Clock = on; break;
        }
    }

    /// <summary>True for a colour written as #RRGGBB.</summary>
    public static bool IsColour(string text) => text.Length == 7 && text[0] == '#' && text[1..].All(Uri.IsHexDigit);

    /// <summary>The look and the order as they came; what is shown, and where games are detected, stays.</summary>
    public void ResetLook()
    {
        var fresh = new OverlaySettings();
        (Order, Horizontal, Scale, Corner, OffsetX, OffsetY) = ([], fresh.Horizontal, fresh.Scale, fresh.Corner, fresh.OffsetX, fresh.OffsetY);
        (Accent, Text, Background, Opacity) = (fresh.Accent, fresh.Text, fresh.Background, fresh.Opacity);
    }

    /// <summary>Program file names (game.exe) always taken for a game, full screen or not.</summary>
    [JsonPropertyName("games")] public List<string> Games { get; set; } = [];
}

/// <summary>Recording what each app uses, for the report on the Benchmark page.</summary>
public sealed class UsageSettings
{
    /// <summary>Record how much each app uses of the processor, graphics card, memory, disk and internet.</summary>
    [JsonPropertyName("record")] public bool Record { get; set; } = true;
}

/// <summary>How Courier behaves, and the PCs it trusts.</summary>
public sealed class PortalSettings
{
    /// <summary>Where received files go; "" = "PC Butler Courier" inside Downloads.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    /// <summary>The PCs this one was paired with: it trusts them, by their certificate.</summary>
    [JsonPropertyName("devices")] public List<PortalDevice> Devices { get; set; } = [];
    /// <summary>True once Courier was switched on here: from then on it starts with PC Butler (when AutoStart is on).</summary>
    [JsonPropertyName("used")] public bool Used { get; set; }
    /// <summary>Start Courier with PC Butler, so this PC can be reached, and stays connected to its PCs, without a click.</summary>
    [JsonPropertyName("auto_start")] public bool AutoStart { get; set; } = true;
    /// <summary>Show the small Courier window, for dropping files and watching transfers, while connected.</summary>
    [JsonPropertyName("widget")] public bool Widget { get; set; } = true;
    /// <summary>The list of PCs: the ones added by their ID and the ones paired with, kept to connect again later.</summary>
    [JsonPropertyName("saved")] public List<PortalSaved> Saved { get; set; } = [];
    /// <summary>What happens when a new PC asks to connect: "ask" (the user allows or refuses it) or "password".</summary>
    [JsonPropertyName("incoming")] public string Incoming { get; set; } = Ask;
    /// <summary>The password new PCs must give, as Portal.HashPassword made it; "" = none set.</summary>
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    /// <summary>Share copied text with the connected PCs and phones: text they send lands on this clipboard, and (on a PC) what is copied here goes to them.</summary>
    [JsonPropertyName("share_clipboard")] public bool ShareClipboard { get; set; } = true;

    /// <summary>Before 1.22 a PC was the host or a client ("host", "client"); read once, to bring its settings over.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    /// <summary>Before 1.22: the host a client connected to.</summary>
    [JsonPropertyName("host_number")] public string HostNumber { get; set; } = "";

    public const string Ask = "ask", WithPassword = "password";
}

/// <summary>A PC that was paired with this one. Its id is the fingerprint of its certificate, which is what "trusted" means.</summary>
public sealed class PortalDevice
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Take what it sends without asking each time.</summary>
    [JsonPropertyName("auto_accept")] public bool AutoAccept { get; set; }
    /// <summary>Its internet address when last connected ("1.2.3.4:5000"), tried first the next time before looking it up.</summary>
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
}

/// <summary>A PC on the list, by its ID, under the name the user gave it.</summary>
public sealed class PortalSaved
{
    /// <summary>Its ID, the six digits.</summary>
    [JsonPropertyName("number")] public string Number { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Kept connected: looked for whenever Courier runs, and found again when the link drops, until Disconnect.</summary>
    [JsonPropertyName("stay")] public bool Stay { get; set; }
    /// <summary>Before 1.22: connected to when Courier started. Read once, it becomes Stay.</summary>
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
}
