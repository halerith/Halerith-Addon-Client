using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HalerithAddonClient.ViewModels;

public sealed class ContentItemCollection : ObservableCollection<ContentItemViewModel>
{
    public void ReplaceWith(IEnumerable<ContentItemViewModel> items)
    {
        var snapshot = items.ToList();
        if (this.SequenceEqual(snapshot)) return;
        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
