using PLCScada.Core;

namespace PLCScada.Application;

// 配方列表筛选：设备下拉与文本搜索协同（名称/说明/设备名/Tag 名均可命中）。
public static class RecipeListFilter
{
    public static bool Matches(Recipe recipe, Guid? deviceId, string? search, string deviceName)
    {
        if (deviceId is Guid selected && recipe.DeviceId != selected)
            return false;
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return recipe.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               recipe.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               recipe.Parameters.Any(parameter =>
                   parameter.Tag?.Name.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }
}
