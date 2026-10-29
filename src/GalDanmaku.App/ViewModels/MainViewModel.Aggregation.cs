namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool _isAggregationEnabled = true;

    public bool IsAggregationEnabled
    {
        get => _isAggregationEnabled;
        set
        {
            if (!SetProperty(ref _isAggregationEnabled, value)) return;
            UpdateSettings();
        }
    }
}
