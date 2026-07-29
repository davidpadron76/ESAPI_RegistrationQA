using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ESAPI_RegistrationQA.UI
{
    /// <summary>
    /// A plain window with one or two read-only tables, built in code rather than in XAML.
    ///
    /// Advisories and diagnostics used to be tabs in the main window. Both are long-form
    /// prose — useful exactly when something needs explaining, and noise the rest of the time
    /// on a screen whose job is to show a handful of numbers and a colour. They live behind a
    /// button now, and this is what the button opens.
    ///
    /// No XAML file, because there is no styling here worth a designer: a grid, a header, a
    /// close button. Keeping it in one method also keeps the two callers honest about how
    /// little chrome this actually needs.
    /// </summary>
    public static class DetailWindow
    {
        /// <summary>
        /// Column specification: header, bound property, width. A width of 0 means "take
        /// whatever is left", which is what the last column always wants.
        /// </summary>
        public static void Show(
            string title,
            IEnumerable rows,
            Tuple<string, string, double>[] columns,
            IEnumerable secondaryRows = null,
            Tuple<string, string, double>[] secondaryColumns = null)
        {
            var layout = new Grid { Margin = new Thickness(12) };

            bool hasSecondary = secondaryRows != null && secondaryColumns != null;

            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            if (hasSecondary)
            {
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(heading, 0);
            layout.Children.Add(heading);

            DataGrid primary = BuildGrid(rows, columns);
            Grid.SetRow(primary, 1);
            layout.Children.Add(primary);

            if (hasSecondary)
            {
                var subheading = new TextBlock
                {
                    Text = "Metrics that do not apply to this case",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 12, 0, 6)
                };
                Grid.SetRow(subheading, 2);
                layout.Children.Add(subheading);

                DataGrid secondary = BuildGrid(secondaryRows, secondaryColumns);
                Grid.SetRow(secondary, 3);
                layout.Children.Add(secondary);
            }

            var close = new Button
            {
                Content = "Close",
                Width = 110,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(close, hasSecondary ? 4 : 2);
            layout.Children.Add(close);

            // No Owner. Inside Eclipse, Application.Current is the host application and its
            // MainWindow belongs to a window tree this plugin does not control; assigning it
            // throws if that window is on another thread or not yet shown, and the only thing
            // it buys is where the dialog appears.
            var window = new Window
            {
                Title = title,
                Content = layout,
                Width = 980,
                Height = 620,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };

            close.Click += (s, e) => window.Close();
            window.ShowDialog();
        }

        private static DataGrid BuildGrid(IEnumerable rows, Tuple<string, string, double>[] columns)
        {
            var grid = new DataGrid
            {
                ItemsSource = rows,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderThickness = new Thickness(0),
                Background = Brushes.White,
                RowBackground = Brushes.White
            };

            foreach (Tuple<string, string, double> column in columns)
            {
                var cell = new FrameworkElementFactory(typeof(TextBlock));
                cell.SetBinding(TextBlock.TextProperty, new Binding(column.Item2));

                // Wrapping rather than clipping. These cells carry whole sentences, and a
                // reason cut off mid-word is the specific failure this window exists to avoid.
                cell.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
                cell.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 4, 6, 4));

                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = column.Item1,
                    Width = column.Item3 > 0
                        ? new DataGridLength(column.Item3)
                        : new DataGridLength(1, DataGridLengthUnitType.Star),
                    CellTemplate = new DataTemplate { VisualTree = cell }
                });
            }

            return grid;
        }
    }
}
