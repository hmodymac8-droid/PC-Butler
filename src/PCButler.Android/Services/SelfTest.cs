// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Security.Cryptography.X509Certificates;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>
/// A check of Courier on this very phone, for development: two Courier nodes inside the
/// app, joined over the phone's own loopback address, pair and pass a file, through the same
/// encrypted connection (with a certificate on each side) that PCs use. Started with
/// "adb shell am start -n com.hmodymac8droid.pcbutler/crc...MainActivity --ez selftest true";
/// the result goes to the log ("adb logcat -s PCButler").
/// </summary>
public static class SelfTest
{
    public static async Task Run()
    {
        void Say(string text)
        {
            global::Android.Util.Log.Info("PCButler", text);
            Log.Info(text);
        }

        try
        {
            var root = Path.Combine(FileSystem.CacheDirectory, "selftest");
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            X509Certificate2 Identity() => X509CertificateLoader.LoadPkcs12(Portal.NewCertificate(), null);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var host = await PortalNode.Start(new PortalSettings { Folder = Path.Combine(root, "host") }, certificate: Identity(), local: true);
            var client = await PortalNode.Start(new PortalSettings { Folder = Path.Combine(root, "client") }, certificate: Identity(), local: true);
            Say($"SELFTEST: two nodes started in {clock.ElapsedMilliseconds} ms");
            var hostPeer = await client.ConnectDirect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, host.LinkPort), host.Number);
            Say($"SELFTEST: connect {(hostPeer is null ? "FAILED" : "ok")}");
            if (hostPeer is null)
                return;
            var asked = await client.Pair(hostPeer);
            for (var i = 0; i < 100 && host.Pairings.Count == 0; i++)
                await Task.Delay(50);
            Say($"SELFTEST: pairing {(asked is not null && host.Pairings.Count == 1 && host.Pairings[0].Code == asked.Code ? "ok, code " + asked.Code : "FAILED")}");
            if (asked is null || host.Pairings.Count == 0)
                return;
            client.Confirm(asked);
            host.Confirm(host.Pairings[0]);
            var payload = new byte[12_000_000];
            Random.Shared.NextBytes(payload);
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, "test.bin");
            await File.WriteAllBytesAsync(file, payload);
            clock.Restart();
            var transfer = client.Send(hostPeer, [file])!;
            while (transfer.Running && clock.Elapsed < TimeSpan.FromSeconds(60))
                await Task.Delay(100);
            var arrived = Path.Combine(root, "host", "test.bin");
            var whole = File.Exists(arrived) && (await File.ReadAllBytesAsync(arrived)).AsSpan().SequenceEqual(payload);
            Say($"SELFTEST: file {(whole ? "ok" : "FAILED")} ({transfer.State} {transfer.Note}) {payload.Length / 1048576.0 / clock.Elapsed.TotalSeconds:0.0} MB/s");
            var back = host.Send(host.Peers.Single(peer => peer.Id == client.Id), [arrived])!;
            clock.Restart();
            while (back.Running && clock.Elapsed < TimeSpan.FromSeconds(60))
                await Task.Delay(100);
            Say($"SELFTEST: back to the client {(back.State == "done" ? "ok" : "FAILED " + back.Note)}");
            string? got = null;
            host.ClipboardReceived = (_, text) => { got = text; return true; };
            var sentText = "Clipboard test مرحبا " + Guid.NewGuid();
            var problem = await client.SendClipboard(hostPeer, sentText);
            Say($"SELFTEST: clipboard {(problem is null && got == sentText ? "ok" : "FAILED " + problem)}");
            host.ClipboardReceived = (_, _) => false;
            problem = await client.SendClipboard(hostPeer, "x");
            Say($"SELFTEST: clipboard switched off {(problem is not null && problem != PortalNode.OldVersion ? "ok: " + problem : "FAILED " + problem)}");
            await client.Stop();
            await host.Stop();
            Say("SELFTEST: DONE");
        }
        catch (Exception error)
        {
            Say($"SELFTEST: FAILED with {error}");
        }
    }
}
