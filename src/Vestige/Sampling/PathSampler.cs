using System.Text.RegularExpressions;

namespace Vestige.Sampling;

/// <summary>Keeps (or rate-samples) events matching <c>http.path</c> wildcard patterns.</summary>
internal sealed class PathSampler : ISamplingStrategy
{
    private readonly Regex[] _patterns;
    private readonly double _rate;

    public PathSampler(IEnumerable<string> patterns, double rate)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        _patterns = patterns
            .Select(p => new Regex(
                WildcardToRegex(p),
                // CultureInvariant matters: without it, IgnoreCase follows the ambient culture and
                // a Turkish locale stops matching paths containing "I"/"i".
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
            .ToArray();

        _rate = Math.Clamp(rate, 0.0, 1.0);
    }

    public SamplingDecision Evaluate(WideEvent ev)
    {
        var path = ev.Get<string>("http.path");
        if (path is null)
            return SamplingDecision.Defer;

        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(path))
                return Random.Shared.NextDouble() < _rate ? SamplingDecision.Keep : SamplingDecision.Drop;
        }

        return SamplingDecision.Defer;
    }

    private static string WildcardToRegex(string pattern)
        => $"^{Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".")}$";
}
