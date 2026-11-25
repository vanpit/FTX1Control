using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;

namespace FTX1Control.ViewModels;

public partial class MeterOptionViewModel : ObservableObject
{
    public static readonly (string Label, int Code)[] AvailableMeters =
    {
        ("PO", 0),
        ("COMP", 1),
        ("ALC", 2),
        ("VDD", 3),
        ("ID", 4),
        ("SWR", 5)
    };

    [ObservableProperty]
    private bool _isSelected;

    public string Label { get; }
    public int Code { get; }
    public IRelayCommand SelectCommand { get; }

    public MeterOptionViewModel(string label, int code, Action select)
    {
        Label = label;
        Code = code;
        SelectCommand = new RelayCommand(select);
    }

    public static string GetLabel(int code) => code == 6
        ? "TEMP"
        : AvailableMeters.First(meter => meter.Code == code).Label;
}