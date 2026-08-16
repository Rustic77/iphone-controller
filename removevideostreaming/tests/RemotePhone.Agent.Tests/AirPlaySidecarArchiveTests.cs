using System.IO.Compression;
using System.Text;
using FluentAssertions;
using RemotePhone.Agent.Core.AirPlay;

namespace RemotePhone.Agent.Tests;

public class AirPlaySidecarArchiveTests
{
    [Fact]
    public void VerifySha256_rejects_mismatch()
    {
        var bytes = Encoding.UTF8.GetBytes("not-a-zip");
        var act = () => AirPlaySidecarArchive.VerifySha256(bytes, AirPlaySidecarSpec.DefaultSha256);
        act.Should().Throw<InvalidDataException>().WithMessage("*mismatch*");
    }

    [Fact]
    public void Extract_installs_exe_from_nested_zip()
    {
        using var temp = new TempDir();
        var spec = new AirPlaySidecarSpec
        {
            InstallDirectory = Path.Combine(temp.Path, "install"),
            Sha256 = string.Empty,
        };

        var zipBytes = CreateZipWithNestedExe(spec.ExeFileName);
        spec = new AirPlaySidecarSpec
        {
            InstallDirectory = spec.InstallDirectory,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes)),
            ExeFileName = spec.ExeFileName,
        };

        var exe = AirPlaySidecarArchive.Extract(zipBytes, spec);
        File.Exists(exe).Should().BeTrue();
        AirPlaySidecarArchive.IsInstalled(spec).Should().BeTrue();
        File.ReadAllText(exe).Should().Be("sidecar-ok");
    }

    [Fact]
    public void FindExe_returns_null_when_missing()
    {
        using var temp = new TempDir();
        AirPlaySidecarArchive.FindExe(temp.Path, "airplay-windows.exe").Should().BeNull();
    }

    private static byte[] CreateZipWithNestedExe(string exeName)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry($"payload/{exeName}");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes("sidecar-ok"));
        }

        return ms.ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rp-airplay-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
