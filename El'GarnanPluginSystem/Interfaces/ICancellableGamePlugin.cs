using El_Garnan_Plugin_Loader.Models;
using WebLaunch.Core;
namespace El_Garnan_Plugin_Loader.Interfaces;

// Optional extension: the existing IGamePlugin ABI remains unchanged.
public interface ICancellableGamePlugin
{
    Task<bool> LaunchAsync(GameLaunchParameters parameters, IProgress<LaunchStatus> progress, CancellationToken cancellationToken);
}
