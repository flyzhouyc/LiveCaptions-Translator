using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LiveCaptionsTranslator.controllers;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator
{
    public partial class HistoryPage : Page
    {
        int page = 1;
        int maxPage = 1;
        int maxRow = 20;

        public HistoryPage()
        {
            InitializeComponent();
            LoadHistoryAsync();
            MaxRowBox.SelectedIndex = App.Settings.HistoryMaxRow;

            TranslationController.TranslationLogged += async () => await LoadHistoryAsync();
        }

        private async Task LoadHistoryAsync()
        {
            var data = await SQLiteHistoryLogger.LoadHistoryAsync(page, maxRow);
            List<TranslationHistoryEntry> history = data.Item1;

            maxPage = data.Item2;
            await Dispatcher.InvokeAsync(() =>
            {
                HistoryDataGrid.ItemsSource = history;
                PageNamber.Text = page.ToString() + "/" + maxPage.ToString();
            });
        }

        void PageDown(object sender, RoutedEventArgs e)
        {
            if (page-1 >= 1)
            {
                page--;
            }

            LoadHistoryAsync();
        }
        void PageUp(object sender, RoutedEventArgs e)
        {
            if (page < maxPage)
            {
                page++;
            }
            
            LoadHistoryAsync();
        }

        void RemoveLogs(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Do you want to clear translation storage history?",
                    "Clear history",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                SQLiteHistoryLogger.ClaerHistory();
                LoadHistoryAsync();
            }
        }

        private void maxRow_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string text = (e.AddedItems[0] as ComboBoxItem).Tag as string;
            maxRow = Convert.ToInt32(text);
            App.Settings.HistoryMaxRow = MaxRowBox.SelectedIndex;

            LoadHistoryAsync();

            if (page> maxPage)
            {
                page = maxPage;
                LoadHistoryAsync();;
            }
        }

        private void ReloadLogs(object sender, RoutedEventArgs e)
        {
            LoadHistoryAsync();
        }
    }
}