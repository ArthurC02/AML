using System.Reflection;

namespace Aml.Tests;

/// <summary>
/// The test pyramid by namespace: <c>Aml.Tests.E2E</c> drives the API, <c>Aml.Tests.Integration</c> runs services and
/// repositories on SQLite, everything else (Unit, GoldenCases) runs in process. Each layer must stay well below the one under it.
/// </summary>
public class TestPyramidTests
{
    // ponytail: the ratios are a guard, not a target; raise them if a layer starts to grow out of shape.
    private const int UnitPerIntegration = 3;
    private const int IntegrationPerE2E = 2;

    [Fact]
    public void Each_layer_has_fewer_tests_than_the_one_below_it()
    {
        var layers = typeof(TestPyramidTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods().Where(m => m.GetCustomAttribute<FactAttribute>() is not null).Select(_ => Layer(t.Namespace)))
            .GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        var (unit, integration, e2e) = (layers.GetValueOrDefault("Unit"), layers.GetValueOrDefault("Integration"), layers.GetValueOrDefault("E2E"));
        TestContext.Current.SendDiagnosticMessage($"Unit {unit}, Integration {integration}, E2E {e2e}");

        Assert.True(e2e > 0 && integration >= IntegrationPerE2E * e2e, $"Integration {integration} vs E2E {e2e}");
        Assert.True(unit >= UnitPerIntegration * integration, $"Unit {unit} vs Integration {integration}");
    }

    private static string Layer(string? ns) => ns switch
    {
        "Aml.Tests.E2E" => "E2E",
        "Aml.Tests.Integration" => "Integration",
        _ => "Unit",
    };
}
