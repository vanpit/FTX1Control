using Avalonia;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace FTX1Control.Services;

public static class LanguageService
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FTX1Control",
        "language.txt");

    private static readonly (string Key, string Polish, string English)[] UiStrings =
    {
        ("WindowTitle", "FTX-1 | Sterowanie CAT", "FTX-1 | CAT Control"),
        ("HeaderEyebrow", "FTX-1  /  STEROWANIE CAT", "FTX-1  /  CAT CONTROL"),
        ("HeaderTitle", "Yaesu FTX-1 Panel Sterowania", "Yaesu FTX-1 Control Panel"),
        ("AlwaysOnTop", "Zawsze na wierzchu", "Always on top"),
        ("SerialPortLabel", "PORT SZEREGOWY", "SERIAL PORT"),
        ("BaudRateLabel", "PRĘDKOŚĆ ŁĄCZA", "BAUD RATE"),
        ("SMeterTitle", "S-METR MAIN VFO (ODBIÓR)", "MAIN VFO S-METER (RX)"),
        ("IncreaseDLevelTip", "Zwiększ D-LEVEL o 0,5 dB", "Increase D-LEVEL by 0.5 dB"),
        ("DecreaseDLevelTip", "Zmniejsz D-LEVEL o 0,5 dB", "Decrease D-LEVEL by 0.5 dB"),
        ("IncreaseNbTip", "Zwiększ poziom NB", "Increase NB level"),
        ("DecreaseNbTip", "Zmniejsz poziom NB", "Decrease NB level"),
        ("IncreaseDnrTip", "Zwiększ poziom DNR", "Increase DNR level"),
        ("DecreaseDnrTip", "Zmniejsz poziom DNR", "Decrease DNR level"),
        ("TransmitPowerTitle", "MOC NADAWANIA", "TRANSMIT POWER"),
        ("TransmitMetersTitle", "MIERNIKI NADAWANIA", "TRANSMIT METERS"),
        ("ActiveDuringTx", "Aktywne podczas TX", "Active during TX"),
        ("PowerOutputEstimate", "PO - MOC WYJŚCIOWA (SZAC.)", "PO - OUTPUT POWER (EST.)"),
        ("SwrEstimate", "SWR (SZAC.)", "SWR (EST.)"),
        ("MeterSelection", "WYBÓR MIERNIKA", "METER SELECTION"),
        ("PinnedFrequencies", "PRZYPIĘTE CZĘSTOTLIWOŚCI", "PINNED FREQUENCIES"),
        ("PinFrequency", "Przypnij\nczęst.", "Pin\nfreq."),
        ("CallSignPlaceholder", "Znak", "Callsign"),
        ("MuteMainVolumeTip", "Wycisz lub przywróć głośność MAIN VFO", "Mute or restore MAIN VFO volume"),
        ("MuteSubVolumeTip", "Wycisz lub przywróć głośność SUB VFO", "Mute or restore SUB VFO volume"),
        ("CatStatus", "STATUS CAT", "CAT STATUS"),
        ("CatFrames", "RAMKI CAT  [TX] wysłane  |  [RX] odebrane", "CAT FRAMES  [TX] sent  |  [RX] received")
    };

    private static readonly (string Polish, string English)[] StatusTranslations =
    {
        ("NIEPOŁĄCZONY", "NOT CONNECTED"),
        ("ŁĄCZENIE", "CONNECTING"),
        ("POŁĄCZONY", "CONNECTED"),
        ("BŁĄD POŁĄCZENIA", "CONNECTION ERROR"),
        ("Łączenie...", "Connecting..."),
        ("Rozłącz", "Disconnect"),
        ("Połącz", "Connect"),
        ("AUTO-PRZEWIJANIE: WŁ.", "AUTO-SCROLL: ON"),
        ("AUTO-PRZEWIJANIE: WYŁ.", "AUTO-SCROLL: OFF"),
        ("TX - NADAWANIE", "TX - TRANSMITTING"),
        ("RX - ODBIÓR", "RX - RECEIVING"),
        ("Wybierz port COM i połącz się z radiem FTX-1.", "Select a COM port and connect to the FTX-1 radio."),
        ("Połączenie z portem szeregowym zostało zamknięte.", "The serial port connection has been closed."),
        ("Skanowanie portów CAT jest dostępne tylko w systemie Windows.", "CAT port scanning is only available on Windows."),
        ("Nie wykryto portu CAT Silicon Labs typu Enhanced ani Standard.", "No Silicon Labs Enhanced or Standard CAT port was detected."),
        ("Wybierz wykryty port COM Silicon Labs.", "Select a detected Silicon Labs COM port."),
        ("Nie udało się zeskanować portów COM: ", "Failed to scan COM ports: "),
        ("Nie można utworzyć pliku logu CAT: ", "Unable to create the CAT log file: "),
        ("Nie można zapisać logu CAT; dalsze wpisy nie będą zapisywane: ", "Unable to write the CAT log; further entries will not be saved: "),
        ("Port został zamknięty, ale nie udało się wyłączyć Auto Information: ", "The port was closed, but Auto Information could not be disabled: "),
        ("Próba połączenia z ", "Connecting to "),
        ("Ponawianie połączenia (", "Retrying connection ("),
        ("Połączono z ", "Connected to "),
        ("Połączono, ale nie udało się odczytać AVG/PEAK: ", "Connected, but failed to read AVG/PEAK: "),
        ("Nie udało się odczytać mocy SPA-1: ", "Failed to read SPA-1 power: "),
        ("Nie udało się odczytać niektórych limitów mocy: ", "Some power limits could not be read: "),
        ("Nie udało się połączyć: ", "Failed to connect: "),
        ("Błąd zamykania portu: ", "Port shutdown error: "),
        ("Nie udało się ustawić D-LEVEL: ", "Failed to set D-LEVEL: "),
        ("Nie udało się odczytać D-LEVEL: ", "Failed to read D-LEVEL: "),
        ("Nie udało się ustawić poziomu NB: ", "Failed to set NB level: "),
        ("Nie udało się ustawić poziomu DNR: ", "Failed to set DNR level: "),
        ("Nie udało się odczytać poziomu NB: ", "Failed to read NB level: "),
        ("Nie udało się odczytać poziomu DNR: ", "Failed to read DNR level: "),
        ("Błąd odczytu mierników TX CAT: ", "Error reading CAT TX meters: "),
        ("Błąd odbioru Auto Information CAT: ", "Error receiving CAT Auto Information: "),
        ("Błąd danych Auto Information CAT: ", "Invalid CAT Auto Information data: "),
        ("Nieobsługiwany kod CAT MD0: ", "Unsupported CAT MD0 code: "),
        ("Nieobsługiwany kod CAT MD1: ", "Unsupported CAT MD1 code: "),
        ("Nieoczekiwana ramka CAT częstotliwości: ", "Unexpected CAT frequency frame: "),
        ("Nieoczekiwana ramka CAT ustawień mierników: ", "Unexpected CAT meter settings frame: "),
        ("Nie udało się w pełni ustawić częstotliwości i modulacji ", "Failed to fully set frequency and modulation for "),
        ("Nie udało się odczytać mocy RF: ", "Failed to read RF power: "),
        ("Radio ustawiło ", "The radio set "),
        (" W zamiast żądanych ", " W instead of the requested "),
        ("Nie udało się potwierdzić mocy RF: ", "Failed to confirm RF power: "),
        ("Nie udało się ustawić głośności ", "Failed to set volume for "),
        ("Nie udało się potwierdzić trybu AVG/PEAK: ", "Failed to confirm AVG/PEAK mode: "),
        ("Wysłano AC103; — żądanie strojenia anteny.", "Sent AC103; antenna tuning requested."),
        ("Nie udało się uruchomić strojenia anteny: ", "Failed to start antenna tuning: "),
        ("Wysłano MS", "Sent MS"),
        ("Nieobsługiwana odpowiedź CAT mocy SPA-1: ", "Unsupported CAT SPA-1 power response: "),
        ("Nieoczekiwana odpowiedź: ", "Unexpected response: "),
        ("Przekroczono czas oczekiwania na odpowiedź CAT dla ", "Timed out waiting for a CAT response to "),
        ("Radio odrzuciło polecenie CAT ", "The radio rejected CAT command "),
        ("Nieoczekiwana ramka CAT ", "Unexpected CAT frame "),
        ("Nie znaleziono folderu Dokumenty.", "The Documents folder was not found.")
    };

    public static bool IsEnglish { get; private set; }

    public static int LoadPreferredLanguageIndex()
    {
        try
        {
            return File.Exists(PreferencePath)
                && string.Equals(File.ReadAllText(PreferencePath).Trim(), "en", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.TraceError($"Unable to read the language preference: {exception}");
            return 0;
        }
    }

    public static void SetLanguage(int languageIndex, bool savePreference)
    {
        IsEnglish = languageIndex == 1;
        if (Application.Current is { } application)
        {
            foreach (var (key, polish, english) in UiStrings)
            {
                application.Resources[key] = IsEnglish ? english : polish;
            }
        }

        if (savePreference)
        {
            SavePreferredLanguage();
        }
    }

    public static string TranslateStatus(string value)
    {
        if (!IsEnglish || string.IsNullOrEmpty(value))
        {
            return value;
        }

        var translated = value;
        foreach (var (polish, english) in StatusTranslations)
        {
            if (string.Equals(value, polish, StringComparison.Ordinal))
            {
                translated = english;
                break;
            }

            if (value.StartsWith(polish, StringComparison.Ordinal))
            {
                translated = english + value.Substring(polish.Length);
                break;
            }
        }

        foreach (var (polish, english) in StatusTranslations)
        {
            translated = translated.Replace(polish, english, StringComparison.Ordinal);
        }

        return translated
            .Replace("Auto Information jest włączone.", "Auto Information is enabled.", StringComparison.Ordinal)
            .Replace(" Ponawianie...", " Retrying...", StringComparison.Ordinal)
            .Replace(" W zamiast żądanych ", " W instead of the requested ", StringComparison.Ordinal);
    }

    private static void SavePreferredLanguage()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, IsEnglish ? "en" : "pl");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.TraceError($"Unable to save the language preference: {exception}");
        }
    }
}
