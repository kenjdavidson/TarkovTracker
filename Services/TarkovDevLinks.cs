using System.Text;

namespace TarkovTracker.Services;

public static class TarkovDevLinks
{
    public static string BuildTaskUrlFromSlug(string slug)
    {
        string cleaned = ToTaskSlug(slug);
        return string.IsNullOrWhiteSpace(cleaned)
            ? "https://tarkov.dev/tasks"
            : $"https://tarkov.dev/task/{cleaned}";
    }

    public static string BuildTaskUrl(string questName)
    {
        return BuildTaskUrlFromSlug(questName);
    }

    public static string BuildWikiUrl(string questName)
    {
        if (string.IsNullOrWhiteSpace(questName))
            return "https://escapefromtarkov.fandom.com/wiki/Quests";

        string wikiTitle = questName.Trim().Replace(' ', '_');
        return "https://escapefromtarkov.fandom.com/wiki/" + Uri.EscapeDataString(wikiTitle);
    }

    public static string ToTaskSlug(string questName)
    {
        if (string.IsNullOrWhiteSpace(questName))
            return string.Empty;

        var sb = new StringBuilder(questName.Length);
        bool lastWasHyphen = false;

        foreach (char c in questName.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        return sb.ToString().Trim('-');
    }
}
