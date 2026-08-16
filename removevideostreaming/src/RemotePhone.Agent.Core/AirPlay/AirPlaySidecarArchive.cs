using System.IO.Compression;
using System.Security.Cryptography;

namespace RemotePhone.Agent.Core.AirPlay;

/// <summary>
/// Verify and unpack the pinned AirPlay-Windows zip. No network I/O.
/// </summary>
public static class AirPlaySidecarArchive
{
    public static bool IsInstalled(AirPlaySidecarSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return File.Exists(spec.ExePath);
    }

    public static void VerifySha256(byte[] archiveBytes, string expectedHex)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHex);

        var actual = Convert.ToHexString(SHA256.HashData(archiveBytes));
        var expected = expectedHex.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"AirPlay sidecar SHA-256 mismatch. Expected {expected}, got {actual}.");
        }
    }

    public static string Extract(byte[] archiveBytes, AirPlaySidecarSpec spec)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentNullException.ThrowIfNull(spec);
        VerifySha256(archiveBytes, spec.Sha256);

        Directory.CreateDirectory(spec.InstallDirectory);
        var staging = Path.Combine(spec.InstallDirectory, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        var zipPath = Path.Combine(staging, "payload.zip");
        try
        {
            File.WriteAllBytes(zipPath, archiveBytes);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            var exe = FindExe(staging, spec.ExeFileName)
                      ?? throw new FileNotFoundException(
                          $"Zip did not contain {spec.ExeFileName}.", spec.ExeFileName);

            var dest = spec.ExePath;
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            CopyDirectoryContents(Path.GetDirectoryName(exe)!, spec.InstallDirectory);
            if (!File.Exists(dest))
            {
                File.Copy(exe, dest, overwrite: true);
            }

            return dest;
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // leftover staging is harmless
            }
        }
    }

    public static string? FindExe(string root, string exeFileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(root, exeFileName, SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            if (rel.Contains(".extract-", StringComparison.OrdinalIgnoreCase) ||
                rel.Equals("payload.zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(destDir, rel);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(file, target, overwrite: true);
        }
    }
}
