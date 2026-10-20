namespace PLCScada.Web;

public sealed class PageUiState
{
    public Guid? ManualDeviceId { get; set; }

    public Guid? TagsDeviceId { get; set; }
    public string TagsSearch { get; set; } = "";

    public Guid? RecipesDeviceId { get; set; }
    public string RecipesSearch { get; set; } = "";

    public string DevicesSearch { get; set; } = "";

    public bool AlarmsActiveOnly { get; set; }
    public string AlarmsSearch { get; set; } = "";

    public string LogsSearch { get; set; } = "";

    public Guid? HmiSelectedTagId { get; set; }
    public string HmiSearch { get; set; } = "";
    public int HmiPrecision { get; set; } = 1;
}
