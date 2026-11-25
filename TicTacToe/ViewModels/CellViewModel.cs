using CommunityToolkit.Mvvm.ComponentModel;

namespace TicTacToe.ViewModels;

public partial class CellViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private Color _cellColor = Colors.Black;

    partial void OnValueChanged(string value) =>
        CellColor = string.IsNullOrEmpty(value) ? Colors.Black : Color.FromArgb("#333333");
}
