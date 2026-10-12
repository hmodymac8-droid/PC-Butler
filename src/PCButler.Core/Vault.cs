// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>A problem worth showing the user as is.</summary>
public class VaultException(string message) : Exception(message);

public sealed class WrongPasswordException() : VaultException("Wrong password.");

/// <summary>One file kept in the vault.</summary>
public sealed class VaultItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>When it was added, in seconds since 1970 (UTC).</summary>
    [JsonPropertyName("added")] public double Added { get; set; }
    /// <summary>Where it came from, offered as the default when moving it out.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
}

/// <summary>
/// A password-protected vault: files moved in are encrypted, and can only be read again
/// by opening them from the vault or moving them back out. The file format is the one
/// Downloads Sorter used, so an existing vault opens unchanged.
///
/// How it is protected
/// - The password is stretched with scrypt into a key that unlocks a random "vault key".
///   Changing the password re-wraps that key; files stay as they are.
/// - Every file is encrypted with AES-256-GCM in 1 MB chunks under a random file name.
///   The list of files (names, sizes, folders) is encrypted too, so the vault folder
///   shows nothing about what is inside.
/// - Any change to an encrypted file is detected (GCM), so a damaged or tampered file is
///   refused instead of opened.
/// - Opening a file decrypts a copy into a private folder. When the vault locks, edits to
///   the copy are saved back into the vault and the copy is removed.
/// </summary>
public sealed class Vault
{
    public static string VaultDir => Path.Combine(AppInfo.DataDir, "Vault");
    private static string HeaderFile => Path.Combine(VaultDir, "vault.json");
    private static string IndexFile => Path.Combine(VaultDir, "index.bin");
    /// <summary>
    /// Decrypted copies of opened files: under Local AppData, away from anything the cleaner or sorter scans.
    /// </summary>
    public static string OpenDir =>
        Environment.GetEnvironmentVariable("PCBUTLER_DATA") is { Length: > 0 } custom
            ? Path.Combine(custom, "VaultOpen")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCButler", "VaultOpen");

    private const int Chunk = 1024 * 1024;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "DSV1"u8.ToArray();
    public const int MinPassword = 6;
    // About half a second per try, on purpose.
    private const int ScryptN = 1 << 17, ScryptR = 8, ScryptP = 1;

    private readonly byte[] key;
    private readonly Dictionary<string, (string Copy, DateTime Modified, long Size)> opened = [];

    public List<VaultItem> Items { get; private set; } = [];

    private Vault(byte[] key) => this.key = key;

    public static bool Exists => File.Exists(HeaderFile);

    // --- sealing small pieces of data: output is ciphertext followed by the 16-byte tag ---

