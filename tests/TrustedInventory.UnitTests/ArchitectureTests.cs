using System.Xml.Linq;

namespace TrustedInventory.UnitTests;

/// <summary>Layer rules from section 04, checked on the project graph (the compiler drops unused assembly references).</summary>
public class ArchitectureTests
{
    [Theory]
    [Trait("Requirement", "ARC-002")]
    [InlineData("TrustedInventory.Domain", new string[0])]
    [InlineData("TrustedInventory.Application", new[] { "TrustedInventory.Domain" })]
    [InlineData("TrustedInventory.Infrastructure", new[] { "TrustedInventory.Application", "TrustedInventory.Domain" })]
    [InlineData("TrustedInventory.Api", new[] { "TrustedInventory.Application", "TrustedInventory.Infrastructure" })]
    [InlineData("TrustedInventory.Worker", new[] { "TrustedInventory.Application", "TrustedInventory.Infrastructure" })]
    public void Project_references_follow_layering(string project, string[] expected)
    {
        Assert.Equal(expected, ProjectReferences(project));
    }

    private static string[] ProjectReferences(string project)
    {
        var path = Path.Combine(RepositoryRoot(), "src", project, project + ".csproj");
        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TrustedInventory.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TrustedInventory.slnx not found above test output.");
    }
}
