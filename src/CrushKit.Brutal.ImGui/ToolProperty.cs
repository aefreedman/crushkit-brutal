using System.Text;
using Brutal.ImGuiApi;
using Ui = global::Brutal.ImGuiApi.ImGui;

namespace CrushKit.Brutal.ImGui;

/// <summary>Application-authored help. Construct once; native hover delay and disabled-item hover are reused.</summary>
public sealed class ToolHelp
{
    public string Title { get; }
    public string Description { get; }
    public string Interaction { get; }
    public string DisabledReason { get; }
    private readonly byte[] _title, _description, _interaction, _disabledReason;

    public ToolHelp(string title, string description, string interaction = "", string disabledReason = "")
    {
        Title = title; Description = description; Interaction = interaction; DisabledReason = disabledReason;
        _title = ToolProperty.Encode(title); _description = ToolProperty.Encode(description);
        _interaction = ToolProperty.Encode(interaction); _disabledReason = ToolProperty.Encode(disabledReason);
    }

    /// <summary>Shows structured help for the preceding item, including when it is disabled.
    /// This does not enable the item or change keyboard/mouse ownership.</summary>
    public bool ShowForLastItem(bool disabled = false)
    {
        if (!Ui.IsItemHovered(ImGuiHoveredFlags.ForTooltip | ImGuiHoveredFlags.AllowWhenDisabled) || !Ui.BeginTooltip()) return false;
        try
        {
            // A new auto-sized tooltip has no useful content width yet. Wrapping to its
            // available region creates one-character columns and reinforces that size.
            var style = Ui.GetStyle();
            float available = Ui.GetIO().DisplaySize.X - 2 * (style.WindowPadding.X + style.DisplaySafeAreaPadding.X);
            float width = Math.Min(Ui.GetFontSize() * 35, Math.Max(Ui.GetFontSize(), available));
            Ui.PushTextWrapPos(style.WindowPadding.X + width);
            try { DrawContent(disabled); }
            finally { Ui.PopTextWrapPos(); }
        }
        finally { Ui.EndTooltip(); }
        return true;
    }

    /// <summary>Emit inside an application-owned tooltip/help region. Critical failures belong in visible UI too.</summary>
    public void DrawContent(bool disabled = false)
    {
        if (_title.Length > 0) Ui.TextWrapped(_title);
        if (_description.Length > 0)
        {
            if (_title.Length > 0) Ui.Separator();
            Ui.TextWrapped(_description);
        }
        if (_interaction.Length > 0)
        {
            Ui.Separator();
            Muted(_interaction);
        }
        if (disabled && _disabledReason.Length > 0)
        {
            Ui.Separator();
            Ui.TextWrapped("Unavailable"u8);
            Ui.TextWrapped(_disabledReason);
        }
    }

    /// <summary>Explicitly visible explanation for a disabled control; never tooltip-only.</summary>
    public void DrawDisabledReason()
    {
        if (_disabledReason.Length > 0) Muted(_disabledReason);
    }

    internal static void Muted(ReadOnlySpan<byte> text)
    {
        Ui.PushStyleColor(ImGuiCol.Text, Ui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        try { Ui.TextWrapped(text); }
        finally { Ui.PopStyleColor(); }
    }
}

/// <summary>Cached stable identity, primary label and quieter metadata. Values, ranges, policies and persistence
/// remain application-owned. Metadata changes require a new descriptor, not per-frame UTF-8 rebuilding.</summary>
public sealed class ToolProperty
{
    public string Id { get; }
    public string Label { get; }
    public string Units { get; }
    public string Effect { get; }
    public ToolHelp? Help { get; }
    private readonly byte[] _id, _label, _metadata;
    public ReadOnlySpan<byte> IdUtf8 => _id;
    public ReadOnlySpan<byte> LabelUtf8 => _label;
    public ReadOnlySpan<byte> MetadataUtf8 => _metadata;

    public ToolProperty(string id, string label, string units = "", string effect = "", ToolHelp? help = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Id = id; Label = label; Units = units; Effect = effect; Help = help;
        _id = Encode(id); _label = Encode(label);
        _ = Encode(units); _ = Encode(effect);
        _metadata = Encode(units.Length == 0 ? effect : effect.Length == 0 ? units : units + " · " + effect);
    }

    internal static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("Native UI text must not contain NUL.", nameof(value));
        return Encoding.UTF8.GetBytes(value);
    }
}
