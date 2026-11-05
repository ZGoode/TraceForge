using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using TraceForge.Domain;

namespace TraceForge.Desktop;

/// <summary>Small code-built modal dialogs (keyboard friendly: Enter = OK, Esc = Cancel, named for screen readers).</summary>
internal static class Dialogs
{
    private static Window Create(Window owner, string title, UIElement content) => new()
    {
        Title = title, Owner = owner, Icon = owner.Icon, Content = content, SizeToContent = SizeToContent.WidthAndHeight,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
    };

    /// <summary>The About box: name, version, data format and where TraceForge keeps its small support files. "Copy details" is for support requests.</summary>
    public static void About(Window owner)
    {
        var assembly = typeof(Dialogs).Assembly;
        var version = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
        var dataFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraceForge");
        var details = $"TraceForge {version}\nProject format {TraceForge.Infrastructure.ProjectManifest.CurrentFormatVersion}, database schema {TraceForge.Infrastructure.ProjectManifest.CurrentSchemaVersion}\n"
                    + $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nSupport files: {dataFolder}";

        var logo = new System.Windows.Controls.Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        try { logo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/TraceForge-256.png")); }
        catch (Exception ex) when (ex is System.IO.IOException or UriFormatException or NotSupportedException) { logo.Visibility = Visibility.Collapsed; }
        AutomationProperties.SetName(logo, "TraceForge logo");

        var text = new StackPanel { MaxWidth = 420 };
        text.Children.Add(new TextBlock { Text = "TraceForge", FontSize = 24, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = $"Version {version}", Margin = new Thickness(0, 2, 0, 8) });
        text.Children.Add(new TextBlock { Text = "A local-first requirements and traceability workbench. Your projects are ordinary folders on your computer; nothing is sent anywhere.", TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = $"Project format {TraceForge.Infrastructure.ProjectManifest.CurrentFormatVersion}, database schema {TraceForge.Infrastructure.ProjectManifest.CurrentSchemaVersion}\n{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}", Margin = new Thickness(0, 10, 0, 0), Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = $"Support files: {dataFolder}", Margin = new Thickness(0, 4, 0, 0), Opacity = 0.75, TextWrapping = TextWrapping.Wrap });

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(logo); head.Children.Add(text);
        var copy = new Button { Content = "Copy details", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, Width = 80 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(copy); row.Children.Add(close);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(head); panel.Children.Add(row);

        var window = Create(owner, "About TraceForge", panel);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(details); copy.Content = "Copied"; }
            catch (System.Runtime.InteropServices.COMException) { copy.Content = "Clipboard busy"; } // another program holds the clipboard; try again
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static (Button Ok, StackPanel Row) Buttons()
    {
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(ok); row.Children.Add(cancel);
        return (ok, row);
    }

    public static string? Prompt(Window owner, string title, string label, string initial = "", bool multiline = false)
    {
        var box = new TextBox { Text = initial, MinWidth = 340, Margin = new Thickness(0, 4, 0, 0) };
        if (multiline) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 110; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        AutomationProperties.SetName(box, label);
        var (ok, row) = Buttons();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 }); panel.Children.Add(box); panel.Children.Add(row);
        var window = Create(owner, title, panel);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public static T? Choose<T>(Window owner, string title, string label, IReadOnlyList<T> items, Func<T, string> display) where T : class
    {
        var list = new ListBox { MinWidth = 340, MinHeight = 160, MaxHeight = 320, Margin = new Thickness(0, 4, 0, 0), ItemsSource = items.Select(display).ToList() };
        AutomationProperties.SetName(list, label);
        var (ok, row) = Buttons();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(list); panel.Children.Add(row);
        var window = Create(owner, title, panel);
        ok.Click += (_, _) => { if (list.SelectedIndex >= 0) window.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) window.DialogResult = true; };
        window.Loaded += (_, _) => { list.SelectedIndex = 0; list.Focus(); };
        return window.ShowDialog() == true && list.SelectedIndex >= 0 ? items[list.SelectedIndex] : null;
    }

