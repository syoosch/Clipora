using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Clipora.ViewModels;

/// <summary>隐私页"应用排除"名单中的单行 ViewModel。</summary>
public sealed class ExcludedAppViewModel : INotifyPropertyChanged
{
    private string _processName;
    private string _displayName;

    /// <summary>归一化进程名（lowercase、无扩展名），持久化键。</summary>
    public string ProcessName
    {
        get => _processName;
        set { _processName = value; OnPropertyChanged(); }
    }

    /// <summary>UI 显示名（当前运行则取友好名，否则直接显示进程名）。</summary>
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    public ExcludedAppViewModel(string processName, string displayName)
    {
        _processName = processName;
        _displayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
