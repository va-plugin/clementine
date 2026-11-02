using Clementine.TestSupport;

namespace Clementine.Engines.Documents.Tests;

/// <summary>Shared rules every provider must satisfy (see tests/Clementine.TestSupport/ProviderContract.cs). Do not delete.</summary>
public class ProviderContractTests
{
    [Fact]
    public void Provider_satisfies_the_operation_contract()
    {
        using var workspace = new TestWorkspace("contract");
        var problems = ProviderContract.Validate(new DocumentOperationProvider(), workspace.Context);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
