namespace RemotePhone.Agent.Core.Models;

public enum ScreenOrientation
{
    Portrait,
    Landscape,
}

public static class OrientationHelper
{
    public static ScreenOrientation FromSize(int width, int height)
        => height > width ? ScreenOrientation.Portrait : ScreenOrientation.Landscape;
}
