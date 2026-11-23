using System.Xml.Linq;
using Xunit;

namespace PiAgent.Architecture.Tests;

/// <summary>
/// Day-one contour: the Host Monitor and Build Worker hosts must not have any
/// database access compiled in — they reference Platform only (user-approved boundary).
/// Guard test: verifies a boundary the implementation already honors.
/// </summary>
public class ProjectBoundaryTests
{
    [Theory]
    [InlineData("../../../../../Hosts/HostMonitor/PiAgent.HostMonitor.csproj")]
    [InlineData("../../../../../Hosts/BuildWorker/PiAgent.BuildWorker.csproj")]
    public void NonDbHosts_HaveNoEfCoreOrNpgsqlOrInfrastructureReferences(string projectPath)
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, projectPath));

        var references = project
            .Descendants(project.Root!.Name.Namespace + "ProjectReference")
            .Concat(project.Descendants(project.Root.Name.Namespace + "PackageReference"))
            .Select(r => (string?)r.Attribute("Include"))
            .ToList();

        Assert.DoesNotContain(references, r => r!.Contains("PiAgent.Infrastructure"));
        Assert.DoesNotContain(references, r => r!.Contains("EntityFrameworkCore"));
        Assert.DoesNotContain(references, r => r!.Contains("Npgsql"));
    }
}
