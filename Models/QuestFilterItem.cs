using System.ComponentModel;
using System.Runtime.CompilerServices;

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
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public string Trader
    {
        get => _trader;
        set
        {
            if (_trader != value)
            {
                _trader = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }
    
    public string DisplayName => $"{Name} ({Trader})";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}