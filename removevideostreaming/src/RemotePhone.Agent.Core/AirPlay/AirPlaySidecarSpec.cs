namespace RemotePhone.Agent.Core.AirPlay;

/// <summary>
/// Pinned lab AirPlay receiver (UxPlay port). GPLv3 — see THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class AirPlaySidecarSpec
{
    public const string DefaultDownloadUrl =
        "https://github.com/moieric11/AirPlay-Windows/releases/download/v0.1.0/airplay-windows-v0.1.0-x64.zip";

    public const string DefaultSha256 = "e9350ca262ceb3967bda817d09a8d28b45327ec020fb6049cf6453097cfd8bab";

    public const string DefaultExeFileName = "airplay-windows.exe";

    public const string DefaultArguments = "--log --mirror-res 1170x2532";

    public string DownloadUrl { get; init; } = DefaultDownloadUrl;

    public string Sha256 { get; init; } = DefaultSha256;

    public string ExeFileName { get; init; } = DefaultExeFileName;

    public string Arguments { get; init; } = DefaultArguments;

    public string InstallDirectory { get; init; } = DefaultInstallDirectory();

    public string ExePath => Path.Combine(InstallDirectory, ExeFileName);

    public static string DefaultInstallDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "RemotePhone", "airplay-windows");
    }
}
