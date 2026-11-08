using System.Text.RegularExpressions;

namespace DcsVoiceCom.DcsHook;

/// <summary>
/// A server profile's <c>servers</c> entry: a server name or address, ignoring case and outer spaces.
/// <c>*</c> stands for any text and works anywhere: <c>ShadowReapers*</c>, <c>*Reapers*</c>, <c>*PvE</c>.
/// Without <c>*</c> the whole name (or address) must match.
/// </summary>
public static class ServerPattern
{
    public static bool Matches(string pattern, DcsSession? session)
    {
        if (session is null || !session.OnServer) return false;
        pattern = pattern.Trim();
        if (pattern.Length == 0 || pattern.All(c => c == '*')) return false; // "*" alone would match every server
        var regex = new Regex("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return (session.Server.Length > 0 && regex.IsMatch(session.Server.Trim())) ||
               (session.Address.Length > 0 && regex.IsMatch(session.Address.Trim()));
    }

    public static bool MatchesAny(IEnumerable<string>? patterns, DcsSession? session) =>
        patterns?.Any(p => Matches(p, session)) == true;
}
