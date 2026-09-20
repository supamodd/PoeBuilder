using System.ComponentModel;
using System.Windows;
using PoeBuilder.App.ViewModels;

namespace PoeBuilder.App.Views;

public partial class ItemEditorWindow : Window
{
    private readonly ItemDraftViewModel _draft;
    public ItemEditorWindow(ItemDraftViewModel draft)
    {
        InitializeComponent();
        _draft = draft;
        DataContext = draft;
        draft.Saved += Close;
        Closing += OnClosing;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_draft.Accepted || !_draft.IsDirty) return;
        if (MessageBox.Show(_draft.L["UnsavedDraftQuestion"], _draft.L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            e.Cancel = true;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
}
