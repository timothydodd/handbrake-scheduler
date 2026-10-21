using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace HandbrakeScheduler.Services;

/// <summary>
/// Starts the live dashboard when the host starts and tears it down on shutdown. Registered
/// before any other hosted service so the live region is in place before logs start flowing.
/// </summary>
internal sealed class DashboardHostedService : IHostedService
{
    private readonly DashboardRenderer _renderer;

    public DashboardHostedService(DashboardRenderer renderer)
    {
        _renderer = renderer;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _renderer.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _renderer.StopAsync();
}
