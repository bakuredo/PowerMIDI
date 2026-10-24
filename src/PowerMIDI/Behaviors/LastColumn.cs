using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerMIDI.Behaviors;

// The header of the last column of a list with columns, marked (LastColumn.IsLast) so that its template does not draw the
// separator after it (it would show against the rounded border of the list, see Styles.xaml). Kept right when the
// columns are moved (a header dragged elsewhere). Set on the list: LastColumn.Track="True" (ThemedListView style).
public static class LastColumn
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(LastColumn), new PropertyMetadata(false, OnTrackChanged));

    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);

    public static readonly DependencyProperty IsLastProperty = DependencyProperty.RegisterAttached(
        "IsLast", typeof(bool), typeof(LastColumn), new PropertyMetadata(false));

    public static bool GetIsLast(DependencyObject d) => (bool)d.GetValue(IsLastProperty);
    public static void SetIsLast(DependencyObject d, bool value) => d.SetValue(IsLastProperty, value);

    // The columns followed (once per list, although a list is loaded again each time its tab is shown).
    static readonly DependencyProperty FollowedProperty = DependencyProperty.RegisterAttached(
        "Followed", typeof(bool), typeof(LastColumn), new PropertyMetadata(false));

    static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list) return;
        list.Loaded -= OnLoaded;
        if ((bool)e.NewValue) list.Loaded += OnLoaded;
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var list = (ListView)sender;
        if (list.View is not GridView grid) return;
        if (!(bool)list.GetValue(FollowedProperty))
        {
            list.SetValue(FollowedProperty, true);
            // After the move, once the headers are laid out again.
            grid.Columns.CollectionChanged += (_, _) => list.Dispatcher.BeginInvoke(() => Mark(list), DispatcherPriority.Loaded);
        }
        Mark(list);
    }

    static void Mark(ListView list)
    {
        if (list.View is not GridView { Columns.Count: > 0 } grid) return;
        var last = grid.Columns[^1];
        foreach (var header in Headers(list)) SetIsLast(header, header.Column == last);
    }

    static IEnumerable<GridViewColumnHeader> Headers(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is GridViewColumnHeader header) yield return header;
            else foreach (var inner in Headers(child)) yield return inner;
        }
    }
}
