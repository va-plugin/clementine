using Clementine.Core;

namespace Clementine.App.Startup;

/// <summary>
/// Wraps the engine providers so one faulty engine can't stop the app from starting: a provider that throws while
/// creating its operations is skipped, and an operation whose id is already taken is dropped (both are logged).
/// </summary>
public sealed class GuardedProvider : IOperationProvider
{
    readonly IReadOnlyList<IOperationProvider> _providers;
    readonly ILog _log;

    public GuardedProvider(IEnumerable<IOperationProvider> providers, ILog log)
    {
        _providers = providers.ToList();
        _log = log;
    }

    public string Name => "Engines";

    /// <summary>Problems found the last time operations were created (for the About page and logs).</summary>
    public List<string> Problems { get; } = [];

    public IEnumerable<IOperation> CreateOperations(EngineContext context)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IOperation>();
        Problems.Clear();
        foreach (var provider in _providers)
        {
            List<IOperation> operations;
            try
            {
                operations = provider.CreateOperations(context).ToList();
            }
            catch (Exception ex)
            {
                Report($"The {provider.Name} engine failed to load and was skipped.", ex);
                continue;
            }

            foreach (var operation in operations)
            {
                if (ids.Add(operation.Info.Id))
                    result.Add(operation);
                else
                    Report($"Duplicate operation id '{operation.Info.Id}' from {provider.Name} was ignored.", null);
            }
        }
        return result;
    }

    /// <summary>Adds operations from <paramref name="extra"/> that don't collide with ids already in the catalog.</summary>
    public static IOperationProvider Deduplicated(IOperationProvider extra, OperationCatalog catalog, ILog log) =>
        new FilteredProvider(extra, op => catalog.Find(op.Info.Id) is null, log);

    void Report(string message, Exception? exception)
    {
        Problems.Add(message);
        _log.Error(message, exception);
    }

    sealed class FilteredProvider(IOperationProvider inner, Func<IOperation, bool> keep, ILog log) : IOperationProvider
    {
        public string Name => inner.Name;

        public IEnumerable<IOperation> CreateOperations(EngineContext context)
        {
            foreach (var operation in inner.CreateOperations(context))
            {
                if (keep(operation))
                    yield return operation;
                else
                    log.Error($"Duplicate operation id '{operation.Info.Id}' from {inner.Name} was ignored.");
            }
        }
    }
}
