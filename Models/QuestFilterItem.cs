using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TarkovTracker.Models;

public class QuestFilterItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _trader = string.Empty;
    private bool _isSelected;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;

            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string Trader
    {
        get => _trader;
        set
        {
            if (_trader == value)
                return;

            _trader = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Trader)
        ? Name
        : $"{Name} ({Trader})";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
