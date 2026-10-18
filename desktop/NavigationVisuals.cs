using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Creezio.Switcher.Desktop
{
    internal sealed class NavigationGlyph : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value as string)
            {
                case "Accueil": return "\uE80F";
                case "Instances": return "\uE8A7";
                case "Compétences": return "\uE8F1";
                case "Planifications": return "\uE787";
                case "Performances": return "\uE9D9";
                case "Stockage": return "\uEDA2";
                case "Tâches": return "\uE9D5";
                case "Délégations": return "\uE753";
                case "Webhooks": return "\uE774";
                case "Assistance": return "\uE897";
                case "PC distants": return "\uE968";
                case "Comptes": return "\uE77B";
                case "Outils partagés": return "\uE8D4";
                case "Agents": return "\uE716";
                case "Projets": return "\uE8B7";
                case "Paramètres": return "\uE713";
                default: return "\uE8A5";
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        internal static DataTemplate Template()
        {
            var template = new DataTemplate();
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetBinding(TextBlock.TextProperty, new Binding(".") { Converter = new NavigationGlyph() });
            glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
            glyph.SetValue(TextBlock.FontSizeProperty, 15.0);
            glyph.SetValue(FrameworkElement.WidthProperty, 24.0);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.AppendChild(glyph);
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding("."));
            label.SetValue(TextBlock.FontSizeProperty, 13.0);
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            panel.AppendChild(label);
            template.VisualTree = panel;
            return template;
        }
    }
}
