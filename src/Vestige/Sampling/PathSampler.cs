using System.Text.RegularExpressions;

namespace Vestige.Sampling;

/// <summary>Keeps (or rate-samples) events matching <c>http.path</c> wildcard patterns.</summary>
internal sealed class PathSampler : ISamplingStrategy
{
    private readonly IReadOnlyList<Regex> _patterns;
    private readonly double _rate;
    private static readonly ThreadLocal<Random> s_random = new(() => new Random());

    public PathSampler(IEnumerable<string> patterns, double rate)
    {
        _patterns = patterns
            .Select(p => new Regex(WildcardToRegex(p), RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
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
                return s_random.Value!.NextDouble() < _rate ? SamplingDecision.Keep : SamplingDecision.Drop;
        }

        return SamplingDecision.Defer;
    }

    private static string WildcardToRegex(string pattern)
        => $"^{Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".")}$";
}
