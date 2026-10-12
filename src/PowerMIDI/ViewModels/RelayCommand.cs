using System.Windows.Input;

namespace PowerMIDI.ViewModels;

// Command bound in XAML (right click, mouse wheel…) that runs a view model action.
public sealed class RelayCommand : ICommand
{
    readonly Action<object> execute;

    public RelayCommand(Action<object> execute) => this.execute = execute;
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object parameter) => true;
    public void Execute(object parameter) => execute(parameter);
}
