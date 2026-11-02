namespace Clementine.Core;

/// <summary>Supplies operations. Each engine assembly exposes exactly one provider.</summary>
public interface IOperationProvider
{
    string Name { get; }

    IEnumerable<IOperation> CreateOperations(EngineContext context);
}

/// <summary>A wheel segment: operations sharing <see cref="OperationInfo.Group"/>, best first.</summary>
public sealed record OperationGroup(string Name, IReadOnlyList<IOperation> Operations)
{
    /// <summary>What a drop on the inner segment runs.</summary>
    public IOperation Default => Operations[0];

    public bool HasVariants => Operations.Count > 1;

    public string? IconKey => Operations.Select(o => o.Info.IconKey).FirstOrDefault(k => k is not null);

    public int Order => Operations.Min(o => o.Info.Order);
}

/// <summary>Every operation from every provider, plus the queries the wheel and CLI need.</summary>
public sealed class OperationCatalog
{
    readonly List<IOperation> _operations = [];
    readonly Dictionary<string, IOperation> _byId = new(StringComparer.OrdinalIgnoreCase);

    public OperationCatalog(EngineContext context, IEnumerable<IOperationProvider> providers)
    {
        Context = context;
        foreach (var provider in providers)
            Add(provider);
    }

    public EngineContext Context { get; }

    public IReadOnlyList<IOperation> All => _operations;

    /// <summary>Adds a provider's operations. A failing provider or a duplicate id is logged and skipped, never fatal.</summary>
    public void Add(IOperationProvider provider)
    {
        IReadOnlyList<IOperation> operations;
        try
        {
            operations = provider.CreateOperations(Context).ToList();
        }
        catch (Exception ex)
        {
            Context.Log.Error($"Provider {provider.Name} failed to create its operations; they are unavailable.", ex);
            return;
        }

        foreach (var operation in operations)
        {
            if (!_byId.TryAdd(operation.Info.Id, operation))
            {
                Context.Log.Error($"Duplicate operation id '{operation.Info.Id}' from provider {provider.Name}; skipped.");
                continue;
            }
            _operations.Add(operation);
        }
    }

    public IOperation? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Operations that accept the whole selection, optionally of one category, sorted for display.</summary>
    public IReadOnlyList<IOperation> ForInputs(IReadOnlyList<InputFile> files, OperationCategory? category = null) =>
        _operations
            .Where(o => category is null || o.Info.Category == category)
            .Where(o => SafeAcceptsAll(o, files))
            .OrderBy(o => o.Info.Order)
            .ThenBy(o => o.Info.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Groups operations into wheel segments, preserving the given order inside each group.</summary>
    public static IReadOnlyList<OperationGroup> Group(IEnumerable<IOperation> operations) =>
        operations
            .GroupBy(o => o.Info.Group, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new OperationGroup(g.First().Info.Group, g.ToList()))
            .OrderBy(g => g.Order)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    bool SafeAcceptsAll(IOperation operation, IReadOnlyList<InputFile> files)
    {
        try
        {
            return operation.AcceptsAll(files);
        }
        catch (Exception ex)
        {
            Context.Log.Warn($"{operation.Info.Id}.AcceptsAll threw", ex);
            return false;
        }
    }
}
