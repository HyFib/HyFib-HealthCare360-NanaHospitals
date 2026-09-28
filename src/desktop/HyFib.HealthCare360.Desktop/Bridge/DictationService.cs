using System.Globalization;
using System.Speech.Recognition;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// Offline dictation with the Windows speech recogniser (no audio leaves the workstation). Prefers the requested
/// culture (en-IN), then any English recogniser installed.
/// </summary>
internal static class DictationService
{
    public static Task<string> DictateAsync(string language) => Task.Run(() => Dictate(language));

    private static string Dictate(string language)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var recognizer = installed.FirstOrDefault(r => string.Equals(r.Culture.Name, language, StringComparison.OrdinalIgnoreCase))
                         ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == new CultureInfo(language).TwoLetterISOLanguageName)
                         ?? installed.FirstOrDefault();
        if (recognizer is null)
        {
            throw new BridgeException("not_available", "Speech recognition is not installed on this computer (Windows Settings > Time & language > Speech).");
        }

        using var engine = new SpeechRecognitionEngine(recognizer);
        try
        {
            engine.SetInputToDefaultAudioDevice();
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "No microphone for dictation");
            throw new BridgeException("no_microphone", "No microphone was found. Connect a headset and try again.");
        }

        engine.LoadGrammar(new DictationGrammar());
        engine.InitialSilenceTimeout = TimeSpan.FromSeconds(8);
        engine.EndSilenceTimeout = TimeSpan.FromSeconds(1.5);
        var result = engine.Recognize(TimeSpan.FromSeconds(60));
        return result?.Text ?? string.Empty;
    }
}
