using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PowerMIDI.ViewModels;

// Base of the view models: raises PropertyChanged so that the bound XAML updates itself.
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Sets the field and notifies; returns false if the value did not change.
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
