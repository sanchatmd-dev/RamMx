using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RamMx.App;

public interface IInteraction
{
    bool Confirm(string title, string message);
    void Show(string title, string message);
    string? SaveFile(string name);
    string? OpenFile();
}
public sealed class DialogService(Func<Window?> owner) : IInteraction
{
    public bool Confirm(string title, string message) { var dialog = new ConfirmWindow(title, message) { Owner = owner() }; return dialog.ShowDialog() == true; }
    public void Show(string title, string message) { var dialog = new ConfirmWindow(title, message, true) { Owner = owner() }; dialog.ShowDialog(); }
    public string? SaveFile(string name) { var dialog = new SaveFileDialog { FileName = name, Filter = "JSON (*.json)|*.json", OverwritePrompt = true }; return dialog.ShowDialog(owner()) == true ? dialog.FileName : null; }
    public string? OpenFile() { var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", CheckFileExists = true }; return dialog.ShowDialog(owner()) == true ? dialog.FileName : null; }
}
public sealed class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, bool informational = false)
    {
        Title = title; Width = 660; Height = 470; MinWidth = 500; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush"); FontSize = 14;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, FontSize = 22, Margin = new Thickness(0, 0, 0, 16) }; grid.Children.Add(heading);
        var text = new System.Windows.Controls.TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(text, 1); grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (!informational)
        {
            var cancel = new System.Windows.Controls.Button { Content = "ยกเลิก", IsCancel = true, IsDefault = true }; buttons.Children.Add(cancel);
        }
        var accept = new System.Windows.Controls.Button { Content = informational ? "ปิดหน้าต่าง" : "ยืนยันรายการนี้", IsDefault = informational };
        accept.Click += (_, _) => { DialogResult = true; }; buttons.Children.Add(accept);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
    }
}
