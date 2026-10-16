// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PCButler.Core;

/// <summary>Whether the host a client connects to asked for its password.</summary>
public enum PortalPasswordAsk { None, Needed, Wrong }

/// <summary>
/// A client's list of the PCs it can connect to: the ones it was paired with, and the ones
/// the user added by their ID. The same rules whether Courier runs or not, so the page can
/// change the list either way.
/// </summary>
public static class PortalSavedList
{
    public static PortalSaved? Find(PortalSettings settings, string number) =>
        settings.Saved.FirstOrDefault(saved => saved.Number == Portal.NormalizeNumber(number));

    /// <summary>At start: the host of before 1.21.5 joins the list, and the favourite is the PC connected to.</summary>
    public static void Start(PortalSettings settings)
    {
        settings.Saved.RemoveAll(saved => !Portal.IsNumber(saved.Number));
        if (Portal.IsNumber(settings.HostNumber) && Find(settings, settings.HostNumber) is null)
        {
            var name = settings.Devices.FirstOrDefault(device => Portal.NumberOf(device.Id) == settings.HostNumber)?.Name ?? "";
            settings.Saved.Add(new PortalSaved { Number = settings.HostNumber, Name = name });
        }
        if (settings.Saved.FirstOrDefault(saved => saved.Favorite) is { } favorite)
            settings.HostNumber = favorite.Number;
    }

    /// <summary>A PC connected to: added to the list if it is not there. Returns the name to show for it (the user's, if they gave one).</summary>
    public static string Remember(PortalSettings settings, string number, string name)
    {
        if (Find(settings, number) is { } saved)
        {
            if (saved.Name.Length == 0)
                saved.Name = name;
            return saved.Name;
        }
        settings.Saved.Add(new PortalSaved { Number = Portal.NormalizeNumber(number), Name = name });
        return name;
    }

    /// <summary>Add a PC by its ID, to connect to later. False when the ID is not nine digits.</summary>
    public static bool Add(PortalSettings settings, string number, string name)
    {
        number = Portal.NormalizeNumber(number);
        if (!Portal.IsNumber(number))
            return false;
        name = Portal.CleanLabel(name.Trim());
        if (Find(settings, number) is { } saved)
        {
            if (name.Length > 0)
                saved.Name = name;
            return true;
        }
        settings.Saved.Add(new PortalSaved { Number = number, Name = name });
        return true;
    }

    public static void Rename(PortalSettings settings, string number, string name)
    {
        name = Portal.CleanLabel(name.Trim());
        if (Find(settings, number) is not { } saved || name.Length == 0)
            return;
        saved.Name = name;
        foreach (var device in settings.Devices.Where(device => Portal.NumberOf(device.Id) == saved.Number))
            device.Name = name;
    }

    /// <summary>Off the list, and no longer trusted: connecting to it again means pairing again.</summary>
    public static void Remove(PortalSettings settings, string number)
    {
        number = Portal.NormalizeNumber(number);
        settings.Saved.RemoveAll(saved => saved.Number == number);
        settings.Devices.RemoveAll(device => Portal.NumberOf(device.Id) == number);
        if (settings.HostNumber == number)
            settings.HostNumber = settings.Saved.FirstOrDefault(saved => saved.Favorite)?.Number ?? settings.Saved.FirstOrDefault()?.Number ?? "";
    }

    /// <summary>The favourite is connected to when Courier starts. One at most; the same one again takes it away.</summary>
    public static void ToggleFavorite(PortalSettings settings, string number)
    {
        var chosen = Find(settings, number);
        var on = chosen is { Favorite: false };
        foreach (var saved in settings.Saved)
            saved.Favorite = false;
        if (chosen is not null)
            chosen.Favorite = on;
    }

    /// <summary>What to call it: the name given, or its ID until it has one.</summary>
    public static string Label(PortalSaved saved) => saved.Name.Length > 0 ? saved.Name : Portal.ShowNumber(saved.Number);
}

/// <summary>A few wrong passwords from one PC, or many from all, and new tries wait a while: nobody can guess their way in.</summary>
internal static class PasswordTries
{
    private const int PerPc = 5, InAll = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentQueue<(string Caller, DateTime At)> failures = new();

