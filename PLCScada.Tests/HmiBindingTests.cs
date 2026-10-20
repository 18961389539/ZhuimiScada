using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using PLCScada.Core;
using PLCScada.Web.Components.Shared;

namespace PLCScada.Tests;

public sealed class HmiBindingTests
{
    [Fact]
    public async Task TextDisplaySubscribesByTagIdAndRendersValueQualityAndUnit()
    {
        await using var system = await TestSystem.CreateAsync(); await system.Devices.SaveAsync(system.Device);
        var tag = new Tag { Name = "HMI.Pressure", DeviceId = system.Device.Id, Address = 10, Unit = "kPa" };
        await system.Tags.SaveAsync(tag);
        await using var renderer = new HtmlRenderer(system.Services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var view = await renderer.RenderComponentAsync<TagTile>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(TagTile.TagId)] = tag.Id, [nameof(TagTile.Name)] = tag.Name, [nameof(TagTile.Unit)] = tag.Unit, [nameof(TagTile.Precision)] = 2 }));
            Assert.Contains("HMI.Pressure", view.ToHtmlString()); Assert.Contains("Uncertain", view.ToHtmlString());
            await system.Runtime.GetTagValueAsync(tag.Id);
            var online = view.ToHtmlString();
            Assert.Contains("1234.00", online); Assert.Contains("kPa", online); Assert.Contains("Good", online);
            await system.Communication.DisconnectAsync(system.Device.Id); await system.Runtime.GetTagValueAsync(tag.Id);
            var offline = view.ToHtmlString();
            Assert.Contains("Bad", offline); Assert.Contains("opacity:.45", offline);
        });
    }
}
