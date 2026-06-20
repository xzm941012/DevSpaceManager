namespace DevSpaceManager.Services;

internal static class NotifyIconFactory
{
    public static Icon Create(bool healthy)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "devspace-manager.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        return SystemIcons.Application;
    }
}
