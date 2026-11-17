using System.Windows;
using System.Windows.Controls;
using KeedParsecNative.App.Views;

namespace KeedParsecNative.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        NavList.SelectionChanged += NavList_SelectionChanged;
        Loaded += (_, _) => NavList.SelectedIndex = 0;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not Type viewType)
        {
            return;
        }

        var view = (UserControl)Activator.CreateInstance(viewType)!;
        view.DataContext = DataContext;
        PageHost.Content = view;
    }
}
