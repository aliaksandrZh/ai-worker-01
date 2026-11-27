using PiAgent.RunWorker.Docker;

namespace PiAgent.RunWorker.Tests;

/// <summary>Records the provisioning calls the consumer makes; never talks to Docker.</summary>
public sealed class RecordingProvisioner : IContainerProvisioner
{
    public ContainerPlan? LastPlan { get; private set; }
    public int StopCalls { get; private set; }
    public bool ImageExists { get; set; } = true;
    public int ImageChecks { get; private set; }
    public string? LastCheckedImage { get; private set; }

    /// <summary>The snapshot the next GetContainerStateAsync returns (watchdog tests).</summary>
    public ContainerSnapshot NextSnapshot { get; set; } = new("running", null, null, null);

    public Task<string?> CreateContainerAsync(ContainerPlan plan, CancellationToken cancellationToken)
    {
        LastPlan = plan;
        return Task.FromResult<string?>("container-1");
    }

    public Task<bool> StartAndWaitRunningAsync(string container, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<ContainerSnapshot> GetContainerStateAsync(string container, CancellationToken cancellationToken) =>
        Task.FromResult(NextSnapshot);

    public Task<bool> StopAsync(string container, CancellationToken cancellationToken)
    {
        StopCalls++;
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(string container, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        ImageChecks++;
        LastCheckedImage = image;
        return Task.FromResult(ImageExists);
    }
}
