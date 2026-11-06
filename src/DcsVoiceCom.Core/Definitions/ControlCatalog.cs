namespace DcsVoiceCom.Definitions;

/// <summary>All controls available for one aircraft (its modules plus shared ones such as CommonData).</summary>
public sealed class ControlCatalog
{
    private readonly Dictionary<string, ControlDefinition> _byId;

    public ControlCatalog(string aircraft, IReadOnlyList<string> modules, IEnumerable<ControlDefinition> controls)
    {
        Aircraft = aircraft;
        Modules = modules;
        _byId = new Dictionary<string, ControlDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls)
        {
            // First definition wins: aircraft modules are loaded before shared ones.
            _byId.TryAdd(control.Identifier, control);
        }
    }

    public static ControlCatalog Empty { get; } = new("", [], []);

    public string Aircraft { get; }
    public IReadOnlyList<string> Modules { get; }
    public IEnumerable<ControlDefinition> Controls => _byId.Values;
    public int Count => _byId.Count;

    public ControlDefinition? Find(string identifier) => _byId.GetValueOrDefault(identifier);

    public bool Contains(string identifier) => _byId.ContainsKey(identifier);

    /// <summary>Category → controls, sorted, for the control browser.</summary>
    public IReadOnlyList<IGrouping<string, ControlDefinition>> ByCategory() =>
        _byId.Values
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Identifier, StringComparer.OrdinalIgnoreCase)
            .GroupBy(c => c.Category)
            .ToList();
}
