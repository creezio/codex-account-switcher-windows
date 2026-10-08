using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal static class Ui
    {
        internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int n = 0; n < VisualTreeHelper.GetChildrenCount(parent); n++)
            {
                var child = VisualTreeHelper.GetChild(parent, n);
                yield return child;
                foreach (var nested in Descendants(child))
                    yield return nested;
            }
        }
        public static TextBlock Text(string text, double size = 14, bool muted = false)
        {
            var b = new TextBlock { Text = text, FontSize = size, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            b.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
            return b;
        }
        public static Button Button(string text, Action action, bool primary = false)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(b, text);
            if (primary)
                b.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            b.Click += delegate
            {
                action();
            };
            return b;
        }
        public static Button AsyncButton(string text, Func<Task> action, Action<Exception> failed, bool primary = false)
        {
            Button b = null;
            b = Button(text, async delegate { if (!b.IsEnabled) return; b.IsEnabled = false; try { await action(); } catch (Exception e) { failed(e); } finally { b.IsEnabled = true; } }, primary);
            return b;
        }
        public static TextBox Input(string label, string value, Panel panel, bool multi = false)
        {
            panel.Children.Add(Text(label, 13));
            var field = new TextBox { Text = value ?? "", AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multi ? 112 : 36, VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
            AutomationProperties.SetName(field, label);
            panel.Children.Add(field);
            return field;
        }
        internal static Grid SearchField(TextBox input,string hint)
        {
            var grid=new Grid();grid.Children.Add(input);
            var watermark=Text(hint,13,true);watermark.Margin=new Thickness(11,0,12,10);watermark.VerticalAlignment=VerticalAlignment.Center;watermark.IsHitTestVisible=false;grid.Children.Add(watermark);
            input.TextChanged+=delegate{watermark.Visibility=String.IsNullOrEmpty(input.Text)?Visibility.Visible:Visibility.Collapsed;};
            AutomationProperties.SetName(input,hint);return grid;
        }
        public static ComboBox Select(string label, IEnumerable<string> values, string selected, Panel panel)
        {
            panel.Children.Add(Text(label, 13));
            var box = new ComboBox { ItemsSource = values.ToArray(), SelectedItem = selected };
            if (box.SelectedIndex < 0 && box.Items.Count > 0)
                box.SelectedIndex = 0;
            AutomationProperties.SetName(box, label);
            panel.Children.Add(box);
            return box;
        }
        public static CheckBox Check(string label, bool value, Panel panel)
        {
            var box = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
            AutomationProperties.SetName(box, label);
            panel.Children.Add(box);
            return box;
        }
        public static Border Card(UIElement child)
        {
            var b = new Border { Child = child, CornerRadius = new CornerRadius(10), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            return b;
        }
        public static WrapPanel Actions(Panel parent)
        {
            var p = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
            parent.Children.Add(p);
            return p;
        }
        public static bool Confirm(Window owner, string text, string title)
        {
            return MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        public static string Folder()
        {
            var picker = new Microsoft.Win32.OpenFolderDialog();
            return picker.ShowDialog() == true ? picker.FolderName : null;
        }
        public static string File(string filter)
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            return picker.ShowDialog() == true ? picker.FileName : null;
        }
        public static string Prompt(Window owner, string title, string label, string initial)
        {
            string result = null;
            var dialog = new EditWindow(owner, title);
            var input = Input(label, initial, dialog.Fields);
            dialog.Save = () => { if (String.IsNullOrWhiteSpace(input.Text)) throw new InvalidOperationException("Saisissez un nom."); result = input.Text.Trim(); return Task.CompletedTask; };
            dialog.ShowDialog();
            return result;
        }
    }
    internal sealed class EditWindow : Window
    {
        public readonly StackPanel Fields = new StackPanel();
        public Func<Task> Save;
        public Func<bool> Dirty;
        public bool Saved;
        internal readonly Button SaveButton;
        internal Func<bool> ConfirmDiscard;
        private bool saving;
        public EditWindow(Window owner, string title, string saveCaption = "Enregistrer")
        {
            Owner = owner;
            Title = title;
            SetResourceReference(BackgroundProperty, "CanvasBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;
            Width = Math.Min(680, SystemParameters.WorkArea.Width);
            Height = Math.Min(640, SystemParameters.WorkArea.Height);
            MinWidth = 460;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var dock = new DockPanel { Margin = new Thickness(24) };
            dock.SetResourceReference(Panel.BackgroundProperty,"CanvasBrush");
            Content = dock;
            var heading = Ui.Text(title, 24);
            DockPanel.SetDock(heading, Dock.Top);
            dock.Children.Add(heading);
            var footer = new StackPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);
            var error = Ui.Text("", 13);
            error.Foreground = Brushes.Firebrick;
            footer.Children.Add(error);
            var actions = Ui.Actions(footer);
            SaveButton=Ui.AsyncButton(saveCaption, async delegate { saving = true; try { if (Save != null) await Save(); Saved = true; Close(); } finally { saving = false; } }, e => error.Text = Program.SafeError(e), true);
            actions.Children.Add(SaveButton);
            actions.Children.Add(Ui.Button("Annuler", Close));
            dock.Children.Add(new ScrollViewer { Content = Fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            ConfirmDiscard = () => Ui.Confirm(this, "Abandonner les modifications non enregistrées ?", "Modifications en cours");
            Closing += delegate (object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (saving && !Saved)
                {
                    e.Cancel = true;
                    return;
                }
                if (!Saved && Dirty != null && Dirty() && !ConfirmDiscard())
                    e.Cancel = true;
            };
        }
    }
    internal abstract class PageView : Grid
    {
        protected readonly DesktopContext Context;
        protected readonly ShellWindow Shell;
        protected readonly TextBlock Notice = Ui.Text("", 13, true);
        protected readonly DockPanel Body = new DockPanel();
        protected readonly WrapPanel Commands = new WrapPanel();
        protected readonly StackPanel Header = new StackPanel();
        public string Title
        {
            get; private set;
        }
        protected PageView(DesktopContext context, ShellWindow shell, string title, string description)
        {
            Context = context;
            Shell = shell;
            Title = title;
            Margin = new Thickness(28, 24, 28, 20);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            var head = Header;
            head.Children.Add(Ui.Text(title, 28));
            head.Children.Add(Ui.Text(description, 14, true));
            Children.Add(head);
            SetRow(Commands, 1);
            Children.Add(Commands);
            SetRow(Notice, 2);
            Notice.Margin = new Thickness(0, 8, 0, 10);
            var noticeStyle=new Style(typeof(TextBlock));var empty=new Trigger{Property=TextBlock.TextProperty,Value=""};empty.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));noticeStyle.Triggers.Add(empty);Notice.Style=noticeStyle;
            Children.Add(Notice);
            SetRow(Body, 3);
            Children.Add(Body);
        }
        protected void Error(Exception e)
        {
            Notice.Text = Program.SafeError(e);
        }
        protected Button Command(string text, Func<Task> action, bool primary = false)
        {
            var b = Ui.AsyncButton(text, action, Error, primary);
            Commands.Children.Add(b);
            return b;
        }
        protected async Task Change(Func<AccountService, Task> action)
        {
            await Context.Mutate(action);
            await Refresh();
        }
        public virtual bool CanLeave()
        {
            return true;
        }
        public abstract Task Refresh();
    }
    internal sealed class ItemRow
    {
        public string Id
        {
            get; set;
        }
        public string Title
        {
            get; set;
        }
        public string Summary
        {
            get; set;
        }
        public string State
        {
            get; set;
        }
        public string Revision
        {
            get; set;
        }
        public object Value
        {
            get; set;
        }
        public override string ToString()
        {
            return Title;
        }
    }
    internal abstract class CollectionPage : PageView
    {
        internal readonly ListBox List = new ListBox();
        protected readonly StackPanel Details = new StackPanel();
        protected readonly TextBox Search = new TextBox();
        private string fingerprint;
        protected CollectionPage(DesktopContext context, ShellWindow shell, string title, string description) : base(context, shell, title, description)
        {
            var split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.46, GridUnitType.Star), MinWidth = 220 });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.54, GridUnitType.Star), MinWidth = 240 });
            Body.Children.Add(split);
            var left = new DockPanel();
            split.Children.Add(left);
            Search.MinWidth = 160;
            AutomationProperties.SetName(Search, "Rechercher dans " + title);
            Search.ToolTip = "Rechercher";
            var searchLabel = Ui.Text("Rechercher", 12, true);
            DockPanel.SetDock(searchLabel, Dock.Top);
            left.Children.Add(searchLabel);
            DockPanel.SetDock(Search, Dock.Top);
            left.Children.Add(Search);
            left.Children.Add(List);
            var template = new DataTemplate(typeof(ItemRow));
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetValue(TextBlock.FontSizeProperty, 15.0);
            panel.AppendChild(name);
            var info = new FrameworkElementFactory(typeof(TextBlock));
            info.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Summary"));
            info.SetValue(TextBlock.MarginProperty, new Thickness(0, 5, 0, 0));
            info.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            panel.AppendChild(info);
            var state = new FrameworkElementFactory(typeof(TextBlock));
            state.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("State"));
            state.SetValue(TextBlock.MarginProperty, new Thickness(0, 7, 0, 0));
            state.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            panel.AppendChild(state);
            template.VisualTree = panel;
            List.ItemTemplate = template;
            var right = new ScrollViewer { Content = Details };
            SetColumn(right, 2);
            split.Children.Add(right);
            List.SelectionChanged += delegate
            {
                try { ShowSelected(); } catch (Exception e) { Error(e); }
            };
            Search.TextChanged += async delegate { try { await SearchChanged(); } catch (Exception e) { Error(e); } };
        }
        protected virtual Task SearchChanged()
        {
            return Refresh();
        }
        protected void Rows(IEnumerable<ItemRow> source, bool filter = true)
        {
            string selected = (List.SelectedItem as ItemRow)?.Id;
            var rows = source.Where(r => !filter || (r.Title + " " + r.Summary).IndexOf(Search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0).ToArray();
            string stamp = String.Join("|", rows.Select(r => r.Id + ":" + r.Title + ":" + r.Summary + ":" + r.State + ":" + r.Revision));
            if (stamp == fingerprint)
                return;
            fingerprint = stamp;
            var scroll = Ui.Descendants(List).OfType<ScrollViewer>().FirstOrDefault();
            double offset = scroll?.VerticalOffset ?? 0;
            List.ItemsSource = rows;
            List.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
            List.UpdateLayout();
            scroll?.ScrollToVerticalOffset(offset);
            if (rows.Length == 0)
            {
                Details.Children.Clear();
                Details.Children.Add(Ui.Text("Aucun élément à afficher", 20));
                Details.Children.Add(Ui.Text(String.IsNullOrEmpty(Search.Text) ? "Utilisez l'action en haut de cette page pour commencer." : "Modifiez votre recherche.", 14, true));
            }
        }
        protected abstract void ShowSelected();
    }
}