    /// <summary>A row in a multi-select picker.</summary>
    public sealed class PickItem(object tag, string text, bool isChecked = false) : INotifyPropertyChanged
    {
        private bool _checked = isChecked;
        public object Tag { get; } = tag;
        public string Text { get; } = text;
        public bool IsChecked
        {
            get => _checked;
            set { if (_checked == value) return; _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Searchable checkbox list. Returns the checked items (null when cancelled or nothing checked).
    /// When <paramref name="withLinkType"/> is set a link-type combo is shown and its choice is returned too.
    /// </summary>
    public static (List<PickItem> Picked, RelationshipType? Type)? PickMany(Window owner, string title, string label, IReadOnlyList<PickItem> items, bool allowEmpty = false, bool withLinkType = false, bool single = false)
    {
        var search = new TextBox { MinWidth = 420, Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetName(search, "Search");
        var list = new ListBox { MinWidth = 420, Height = 280, ItemsSource = items };
        AutomationProperties.SetName(list, label);
        list.ItemTemplate = new DataTemplate();
        FrameworkElementFactory factory;
        if (single) { factory = new FrameworkElementFactory(typeof(TextBlock)); factory.SetBinding(TextBlock.TextProperty, new Binding(nameof(PickItem.Text))); }
        else
        {
            factory = new FrameworkElementFactory(typeof(CheckBox));
            factory.SetBinding(ContentControl.ContentProperty, new Binding(nameof(PickItem.Text)));
            factory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(PickItem.IsChecked)) { Mode = BindingMode.TwoWay });
        }
        list.ItemTemplate.VisualTree = factory;
        var view = CollectionViewSource.GetDefaultView(items);
        search.TextChanged += (_, _) =>
        {
            var needle = search.Text.Trim();
            view.Filter = needle.Length == 0 ? null : o => ((PickItem)o).Text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        };
        var count = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        void UpdateCount() => count.Text = $"{items.Count(i => i.IsChecked)} selected";
        foreach (var i in items) i.PropertyChanged += (_, _) => UpdateCount();
        UpdateCount();
        var typeBox = new ComboBox { ItemsSource = Enum.GetValues<RelationshipType>(), SelectedItem = RelationshipType.Decomposes, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(typeBox, "Link type");
        var (ok, row) = Buttons();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(search); panel.Children.Add(list); panel.Children.Add(count);
        if (withLinkType) { panel.Children.Add(new TextBlock { Text = "Link type", Margin = new Thickness(0, 8, 0, 0) }); panel.Children.Add(typeBox); }
        panel.Children.Add(row);
        var window = Create(owner, title, panel);
        ok.Click += (_, _) =>
        {
            if (single) { if (list.SelectedItem is PickItem chosen) { chosen.IsChecked = true; window.DialogResult = true; } return; }
            if (allowEmpty || items.Any(i => i.IsChecked)) window.DialogResult = true;
        };
        if (single) list.MouseDoubleClick += (_, _) => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        list.KeyDown += (_, e) =>
        {
            if (!single && e.Key == System.Windows.Input.Key.Space && list.SelectedItem is PickItem current) { current.IsChecked = !current.IsChecked; e.Handled = true; }
        };
        window.Loaded += (_, _) => search.Focus();
        if (window.ShowDialog() != true) return null;
        return (items.Where(i => i.IsChecked).ToList(), withLinkType ? (RelationshipType)typeBox.SelectedItem : null);
    }

    /// <summary>Resizable read-only table. Columns are (header, property name, width).</summary>
    public static void ShowTable<T>(Window owner, string title, string header, IReadOnlyList<T> rows, params (string Header, string Property, double Width)[] columns)
    {
        var grid = new ListView { ItemsSource = rows, MinHeight = 200 };
        AutomationProperties.SetName(grid, title);
        var view = new GridView();
        foreach (var (h, property, width) in columns)
            view.Columns.Add(new GridViewColumn { Header = h, Width = width, DisplayMemberBinding = new Binding(property) });
        grid.View = view;
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var dock = new DockPanel { Margin = new Thickness(12) };
        var head = new TextBlock { Text = header, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(head); dock.Children.Add(close); dock.Children.Add(grid);
        var window = new Window { Title = title, Owner = owner, Icon = owner.Icon, Content = dock, Width = 900, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    public static void ShowChanges(Window owner, string title, string header, IReadOnlyList<ChangeRow> rows) =>
        ShowTable(owner, title, header, rows, ("Change", nameof(ChangeRow.Change), 100), ("Module", nameof(ChangeRow.Module), 110), ("Object", nameof(ChangeRow.Object), 100), ("Details", nameof(ChangeRow.Description), 520));

    private sealed record IssueRow(string Row, string Level, string Message);

    /// <summary>Shows what an import would do and lets the user accept it. <paramref name="plan"/> is recomputed when the "create new attributes" option changes.</summary>
    public static ImportPlan? ConfirmImport(Window owner, string title, string fileName, Func<bool, ImportPlan> plan)
    {
        var current = plan(false);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 760, Margin = new Thickness(0, 0, 0, 8) };
        var create = new CheckBox { Content = "Create columns that are not attributes yet as new Text attributes", Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListView { Height = 240, MinWidth = 760 };
        AutomationProperties.SetName(list, "Import problems and warnings");
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Row", Width = 50, DisplayMemberBinding = new Binding(nameof(IssueRow.Row)) });
        view.Columns.Add(new GridViewColumn { Header = "Level", Width = 70, DisplayMemberBinding = new Binding(nameof(IssueRow.Level)) });
        view.Columns.Add(new GridViewColumn { Header = "Message", Width = 600, DisplayMemberBinding = new Binding(nameof(IssueRow.Message)) });
        list.View = view;
        var (ok, buttons) = Buttons();
        ok.Content = "Import";
        void Refresh()
        {
            var names = current.NewAttributes.Select(a => a.Name).ToList();
            summary.Text = $"{fileName}: {current.Count(ImportAction.Create)} to create, {current.Count(ImportAction.Update)} to update, {current.Count(ImportAction.Unchanged)} unchanged, {current.Count(ImportAction.Skip)} skipped, {current.Links.Count} link(s)"
                + (names.Count > 0 ? $". New attributes: {string.Join(", ", names)}" : "")
                + (current.AssignAttributes.Count > 0 ? $". Existing attributes added to this module: {string.Join(", ", current.AssignAttributes.Select(a => a.Name))}" : "")
                + ". The whole import can be undone.";
            list.ItemsSource = current.Issues.Select(i => new IssueRow(i.RowNumber > 0 ? i.RowNumber.ToString() : "", i.IsError ? "Error" : "Warning", i.Message)).ToList();
            ok.IsEnabled = current.HasWork;
        }
        create.Click += (_, _) => { current = plan(create.IsChecked == true); Refresh(); };
        Refresh();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(summary); panel.Children.Add(create); panel.Children.Add(new TextBlock { Text = "Problems and warnings (rows with errors are skipped; warnings still import):" }); panel.Children.Add(list); panel.Children.Add(buttons);
        var window = Create(owner, title, panel);
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? current : null;
    }

    public static AttributeDefinition? PromptAttribute(Window owner)
    {
        var name = new TextBox { MinWidth = 340, Margin = new Thickness(0, 4, 0, 8) }; AutomationProperties.SetName(name, "Attribute name");
        var type = new ComboBox { ItemsSource = Enum.GetValues<AttributeDataType>(), SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 8) }; AutomationProperties.SetName(type, "Attribute type");
        var required = new CheckBox { Content = "Required", Margin = new Thickness(0, 0, 0, 8) };
        var values = new TextBox { IsEnabled = false, Margin = new Thickness(0, 4, 0, 0) }; AutomationProperties.SetName(values, "Enumeration values, comma separated");
        type.SelectionChanged += (_, _) => values.IsEnabled = type.SelectedItem is AttributeDataType.Enumeration;
        var (ok, row) = Buttons();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Name" }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Type" }); panel.Children.Add(type);
        panel.Children.Add(required);
        panel.Children.Add(new TextBlock { Text = "Values (enumeration only, comma separated)" }); panel.Children.Add(values);
        panel.Children.Add(row);
        var window = Create(owner, "New attribute", panel);
        AttributeDefinition? result = null;
        ok.Click += (_, _) =>
        {
            try
            {
                result = AttributeDefinition.Create(name.Text, (AttributeDataType)type.SelectedItem, required.IsChecked == true, values.Text.Split(',', StringSplitOptions.RemoveEmptyEntries));
                window.DialogResult = true;
            }
            catch (ArgumentException ex) { MessageBox.Show(window, ex.Message, "New attribute", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        window.Loaded += (_, _) => name.Focus();
        return window.ShowDialog() == true ? result : null;
    }
}