    private static byte[] Seal(byte[] key, byte[] nonce, ReadOnlySpan<byte> plain, byte[] associated)
    {
        var sealedData = new byte[plain.Length + TagSize];
        using var cipher = new AesGcm(key, TagSize);
        cipher.Encrypt(nonce, plain, sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length), associated);
        return sealedData;
    }

    /// <summary>Throws CryptographicException (AuthenticationTagMismatchException) if it was altered.</summary>
    private static byte[] Unseal(byte[] key, byte[] nonce, ReadOnlySpan<byte> sealedData, byte[] associated)
    {
        if (sealedData.Length < TagSize)
            throw new CryptographicException("too short");
        var plain = new byte[sealedData.Length - TagSize];
        using var cipher = new AesGcm(key, TagSize);
        cipher.Decrypt(nonce, sealedData[..plain.Length], sealedData[plain.Length..], plain, associated);
        return plain;
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var temp = path + ".new";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] Wrap(byte[] vaultKey, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        var passwordKey = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), salt, ScryptN, ScryptR, ScryptP, 32);
        var wrapped = Seal(passwordKey, nonce, vaultKey, "vault-key"u8.ToArray());
        var header = new Dictionary<string, object>
        {
            ["version"] = 1, ["kdf"] = "scrypt",
            ["params"] = new Dictionary<string, int> { ["n"] = ScryptN, ["r"] = ScryptR, ["p"] = ScryptP },
            ["salt"] = Convert.ToBase64String(salt), ["nonce"] = Convert.ToBase64String(nonce),
            ["key"] = Convert.ToBase64String(wrapped),
        };
        return JsonSerializer.SerializeToUtf8Bytes(header, new JsonSerializerOptions { WriteIndented = true });
    }

    public static Vault Create(string password)
    {
        if (Exists)
            throw new VaultException(Tr.T("A vault already exists."));
        if (password.Length < MinPassword)
            throw new VaultException(Tr.T("Use a password of at least {0} characters.", MinPassword));
        Directory.CreateDirectory(VaultDir);
        var vaultKey = RandomNumberGenerator.GetBytes(32);
        WriteAtomic(HeaderFile, Wrap(vaultKey, password));
        var vault = new Vault(vaultKey);
        vault.SaveIndex();
        return vault;
    }

    public static Vault Unlock(string password)
    {
        byte[] salt, nonce, wrapped;
        int n, r, p;
        try
        {
            using var header = JsonDocument.Parse(File.ReadAllText(HeaderFile));
            var root = header.RootElement;
            salt = Convert.FromBase64String(root.GetProperty("salt").GetString()!);
            nonce = Convert.FromBase64String(root.GetProperty("nonce").GetString()!);
            wrapped = Convert.FromBase64String(root.GetProperty("key").GetString()!);
            var parameters = root.GetProperty("params");
            (n, r, p) = (parameters.GetProperty("n").GetInt32(), parameters.GetProperty("r").GetInt32(),
                         parameters.GetProperty("p").GetInt32());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new VaultException(Tr.T("The vault could not be read: {0}", error.Message));
        }
        byte[] vaultKey;
        try
        {
            var passwordKey = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), salt, n, r, p, 32);
            vaultKey = Unseal(passwordKey, nonce, wrapped, "vault-key"u8.ToArray());
        }
        catch (CryptographicException)
        {
            throw new WrongPasswordException();
        }
        var vault = new Vault(vaultKey);
        vault.LoadIndex();
        return vault;
    }

    /// <summary>Remove decrypted copies left from an earlier session. Returns any still in use.</summary>
    public static List<string> ClearOpenCopies()
    {
        var left = new List<string>();
        if (!Directory.Exists(OpenDir))
            return left;
        foreach (var entry in Directory.GetFileSystemEntries(OpenDir))
        {
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                left.Add(entry);
            }
        }
        return left;
    }

    // --- the encrypted file list ---

    private void LoadIndex()
    {
        if (!File.Exists(IndexFile))
        {
            Items = [];
            return;
        }
        var data = File.ReadAllBytes(IndexFile);
        try
        {
            var plain = Unseal(key, data[..12], data.AsSpan(12), "index"u8.ToArray());
            Items = JsonSerializer.Deserialize<List<VaultItem>>(plain) ?? [];
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        {
            throw new VaultException(Tr.T("The vault's file list is damaged."));
        }
    }

    private void SaveIndex()
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(Items);
        WriteAtomic(IndexFile, [.. nonce, .. Seal(key, nonce, plain, "index"u8.ToArray())]);
    }

    // --- encrypting and decrypting one file, a chunk at a time ---

    private static string Blob(string itemId) => Path.Combine(VaultDir, itemId + ".bin");

    /// <summary>The chunk number and "last" flag are authenticated, so chunks can't be dropped or reordered.</summary>
    private static byte[] ChunkAad(string itemId, uint index, bool last)
    {
        var id = Encoding.UTF8.GetBytes(itemId);
        var aad = new byte[id.Length + 5];
        id.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(id.Length), index);
        aad[^1] = (byte)(last ? 1 : 0);
        return aad;
    }

    private static byte[] ChunkNonce(byte[] prefix, uint index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        return total;
    }

    private void EncryptFile(string source, string itemId, Action<long, long>? progress, CancellationToken cancel)
    {
        var prefix = RandomNumberGenerator.GetBytes(8);
        var size = new FileInfo(source).Length;
        long done = 0;
        var target = Path.ChangeExtension(Blob(itemId), ".new");
        try
        {
            using (var reader = File.OpenRead(source))
            using (var writer = File.Create(target))
            {
                writer.Write(Magic);
                writer.Write(prefix);
                byte[] buffer = new byte[Chunk], next = new byte[Chunk], header = new byte[4];
                var length = ReadFully(reader, buffer);
                uint index = 0;
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var following = length > 0 ? ReadFully(reader, next) : 0;
                    var last = following == 0;
                    var sealedChunk = Seal(key, ChunkNonce(prefix, index), buffer.AsSpan(0, length), ChunkAad(itemId, index, last));
                    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)sealedChunk.Length);
                    writer.Write(header);
                    writer.Write(sealedChunk);
                    done += length;
                    progress?.Invoke(done, size);
                    if (last)
                        break;
                    index++;
                    (buffer, next, length) = (next, buffer, following);
                }
            }
            File.Move(target, Blob(itemId), overwrite: true);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>The decrypted content of a vault file, a chunk at a time. Throws VaultException if it was altered.</summary>
    private IEnumerable<byte[]> Chunks(VaultItem item)
    {
        using var reader = File.OpenRead(Blob(item.Id));
        var header = new byte[12];
        if (ReadFully(reader, header) < 12 || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new VaultException(Tr.T("{0} is damaged.", item.Name));
        var prefix = header[4..];
        uint index = 0;
        var lengthBytes = new byte[4];
        while (true)
        {
            if (ReadFully(reader, lengthBytes) < 4)
                throw new VaultException(Tr.T("{0} is damaged (cut short).", item.Name));
            var sealedChunk = new byte[BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes)];
            if (ReadFully(reader, sealedChunk) < sealedChunk.Length)
                throw new VaultException(Tr.T("{0} is damaged (cut short).", item.Name));
            byte[]? chunk = null;
            var last = false;
            foreach (var candidate in new[] { false, true })
            {
                try
                {
                    chunk = Unseal(key, ChunkNonce(prefix, index), sealedChunk, ChunkAad(item.Id, index, candidate));
                    last = candidate;
                    break;
                }
                catch (CryptographicException)
                {
                }
            }
            if (chunk is null)
                throw new VaultException(Tr.T("{0} is damaged or was changed outside the vault.", item.Name));
            yield return chunk;
            if (last)
                yield break;
            index++;
        }
    }

    private void DecryptFile(VaultItem item, string target, Action<long, long>? progress = null)
    {
        long done = 0;
        try
        {
            using var writer = File.Create(target);
            foreach (var chunk in Chunks(item))
            {
                writer.Write(chunk);
                done += chunk.Length;
                progress?.Invoke(done, item.Size);
            }
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>True if the vault copy decrypts to exactly the source file's bytes.</summary>
    private bool Matches(VaultItem item, string source)
    {
        using var reader = File.OpenRead(source);
        foreach (var chunk in Chunks(item))
        {
            var buffer = new byte[chunk.Length];
            if (ReadFully(reader, buffer) != chunk.Length || !buffer.AsSpan().SequenceEqual(chunk))
                return false;
        }
        return reader.ReadByte() == -1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    // --- what the page does ---

    /// <summary>
    /// Move files into the vault: encrypt, check, then delete the original for good (the
    /// Recycle Bin would keep a readable copy). progress(file number, files, name, done, size).
    /// </summary>
    public (int Count, List<string> Errors) Add(IReadOnlyList<string> paths,
        Action<int, int, string, long, long>? progress = null, CancellationToken cancel = default)
    {
        var added = 0;
        var errors = new List<string>();
        for (var number = 0; number < paths.Count; number++)
        {
            if (cancel.IsCancellationRequested)
                break;
            var path = paths[number];
            var name = Path.GetFileName(path);
            try
            {
                if (!File.Exists(path))
                    throw new VaultException(Tr.T("not a file"));
                if (Sorter.Norm(path).Contains(Sorter.Norm(VaultDir)) || Sorter.Norm(path).Contains(Sorter.Norm(OpenDir)))
                    throw new VaultException(Tr.T("it is already in the vault"));
                var info = new FileInfo(path);
                var item = new VaultItem
                {
                    Id = Guid.NewGuid().ToString("N"), Name = name, Size = info.Length,
                    Added = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, Folder = info.DirectoryName ?? "",
                };
                var fileNumber = number;
                EncryptFile(path, item.Id, progress is null ? null : (done, size) => progress(fileNumber, paths.Count, name, done, size), cancel);
                if (!Matches(item, path))
                {
                    TryDelete(Blob(item.Id));
                    throw new VaultException(Tr.T("the encrypted copy did not check out, so the file was left where it is"));
                }
                Items.Add(item);
                SaveIndex();
                added++;
                try
                {
                    File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    errors.Add(Tr.T("{0}: copied into the vault, but the original could not be removed ({1})", name, error.Message));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or VaultException)
            {
                errors.Add($"{name}: {error.Message}");
            }
        }
        return (added, errors);
    }

    /// <summary>
    /// Decrypt files back to a normal folder (their original one by default) and remove them from the vault.
    /// </summary>
    public (int Count, List<string> Errors) MoveOut(IReadOnlyList<VaultItem> items, string? folder = null,
        Action<int, int, string, long, long>? progress = null)
    {
        var moved = 0;
        var errors = new List<string>();
        for (var number = 0; number < items.Count; number++)
        {
            var item = items[number];
            try
            {
                CloseCopy(item);
                var destination = folder ?? item.Folder;
                Directory.CreateDirectory(destination);
                var target = Sorter.UniqueDestination(destination, item.Name);
                var fileNumber = number;
                DecryptFile(item, target, progress is null ? null : (done, size) => progress(fileNumber, items.Count, item.Name, done, size));
                Items.Remove(item);
                SaveIndex();
                TryDelete(Blob(item.Id));
                moved++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or VaultException)
            {
                errors.Add($"{item.Name}: {error.Message}");
            }
        }
        return (moved, errors);
    }

    /// <summary>Remove files from the vault for good.</summary>
    public (int Count, List<string> Errors) Delete(IReadOnlyList<VaultItem> items)
    {
        var count = 0;
        var errors = new List<string>();
        foreach (var item in items)
        {
            try
            {
                CloseCopy(item);
                File.Delete(Blob(item.Id));
                Items.Remove(item);
                count++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{item.Name}: {error.Message}");
            }
        }
        SaveIndex();
        return (count, errors);
    }

    /// <summary>Decrypt a working copy into the private folder and return it, ready to open.</summary>
    public string OpenCopy(VaultItem item)
    {
        if (opened.TryGetValue(item.Id, out var existing) && File.Exists(existing.Copy))
            return existing.Copy;
        var folder = Directory.CreateDirectory(Path.Combine(OpenDir, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant())).FullName;
        var target = Path.Combine(folder, item.Name);
        DecryptFile(item, target);
        var info = new FileInfo(target);
        opened[item.Id] = (target, info.LastWriteTimeUtc, info.Length);
        return target;
    }

    /// <summary>
    /// Save edits made to an opened copy back into the vault, then remove the copy. Returns
    /// a message if the copy is still in use (it is then kept until next time).
    /// </summary>
    public string? CloseCopy(VaultItem item)
    {
        if (!opened.TryGetValue(item.Id, out var entry))
            return null;
        if (!File.Exists(entry.Copy))
        {
            opened.Remove(item.Id);
            return null;
        }
        var info = new FileInfo(entry.Copy);
        if (info.LastWriteTimeUtc != entry.Modified || info.Length != entry.Size)
        {
            EncryptFile(entry.Copy, item.Id, null, CancellationToken.None); // edits go back in
            item.Size = info.Length;
            SaveIndex();
            opened[item.Id] = (entry.Copy, info.LastWriteTimeUtc, info.Length);
        }
        try
        {
            Directory.Delete(Path.GetDirectoryName(entry.Copy)!, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Tr.T("{0} is still open in another program; close it and lock the vault again.", item.Name);
        }
        opened.Remove(item.Id);
        return null;
    }

    /// <summary>Before locking: save and remove every opened copy.</summary>
    public List<string> CloseAll()
    {
        var problems = new List<string>();
        foreach (var item in Items.Where(i => opened.ContainsKey(i.Id)).ToList())
        {
            try
            {
                if (CloseCopy(item) is { } problem)
                    problems.Add(problem);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or VaultException)
            {
                problems.Add($"{item.Name}: {error.Message}");
            }
        }
        return problems;
    }

    public void ChangePassword(string oldPassword, string newPassword)
    {
        if (newPassword.Length < MinPassword)
            throw new VaultException(Tr.T("Use a password of at least {0} characters.", MinPassword));
        Unlock(oldPassword); // throws WrongPasswordException
        WriteAtomic(HeaderFile, Wrap(key, newPassword));
    }
}

/// <summary>
/// The scrypt key derivation function (RFC 7914). .NET has no built-in one, and the vault
/// format needs it to stay compatible.
/// </summary>
public static class Scrypt
{
    public static byte[] DeriveKey(byte[] password, byte[] salt, int n, int r, int p, int length)
    {
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("n must be a power of two", nameof(n));
        var blockSize = 128 * r;
        var blocks = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockSize);
        var words = new uint[p * blockSize / 4];
        Buffer.BlockCopy(blocks, 0, words, 0, blocks.Length);
        var memory = new uint[(long)n * 32 * r];
        var work = new uint[32 * r];
        var scratch = new uint[32 * r];
        for (var i = 0; i < p; i++)
        {
            Array.Copy(words, i * 32 * r, work, 0, 32 * r);
            RoMix(work, n, r, memory, scratch);
            Array.Copy(work, 0, words, i * 32 * r, 32 * r);
        }
        Buffer.BlockCopy(words, 0, blocks, 0, blocks.Length);
        return Rfc2898DeriveBytes.Pbkdf2(password, blocks, 1, HashAlgorithmName.SHA256, length);
    }

    private static void RoMix(uint[] x, int n, int r, uint[] v, uint[] scratch)
    {
        var size = 32 * r;
        for (var i = 0; i < n; i++)
        {
            Array.Copy(x, 0, v, (long)i * size, size);
            BlockMix(x, scratch, r);
        }
        for (var i = 0; i < n; i++)
        {
            var j = (long)(x[(2 * r - 1) * 16] & (uint)(n - 1)) * size;
            for (var k = 0; k < size; k++)
                x[k] ^= v[j + k];
            BlockMix(x, scratch, r);
        }
    }

    private static void BlockMix(uint[] b, uint[] y, int r)
    {
        Span<uint> x = stackalloc uint[16];
        b.AsSpan((2 * r - 1) * 16, 16).CopyTo(x);
        for (var i = 0; i < 2 * r; i++)
        {
            for (var k = 0; k < 16; k++)
                x[k] ^= b[i * 16 + k];
            Salsa20_8(x);
            // Even blocks go to the first half, odd blocks to the second.
            x.CopyTo(y.AsSpan((i / 2 + (i % 2) * r) * 16, 16));
        }
        Array.Copy(y, b, 32 * r);
    }

    private static void Salsa20_8(Span<uint> b)
    {
        uint x0 = b[0], x1 = b[1], x2 = b[2], x3 = b[3], x4 = b[4], x5 = b[5], x6 = b[6], x7 = b[7],
             x8 = b[8], x9 = b[9], x10 = b[10], x11 = b[11], x12 = b[12], x13 = b[13], x14 = b[14], x15 = b[15];
        for (var i = 0; i < 4; i++)
        {
            x4 ^= uint.RotateLeft(x0 + x12, 7); x8 ^= uint.RotateLeft(x4 + x0, 9);
            x12 ^= uint.RotateLeft(x8 + x4, 13); x0 ^= uint.RotateLeft(x12 + x8, 18);
            x9 ^= uint.RotateLeft(x5 + x1, 7); x13 ^= uint.RotateLeft(x9 + x5, 9);
            x1 ^= uint.RotateLeft(x13 + x9, 13); x5 ^= uint.RotateLeft(x1 + x13, 18);
            x14 ^= uint.RotateLeft(x10 + x6, 7); x2 ^= uint.RotateLeft(x14 + x10, 9);
            x6 ^= uint.RotateLeft(x2 + x14, 13); x10 ^= uint.RotateLeft(x6 + x2, 18);
            x3 ^= uint.RotateLeft(x15 + x11, 7); x7 ^= uint.RotateLeft(x3 + x15, 9);
            x11 ^= uint.RotateLeft(x7 + x3, 13); x15 ^= uint.RotateLeft(x11 + x7, 18);
            x1 ^= uint.RotateLeft(x0 + x3, 7); x2 ^= uint.RotateLeft(x1 + x0, 9);
            x3 ^= uint.RotateLeft(x2 + x1, 13); x0 ^= uint.RotateLeft(x3 + x2, 18);
            x6 ^= uint.RotateLeft(x5 + x4, 7); x7 ^= uint.RotateLeft(x6 + x5, 9);
            x4 ^= uint.RotateLeft(x7 + x6, 13); x5 ^= uint.RotateLeft(x4 + x7, 18);
            x11 ^= uint.RotateLeft(x10 + x9, 7); x8 ^= uint.RotateLeft(x11 + x10, 9);
            x9 ^= uint.RotateLeft(x8 + x11, 13); x10 ^= uint.RotateLeft(x9 + x8, 18);
            x12 ^= uint.RotateLeft(x15 + x14, 7); x13 ^= uint.RotateLeft(x12 + x15, 9);
            x14 ^= uint.RotateLeft(x13 + x12, 13); x15 ^= uint.RotateLeft(x14 + x13, 18);
        }
        b[0] += x0; b[1] += x1; b[2] += x2; b[3] += x3; b[4] += x4; b[5] += x5; b[6] += x6; b[7] += x7;
        b[8] += x8; b[9] += x9; b[10] += x10; b[11] += x11; b[12] += x12; b[13] += x13; b[14] += x14; b[15] += x15;
    }
}
