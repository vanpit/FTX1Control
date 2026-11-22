using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace FTX1Control.ViewModels;

public sealed partial class PinnedFrequencyViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _callSign = string.Empty;

    public int FrequencyHz { get; }
    public char ModeCode { get; }
    public string ModeDisplay { get; }
    public string Label { get; }
    public string Tooltip => $"{FrequencyHz} Hz, {ModeDisplay}\nPrawy przycisk myszy: usuń przypięcie";
    public IRelayCommand SelectCommand { get; }
    public IRelayCommand RemoveCommand { get; }

    public PinnedFrequencyViewModel(
        int frequencyHz,
        char modeCode,
        string modeDisplay,
        Action select,
        Action<PinnedFrequencyViewModel> remove,
        string callSign = "")
    {
        FrequencyHz = frequencyHz;
        ModeCode = modeCode;
        ModeDisplay = modeDisplay;
        CallSign = callSign;
        var kilohertz = (frequencyHz + 500) / 1000;
        Label = $"{kilohertz / 1000}.{kilohertz % 1000:000}";
        SelectCommand = new RelayCommand(select);
        RemoveCommand = new RelayCommand(() => remove(this));
    }
}
