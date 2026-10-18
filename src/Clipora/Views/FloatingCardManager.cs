using System.Windows;
using Clipora.ViewModels;

namespace Clipora.Views;

/// <summary>管理独立小窗：每条记录一个窗口，不设置 Owner，不随主面板隐藏，不持久化。</summary>
public sealed class FloatingCardManager : IDisposable
{
    private readonly Dictionary<long, FloatingCardWindow> _windows = new();
    private bool _disposed;

    public FloatingCardWindow? Open(ClipItemViewModel item, Window anchor)
    {
        if (_disposed || !item.CanFloat) return null;
        if (_windows.TryGetValue(item.Id, out FloatingCardWindow? existing))
        {
            existing.WindowState = WindowState.Normal;
            existing.Activate(); // 只有用户明确再次点击入口时才激活，不在失焦时抢回焦点。
            return existing;
        }

        var window = new FloatingCardWindow(new FloatingCardViewModel(item));
        _windows.Add(item.Id, window);
        window.Closed += (_, _) => _windows.Remove(item.Id);
        try
        {
            window.ShowNear(anchor, _windows.Count - 1);
            return window;
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (FloatingCardWindow window in _windows.Values.ToArray())
            window.Close();
        _windows.Clear();
    }
}
