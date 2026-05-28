using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DTSAG.HRSuite.SQLImport.Models;

public class ColumnMapping : INotifyPropertyChanged
{
    private string _csvColumn = "(ignorieren)";
    private string _fixedValue = string.Empty;
    private bool _isKey;

    public string DbColumn { get; init; } = string.Empty;
    public string DbColumnType { get; init; } = string.Empty;
    public bool IsNullable { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsForeignKey { get; init; }
    public bool IsIdentity { get; init; }

    public string CsvColumn
    {
        get => _csvColumn;
        set
        {
            _csvColumn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFixedValueEnabled));
        }
    }

    public string FixedValue
    {
        get => _fixedValue;
        set { _fixedValue = value; OnPropertyChanged(); }
    }

    // Fester Wert ist nur sinnvoll wenn keine CSV-Spalte zugeordnet ist
    public bool IsFixedValueEnabled => _csvColumn == "(ignorieren)";

    public bool IsKey
    {
        get => _isKey;
        set { _isKey = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
