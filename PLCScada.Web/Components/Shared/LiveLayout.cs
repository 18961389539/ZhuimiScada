using Microsoft.AspNetCore.Components;
using PLCScada.Core;
namespace PLCScada.Web.Components.Shared;
public abstract class LiveLayout : LayoutComponentBase, IDisposable
{
    [Inject] private IDeviceCommunication Communication { get; set; } = default!;
    protected int Online => Communication.GetSnapshots().Count(x => x.Status == ConnectionStatus.Online);
    private readonly CancellationTokenSource _stop = new();
    protected override void OnInitialized() => _ = TickAsync();
    private async Task TickAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_stop.Token)) await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { _stop.Cancel(); }
}
