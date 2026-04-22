using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ExecutePrograms.Models;

public class ExtraProgram : INotifyPropertyChanged
{
    private string _displayName  = "";
    private string _exePath      = "";
    private bool   _showWindow   = true;
    private bool   _startEnabled = true;
    private bool   _stopEnabled  = true;

    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    public string ExePath
    {
        get => _exePath;
        set { _exePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(FileName)); }
    }

    public bool ShowWindow
    {
        get => _showWindow;
        set { _showWindow = value; OnPropertyChanged(); }
    }

    public bool StartEnabled
    {
        get => _startEnabled;
        set { _startEnabled = value; OnPropertyChanged(); }
    }

    public bool StopEnabled
    {
        get => _stopEnabled;
        set { _stopEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>바인딩 전용 — 저장하지 않음</summary>
    [JsonIgnore]
    public string FileName => Path.GetFileName(_exePath);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
