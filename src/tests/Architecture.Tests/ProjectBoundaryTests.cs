using System.Xml.Linq;
using Xunit;

namespace PiAgent.Architecture.Tests;

/// <summary>
/// The event-driven contour (ADR-0002..0006): every module talks to the world through the
/// EventLog seam — no module references another module's internals, and no module carries
/// EF Core. The database is Postgres via raw SQL owned by each module's store.
/// </summary>
public class ProjectBoundaryTests
{
    public static TheoryData<string> Modules => new()
    {
        "Events/PiAgent.Events.csproj",
        "Orchestration/PiAgent.Orchestration.csproj",
        "GithubSync/PiAgent.GithubSync.csproj",
        "Publisher/PiAgent.Publisher.csproj",
        "RunWorker/PiAgent.RunWorker.csproj",
        "Hosts/AppHost/PiAgent.AppHost.csproj",
    };

    [Theory]
    [MemberData(nameof(Modules))]
    public void Modules_HaveNoEfCoreReferences(string projectPath)
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "../../../../../", projectPath));

        var packages = project
            .Descendants(project.Root!.Name.Namespace + "PackageReference")
            .Select(r => (string?)r.Attribute("Include"))
            .ToList();

        Assert.DoesNotContain(packages, p => p!.Contains("EntityFrameworkCore"));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Modules_DoNotReferenceEachOthersInternals(string projectPath)
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "../../../../../", projectPath));

        var references = project
            .Descendants(project.Root!.Name.Namespace + "ProjectReference")
            .Select(r => r.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(r => r.Replace('\\', '/'))
            .ToList();

        // AppHost wires everything; the modules must not reach sideways
        if (projectPath.Contains("AppHost"))
        {
            Assert.DoesNotContain(references, r => r!.Contains("Hosts/"));
            return;
        }

        Assert.DoesNotContain(references, r => r!.Contains("Orchestration/") && !projectPath.Contains("Orchestration"));
        Assert.DoesNotContain(references, r => r!.Contains("GithubSync/") && !projectPath.Contains("GithubSync"));
        Assert.DoesNotContain(references, r => r!.Contains("Publisher/") && !projectPath.Contains("Publisher"));
        Assert.DoesNotContain(references, r => r!.Contains("RunWorker/") && !projectPath.Contains("RunWorker"));
        Assert.DoesNotContain(references, r => r!.Contains("Hosts/"));
    }
}
