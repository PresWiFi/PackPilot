using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace PackPilot.Core;

/// <summary>
/// Builds and verifies .intunewin packages without Microsoft's IntuneWinAppUtil.exe.
///
/// Format (same as IntuneWinAppUtil 1.8.x):
///   .intunewin = zip {
///     IntuneWinPackage/Metadata/Detection.xml           (plain text, holds the keys)
///     IntuneWinPackage/Contents/IntunePackage.intunewin  (encrypted payload)
///   }
///   encrypted payload = [HMAC-SHA256: 32 bytes][IV: 16 bytes][AES-256-CBC(payload.zip), PKCS7]
///   HMAC covers IV + ciphertext. FileDigest is SHA-256 of the unencrypted payload.zip.
///   payload.zip = every file in the source folder, paths relative to it.
/// </summary>
public static class IntuneWin
{
    const string XmlEntry = "IntuneWinPackage/Metadata/Detection.xml";
    const string EncEntry = "IntuneWinPackage/Contents/IntunePackage.intunewin";
    const int BufferSize = 1 << 20;

    public static IntuneWinInfo Build(string sourceDir, string setupFile, string outputFile, string appName,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(sourceDir, setupFile)))
            throw new FileNotFoundException($"Setup file {setupFile} is not in {sourceDir}.");

        var tmp = Path.Combine(Path.GetTempPath(), "PackPilot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // 1. Zip the source folder.
            log?.Invoke("Zipping the source folder...");
            var payload = Path.Combine(tmp, "payload.zip");
            ZipFile.CreateFromDirectory(sourceDir, payload, CompressionLevel.Optimal, includeBaseDirectory: false);
            ct.ThrowIfCancellationRequested();
            long payloadSize = new FileInfo(payload).Length;

            // 2. Encrypt it: placeholder HMAC, IV, ciphertext. Hash the plaintext on the way through.
            log?.Invoke($"Encrypting {Util.FormatSize(payloadSize)} with AES-256-CBC...");
            var key = RandomNumberGenerator.GetBytes(32);
            var macKey = RandomNumberGenerator.GetBytes(32);
            var iv = RandomNumberGenerator.GetBytes(16);
            var enc = Path.Combine(tmp, "IntunePackage.intunewin");
            byte[] digest;
            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using var outFs = new FileStream(enc, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize);
                outFs.Write(new byte[32]);
                outFs.Write(iv);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var crypto = new CryptoStream(outFs, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
                using (var inFs = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                {
                    var buf = new byte[BufferSize];
                    int n;
                    while ((n = inFs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buf, 0, n);
                        crypto.Write(buf, 0, n);
                    }
                    crypto.FlushFinalBlock();
                }
                digest = sha.GetHashAndReset();

                // 3. HMAC over IV + ciphertext, written into the placeholder.
                outFs.Position = 32;
                byte[] mac;
                using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(outFs);
                outFs.Position = 0;
                outFs.Write(mac);
                outFs.Flush();

                // 4. Detection.xml + outer zip.
                var xml = DetectionXml(appName, setupFile, payloadSize, key, macKey, iv, mac, digest);
                log?.Invoke("Writing the .intunewin...");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
                if (File.Exists(outputFile)) File.Delete(outputFile);
                outFs.Position = 0;
                using var zipFs = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                using var zip = new ZipArchive(zipFs, ZipArchiveMode.Create);
                using (var s = zip.CreateEntry(XmlEntry, CompressionLevel.NoCompression).Open())
                    s.Write(Encoding.UTF8.GetBytes(xml));
                using (var s = zip.CreateEntry(EncEntry, CompressionLevel.NoCompression).Open())
                    outFs.CopyTo(s, BufferSize);
            }

            log?.Invoke("Package written. Verifying it the way Intune reads it...");
            var info = Verify(outputFile, setupFile, ct);
            info.BuiltAt = DateTime.Now;
            return info;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp clean-up only */ }
        }
    }

    static string DetectionXml(string appName, string setupFile, long size, byte[] key, byte[] macKey, byte[] iv, byte[] mac, byte[] digest)
    {
        string E(string s) => SecurityElement.Escape(s) ?? "";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ApplicationInfo xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" ToolVersion="1.8.6.0">
              <Name>{E(appName)}</Name>
              <UnencryptedContentSize>{size}</UnencryptedContentSize>
              <FileName>IntunePackage.intunewin</FileName>
              <SetupFile>{E(setupFile)}</SetupFile>
              <EncryptionInfo>
                <EncryptionKey>{Convert.ToBase64String(key)}</EncryptionKey>
                <MacKey>{Convert.ToBase64String(macKey)}</MacKey>
                <InitializationVector>{Convert.ToBase64String(iv)}</InitializationVector>
                <Mac>{Convert.ToBase64String(mac)}</Mac>
                <ProfileIdentifier>ProfileVersion1</ProfileIdentifier>
                <FileDigest>{Convert.ToBase64String(digest)}</FileDigest>
                <FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
              </EncryptionInfo>
            </ApplicationInfo>
            """.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>
    /// Re-opens a package and checks everything Intune checks: both entries present, HMAC, decryption,
    /// size, SHA-256 digest, and that the payload is a readable zip holding the setup file.
    /// Optionally extracts the payload to <paramref name="extractTo"/>.
    /// </summary>
    public static IntuneWinInfo Verify(string packagePath, string? expectSetupFile = null, CancellationToken ct = default, string? extractTo = null)
    {
        var info = new IntuneWinInfo { PackagePath = Path.GetFullPath(packagePath), PackageSize = new FileInfo(packagePath).Length };
        info.PackageSha256 = Util.Sha256File(packagePath);
        var tmp = Path.Combine(Path.GetTempPath(), "PackPilot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            XElement ei; long declaredSize;
            var enc = Path.Combine(tmp, "enc.bin");
            using (var zip = ZipFile.OpenRead(packagePath))
            {
                var xmlE = zip.GetEntry(XmlEntry) ?? throw new InvalidDataException("Detection.xml is missing.");
                var encE = zip.GetEntry(EncEntry) ?? throw new InvalidDataException("The encrypted content is missing.");
                XDocument doc;
                using (var s = xmlE.Open()) doc = XDocument.Load(s);
                var root = doc.Root!;
                ei = root.Element("EncryptionInfo") ?? throw new InvalidDataException("Detection.xml has no EncryptionInfo.");
                declaredSize = long.Parse(root.Element("UnencryptedContentSize")!.Value);
                var setup = root.Element("SetupFile")?.Value ?? "";
                info.VerifyChecks.Add($"Detection.xml present (app \"{root.Element("Name")?.Value}\", setup file {setup})");
                if (expectSetupFile != null && !string.Equals(setup, expectSetupFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Detection.xml names setup file {setup}, expected {expectSetupFile}.");
                encE.ExtractToFile(enc, true);
            }

            byte[] B(string name) => Convert.FromBase64String(ei.Element(name)!.Value);
            var key = B("EncryptionKey"); var macKey = B("MacKey"); var iv = B("InitializationVector");
            var mac = B("Mac"); var digest = B("FileDigest");

            using (var fs = new FileStream(enc, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                var stored = new byte[32];
                fs.ReadExactly(stored);
                var storedIv = new byte[16];
                fs.ReadExactly(storedIv);
                fs.Position = 32;
                byte[] computed;
                using (var h = new HMACSHA256(macKey)) computed = h.ComputeHash(fs);
                if (!CryptographicOperations.FixedTimeEquals(computed, stored) || !CryptographicOperations.FixedTimeEquals(computed, mac))
                    throw new InvalidDataException("HMAC does not match: the package is damaged.");
                if (!storedIv.SequenceEqual(iv)) throw new InvalidDataException("IV in the content does not match Detection.xml.");
                info.VerifyChecks.Add("HMAC-SHA256 matches (content not tampered with)");
                ct.ThrowIfCancellationRequested();

                var payload = Path.Combine(tmp, "payload.zip");
                fs.Position = 48;
                using (var aes = Aes.Create())
                {
                    aes.Key = key; aes.IV = iv;
                    using var crypto = new CryptoStream(fs, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
                    using var outFs = new FileStream(payload, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                    crypto.CopyTo(outFs, BufferSize);
                }
                info.VerifyChecks.Add("AES-256-CBC decryption succeeded");

                long size = new FileInfo(payload).Length;
                if (size != declaredSize) throw new InvalidDataException($"Decrypted size {size} does not match UnencryptedContentSize {declaredSize}.");
                info.UnencryptedContentSize = size;
                info.VerifyChecks.Add($"Decrypted size matches ({Util.FormatSize(size)})");

                byte[] sha;
                using (var p = File.OpenRead(payload)) sha = SHA256.HashData(p);
                if (!sha.SequenceEqual(digest)) throw new InvalidDataException("SHA-256 of the content does not match FileDigest.");
                info.FileDigest = Convert.ToBase64String(digest);
                info.VerifyChecks.Add("SHA-256 file digest matches");

                using (var pz = ZipFile.OpenRead(payload))
                {
                    info.Files = pz.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => $"{e.FullName} ({Util.FormatSize(e.Length)})").ToList();
                    if (expectSetupFile != null && pz.GetEntry(expectSetupFile) == null)
                        throw new InvalidDataException($"The content does not contain {expectSetupFile}.");
                }
                info.VerifyChecks.Add($"Content zip opens: {info.Files.Count} file(s)");
                if (extractTo != null) ZipFile.ExtractToDirectory(payload, extractTo, true);
            }
            info.Verified = true;
            return info;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp clean-up only */ }
        }
    }
}
