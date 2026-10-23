using System.Windows;
using System.Windows.Controls;

namespace DesktopAssistant;

internal sealed class MemoryManagementWindow : Window
{
    private readonly ConversationMemoryStore _store;
    private readonly ListBox _preferences = new();
    private readonly ListBox _conversations = new();
    private readonly TextBlock _status = new();
    private readonly Func<Task> _onConversationsChanged;

    public MemoryManagementWindow(ConversationMemoryStore store, Func<Task> onConversationsChanged)
    {
        _store = store;
        _onConversationsChanged = onConversationsChanged;
        Title = "Local memory";
        Width = 760; Height = 560; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.WhiteSmoke;

        var root = new DockPanel { Margin = new Thickness(16) };
        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var memories = Section("Saved preferences", _preferences, "Forget selected", ForgetPreferenceAsync);
        var chats = Section("Conversations", _conversations, "Delete selected", DeleteConversationAsync);
        Grid.SetColumn(chats, 1); columns.Children.Add(memories); columns.Children.Add(chats);
        root.Children.Add(columns);
        var clear = new Button { Content = "Clear all conversation history", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Right };
        clear.Click += async (_, _) => await ClearAllAsync();
        DockPanel.SetDock(clear, Dock.Bottom); root.Children.Add(clear);
        var create = new Button { Content = "Start a new conversation", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
        create.Click += async (_, _) => await CreateConversationAsync();
        DockPanel.SetDock(create, Dock.Bottom); root.Children.Add(create);
        Content = root;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private static UIElement Section(string title, ListBox list, string buttonText, Func<Task> action)
    {
        var panel = new DockPanel { Margin = new Thickness(6) };
        var heading = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var button = new Button { Content = buttonText, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => await action(); DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        list.DisplayMemberPath = "Display"; panel.Children.Add(list);
        return panel;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var preferences = await _store.GetPreferencesAsync();
            _preferences.ItemsSource = preferences.Select(item => new PreferenceItem(item.Id, item.Content)).ToList();
            var conversations = await _store.GetSessionsAsync();
            _conversations.ItemsSource = conversations.Select(item => new ConversationItem(item.Id, item.Title, item.UpdatedUtc)).ToList();
            _status.Text = $"Stored locally on this computer · {preferences.Count} preferences · {conversations.Count} conversations";
        }
        catch (Exception ex) { _status.Text = $"Could not read local memory: {ex.Message}"; }
    }

    private async Task ForgetPreferenceAsync()
    {
        if (_preferences.SelectedItem is not PreferenceItem item) { _status.Text = "Select a preference first."; return; }
        try { await _store.DeletePreferenceAsync(item.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = $"Could not delete preference: {ex.Message}"; }
    }

    private async Task DeleteConversationAsync()
    {
        if (_conversations.SelectedItem is not ConversationItem item) { _status.Text = "Select a conversation first."; return; }
        try { await _store.DeleteSessionAsync(item.Id); await _onConversationsChanged(); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = $"Could not delete conversation: {ex.Message}"; }
    }

    private async Task ClearAllAsync()
    {
        if (MessageBox.Show(this, "Delete all locally stored conversation history? Saved preferences will remain.", "Clear conversation history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { await _store.ClearConversationsAsync(); await _onConversationsChanged(); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = $"Could not clear conversation history: {ex.Message}"; }
    }

    private async Task CreateConversationAsync()
    {
        try { await _store.CreateSessionAsync(); await _onConversationsChanged(); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = $"Could not start a conversation: {ex.Message}"; }
    }

    private sealed record PreferenceItem(long Id, string Display);
    private sealed record ConversationItem(string Id, string Title, DateTime UpdatedUtc)
    {
        public string Display => $"{Title}  ·  {UpdatedUtc.ToLocalTime():g}";
    }
}
