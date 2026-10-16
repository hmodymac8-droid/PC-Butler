// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PCButler.Core;

/// <summary>Whether a PC this one connects to asked for its password.</summary>
public enum PortalPasswordAsk { None, Needed, Wrong }

/// <summary>
/// The list of PCs: the ones the user added by their ID and the ones paired with, whichever
/// side asked. The same rules whether Courier runs or not, so the page can change the list
/// either way.
/// </summary>
public static class PortalSavedList
{
    public static PortalSaved? Find(PortalSettings settings, string number) =>
        settings.Saved.FirstOrDefault(saved => saved.Number == Portal.NormalizeNumber(number));

    /// <summary>
    /// At start: brings over the settings of before 1.22 (a host's clients and a client's host
    /// join the list; the PC a client was connected to, or its favourite, stays connected),
    /// and every paired PC is on the list.
    /// </summary>
    public static void Start(PortalSettings settings)
    {
        settings.Saved.RemoveAll(saved => !Portal.IsNumber(saved.Number));
        if (settings.Role.Length > 0)
        {
            settings.Used = true;
            // IDs were twelve digits before 1.16: a client paired with its host knows the host's new one from its certificate.
            if (settings.Role == "client" && !Portal.IsNumber(settings.HostNumber) && settings.Devices is [{ } only] && only.Id.Length >= 15)
                settings.HostNumber = Portal.NumberOf(only.Id);
            if (settings.Role == "client" && Portal.IsNumber(settings.HostNumber))
            {
                Add(settings, settings.HostNumber, "");
                Find(settings, settings.HostNumber)!.Stay = true;
            }
            (settings.Role, settings.HostNumber) = ("", "");
        }
        foreach (var saved in settings.Saved.Where(saved => saved.Favorite))
            (saved.Stay, saved.Favorite) = (true, false);
        foreach (var device in settings.Devices.Where(device => device.Id.Length >= 15))
            Remember(settings, Portal.NumberOf(device.Id), device.Name);
    }

    /// <summary>A PC connected to or paired with: added to the list if it is not there. Returns the name to show for it (the user's, if they gave one).</summary>
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
        name = name.Trim().Length == 0 ? "" : Portal.CleanLabel(name.Trim());
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
        if (Find(settings, number) is not { } saved || name.Trim().Length == 0)
            return;
        saved.Name = name;
        foreach (var device in settings.Devices.Where(device => device.Id.Length >= 15 && Portal.NumberOf(device.Id) == saved.Number))
            device.Name = name;
    }

    /// <summary>Off the list, and no longer trusted: connecting to it again means pairing again.</summary>
    public static void Remove(PortalSettings settings, string number)
    {
        number = Portal.NormalizeNumber(number);
        settings.Saved.RemoveAll(saved => saved.Number == number);
        settings.Devices.RemoveAll(device => device.Id.Length >= 15 && Portal.NumberOf(device.Id) == number);
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
    /// <summary>The PCs that asked this one for their password (none was given, or a wrong one), by ID.</summary>
    public IReadOnlyList<(string Number, PortalPasswordAsk Ask)> PasswordAsks
    {
        get
        {
            lock (gate)
                return attempts.Where(item => item.Value.PasswordAsk != PortalPasswordAsk.None).Select(item => (item.Key, item.Value.PasswordAsk)).ToList();
        }
    }

    /// <summary>The password the user typed for a PC that asked for one; the next try to connect to it gives it.</summary>
    public void GivePassword(string number, string password)
    {
        var attempt = AttemptFor(Portal.NormalizeNumber(number));
        (attempt.Password, attempt.PasswordAsk, attempt.NextTry) = (password, PortalPasswordAsk.None, 0);
        Raise();
    }

    /// <summary>How new PCs get in. The page changes the setting; this tells the running Courier.</summary>
    public void UseIncoming(string rule, string password)
    {
        lock (gate)
            (settings.Incoming, settings.Password) = (rule, password);
    }

    /// <summary>Change the list while Courier runs: the change is saved, and the names shown follow it.</summary>
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
                if (peer.Trusted && !settings.Devices.Any(device => device.Id.Equals(peer.Id, StringComparison.OrdinalIgnoreCase)))
                    (peer.Trusted, peer.AutoAccept) = (false, false);
            }
        }
        save();
        Raise();
    }
}
