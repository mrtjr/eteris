namespace Eteris.Browser.Browser;

public static class NewTabPage
{
    public const string Url = "about:blank";

    public static bool IsNewTab(string address) => address == Url;
}
