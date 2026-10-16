using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PowerMIDI.ViewModels;

namespace PowerMIDI;

// Event Editor window (see EventEditorViewModel): scrolls to the row being played, plays from a double-clicked row.
public partial class EventEditorWindow : Window
{
    readonly EventEditorViewModel vm;

    public EventEditorWindow(EventEditorViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        vm.FollowRow += row => EventList.ScrollIntoView(row);
    }

    void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: EventRowViewModel row }) vm.SeekCommand.Execute(row);
    }
}
