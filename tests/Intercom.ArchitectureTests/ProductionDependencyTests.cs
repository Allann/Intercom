using Intercom.App.AttentionCards;
using Intercom.Identity;
using NetArchTest.Rules;

namespace Intercom.ArchitectureTests;

public sealed class ProductionDependencyTests
{
    [Fact]
    public void CoreDoesNotDependOnOuterAppModules()
    {
        var result = Types.InAssembly(typeof(IdentityStore).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("Intercom.App", "Intercom.App.Runtime")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Intercom.Core must not depend on an outer app module. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void AppRuntimeDoesNotDependOnOuterApp()
    {
        var references = typeof(AttentionCardToastObservation).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("Intercom.App", references);
    }

    [Fact]
    public void AppRuntimeAttentionCardDecisionDependsInwardOnCore()
    {
        var result = Types.InAssembly(typeof(AttentionCardToastObservation).Assembly)
            .That()
            .HaveName(nameof(AttentionCardToastObservation))
            .Should()
            .HaveDependencyOn("Intercom.AttentionCards")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "The runtime attention-card decision must depend inward on Intercom.Core.");
    }
}
