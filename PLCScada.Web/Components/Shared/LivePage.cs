using Microsoft.AspNetCore.Components;
using PLCScada.Core;
namespace PLCScada.Web.Components.Shared;
public abstract class LivePage : ComponentBase, IAsyncDisposable
{
    [Inject] protected IScadaStore Store { get; set; } = default!;
    [Inject] protected ITagRuntime Runtime { get; set; } = default!;
    [Inject] protected IDeviceCommunication Communication { get; set; } = default!;
    protected string? LoadError;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    protected override async Task OnInitializedAsync() { await RefreshSafelyAsync(); _loop = LoopAsync(); }
    protected abstract Task RefreshAsync();
    private async Task RefreshSafelyAsync()
    { try { await RefreshAsync(); LoadError = null; } catch (Exception ex) { LoadError = ex.Message; } }
    private async Task LoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_stop.Token))
                await InvokeAsync(async () => { await RefreshSafelyAsync(); StateHasChanged(); });
        }
        catch (OperationCanceledException) { }
    }
    public virtual async ValueTask DisposeAsync()
    { await _stop.CancelAsync(); if (_loop is not null) await _loop; _stop.Dispose(); }
}
