namespace EpochAssist.Core;

/// <summary>The system clipboard (Unity's copy buffer); reads and writes never throw.</summary>
internal static class Clipboard
{
    public static string Text
    {
        get
        {
            try
            {
                return UnityEngine.GUIUtility.systemCopyBuffer ?? string.Empty;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("clipboard", $"Clipboard read failed: {e.Message}", 30);
                return string.Empty;
            }
        }
        set
        {
            try
            {
                UnityEngine.GUIUtility.systemCopyBuffer = value;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("clipboard", $"Clipboard write failed: {e.Message}", 30);
            }
        }
    }
}
