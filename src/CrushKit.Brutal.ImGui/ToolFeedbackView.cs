using Ui = global::Brutal.ImGuiApi.ImGui;

namespace CrushKit.Brutal.ImGui;

/// <summary>Opt-in inline feedback inside the caller's current window. Does not create windows, steal focus,
/// post events, advance clocks or mutate the buffer. Callers may wrap each notice in their own tone/style.</summary>
public static class ToolFeedbackView
{
    /// <summary>Draws a severity-labeled title, optional detail and visible optional dismissal action.
    /// Returns an action request only; apply dismissal after iterating the caller-owned feedback buffer.</summary>
    public static bool DrawNotice(ToolFeedbackNotice notice, bool dismissible)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Ui.PushID(notice.IdUtf8);
        try
        {
            Ui.TextWrapped(notice.HeadingUtf8);
            bool dismiss = false;
            if (dismissible)
            {
                ToolLayout.SameLineIfFits("Dismiss"u8);
                dismiss = Ui.Button("Dismiss"u8);
            }
            if (!notice.DetailUtf8.IsEmpty) Ui.TextWrapped(notice.DetailUtf8);
            return dismiss;
        }
        finally { Ui.PopID(); }
    }
}
