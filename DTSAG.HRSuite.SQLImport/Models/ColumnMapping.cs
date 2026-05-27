using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DTSAG.HRSuite.SQLImport.Models;

public class ColumnMapping : INotifyPropertyChanged
{
    private string _csvColumn = "(ignorieren)";

    public string DbColumn { get; init; } = string.Empty;
    public string DbColumnType { get; init; } = string.Empty;
    public bool IsNullable { get; init; }

    public string CsvColumn
    {
        get => _csvColumn;
        set { _csvColumn = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
