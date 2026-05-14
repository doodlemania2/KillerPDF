using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TDPdf
{
    // Foundation only. Subsequent slices migrate state from MainWindow.xaml.cs into here. Do NOT load this from MainWindow yet; the migration is a separate PR per issue #18 slice list.
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private string statusText = "";

        [RelayCommand]
        private void NoOp()
        {
        }
    }
}