    public static bool Allow(string caller)
    {
        var since = DateTime.UtcNow - Window;
        while (failures.TryPeek(out var oldest) && oldest.At < since)
            failures.TryDequeue(out _);
        var recent = failures.ToArray();
        return recent.Length < InAll && recent.Count(failure => failure.Caller == caller) < PerPc;
    }

    public static void Failed(string caller) => failures.Enqueue((caller, DateTime.UtcNow));

    /// <summary>For the tests.</summary>
    internal static void Clear() => failures.Clear();
}

public static partial class Portal
{
    private const int PasswordRounds = 120_000;

    /// <summary>A password as it is kept: salted and stretched, so the settings file does not give it away.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, PasswordRounds, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${PasswordRounds}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>True when the password matches the kept one. Nothing matches when no password is kept.</summary>
    public static bool CheckPassword(string password, string kept)
    {
        var parts = kept.Split('$');
        if (password.Length == 0 || parts is not ["pbkdf2-sha256", var rounds, var salt, var hash] || !int.TryParse(rounds, out var count) || count is < 1000 or > 10_000_000)
            return false;
        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), count, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed partial class PortalNode
{
    /// <summary>A client: the password to give the host with the next pairing, as the user typed it. Kept in memory only, and only until it is used.</summary>
    internal string HostPassword { get; private set; } = "";

    /// <summary>A client: the ID of the PC it connects to.</summary>
    public string HostNumber => settings.HostNumber;

    /// <summary>A client: the host asked for its password (none was given, or a wrong one).</summary>
    public PortalPasswordAsk PasswordWanted { get; private set; }

    /// <summary>A client: paired on this side, but the host's user has not allowed it yet.</summary>
    public bool WaitingForHost { get; private set; }

    /// <summary>A client: the password the user typed for the host; the next try to connect gives it.</summary>
    public void GivePassword(string password)
    {
        HostPassword = password;
        PasswordWanted = PortalPasswordAsk.None;
        Raise();
    }

    /// <summary>A host: how new PCs get in. The page changes the setting; this tells the running Courier.</summary>
    public void UseIncoming(string rule, string password)
    {
        lock (gate)
            (settings.Incoming, settings.Password) = (rule, password);
    }

    /// <summary>A client: connect to another PC from the list (or any ID). The one before stays on the list.</summary>
    public void ConnectTo(string number)
    {
        number = Portal.NormalizeNumber(number);
        if (!Portal.IsNumber(number) || number == Number)
            return;
        lock (gate)
            PortalSavedList.Add(settings, number, "");
        (PasswordWanted, HostPassword, WaitingForHost) = (PortalPasswordAsk.None, "", false);
        SetHost(number);
    }

    /// <summary>Change the saved list of a running client: the change is saved, and the names shown follow it.</summary>
    public void ChangeSaved(Action<PortalSettings> change)
    {
        lock (gate)
        {
            change(settings);
            foreach (var peer in peers.Values)
            {
                if (PortalSavedList.Find(settings, peer.Number) is { Name.Length: > 0 } saved)
                    peer.Name = saved.Name;
                // Removed from the list: no longer trusted either.
                if (!IsHost && peer.Trusted && !settings.Devices.Any(device => device.Id.Equals(peer.Id, StringComparison.OrdinalIgnoreCase)))
                    (peer.Trusted, peer.AutoAccept) = (false, false);
            }
        }
        save();
        Raise();
    }

    /// <summary>A host: give a client another name. It shows under it from now on.</summary>
    public void Rename(string id, string name)
    {
        name = Portal.CleanLabel(name.Trim());
        if (name.Length == 0)
            return;
        lock (gate)
        {
            if (peers.TryGetValue(id, out var peer))
                peer.Name = name;
            foreach (var device in settings.Devices.Where(device => device.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                device.Name = name;
        }
        save();
        Raise();
    }

    /// <summary>The host answered a request of a paired client: it has been allowed.</summary>
    private void HostAllowed()
    {
        if (!WaitingForHost)
            return;
        WaitingForHost = false;
        Raise();
    }

    /// <summary>The host turned a request down because it does not know this client (yet).</summary>
    private void HostRefused()
    {
        if (WaitingForHost)
            return;
        WaitingForHost = true;
        Raise();
    }
}
