using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.Engines.Documents.Tests;

/// <summary>A fresh workspace per test plus a one-liner to run an operation by id.</summary>
public abstract class DocumentTestBase : IDisposable
{
    protected DocumentTestBase(string name) => Workspace = new TestWorkspace(name);

    protected TestWorkspace Workspace { get; }

    protected static DocumentOperationProvider Provider { get; } = new();

    protected IOperation Op(string id) => Workspace.Operation(Provider, id);

    protected Task<OperationResult> RunAsync(string id, string input, Dictionary<string, object?>? options = null, CancellationToken cancellationToken = default) =>
        Workspace.RunAsync(Op(id), [input], options, cancellationToken: cancellationToken);

    protected Task<OperationResult> RunAsync(string id, IEnumerable<string> inputs, Dictionary<string, object?>? options = null) =>
        Workspace.RunAsync(Op(id), inputs, options);

    /// <summary>The single output of a result, asserted to exist.</summary>
    protected static string Single(OperationResult result)
    {
        var output = Assert.Single(result.Outputs);
        Assert.True(File.Exists(output) || Directory.Exists(output), $"Output {output} doesn't exist.");
        return output;
    }

    public void Dispose()
    {
        Workspace.Dispose();
        GC.SuppressFinalize(this);
    }
}
