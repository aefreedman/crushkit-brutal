using Brutal.ImGuiApi;
using Ui = global::Brutal.ImGuiApi.ImGui;

namespace CrushKit.Brutal.ImGui;

public enum ToolFieldActionKind { None, Restore, Copy, Preset }
public readonly record struct ToolFieldAction(ToolFieldActionKind Kind, int PresetIndex = -1);

/// <summary>A cached display label only. The application owns the associated value/command.</summary>
public sealed class ToolFieldPreset
{
    public string Label { get; }
    private readonly byte[] _label;
    internal ReadOnlySpan<byte> LabelUtf8 => _label;
    public ToolFieldPreset(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Contains("##", StringComparison.Ordinal)) throw new ArgumentException("Preset labels must not contain native ID delimiters.", nameof(label));
        Label = label; _label = ToolProperty.Encode(label);
    }
}

/// <summary>Discoverable field actions; returns requests and never reads files, restores values,
/// writes a clipboard, executes commands or owns native/context lifetime.</summary>
public static class ToolFieldActions
{
    /// <summary>Reserve room for the visible action button before emitting the field's control.
    /// When even a frame-height control cannot fit beside it, both remain full-width/stacked instead.</summary>
    public static void ReserveControlWidth()
    {
        var style = Ui.GetStyle();
        float button = Ui.CalcTextSize("..."u8).X + style.FramePadding.X * 2;
        float available = Ui.GetContentRegionAvail().X;
        float control = available - button - style.ItemSpacing.X;
        Ui.SetNextItemWidth(control >= Ui.GetFrameHeight() ? control : -1);
    }

    /// <summary>Call immediately after the field widget (or its disabled explanation). The visible
    /// button is always present; optional right-click targets the preceding item. IDs must be stable.
    /// PresetIndex refers to this call's caller-owned preset order, not a retained native index.</summary>
    public static ToolFieldAction Draw(ReadOnlySpan<byte> id, bool canRestore, bool canCopy,
        ReadOnlySpan<ToolFieldPreset> presets = default, bool enableContextMenu = true)
    {
        ToolControls.ValidateId(id);
        bool contextRequested = enableContextMenu && Ui.IsItemClicked(ImGuiMouseButton.Right);
        Ui.PushID(id);
        try
        {
            ToolLayout.SameLineIfFits("..."u8);
            if (Ui.SmallButton("...##field-actions"u8) || contextRequested) Ui.OpenPopup("##actions"u8);
            Ui.SetItemTooltip("Field actions"u8);
            if (!Ui.BeginPopup("##actions"u8, ImGuiWindowFlags.None)) return default;
            try
            {
                if (Ui.MenuItem("Restore value"u8, default, false, canRestore)) return new(ToolFieldActionKind.Restore);
                if (Ui.MenuItem("Copy value"u8, default, false, canCopy)) return new(ToolFieldActionKind.Copy);
                if (!presets.IsEmpty) Ui.Separator();
                for (int i = 0; i < presets.Length; i++)
                {
                    ArgumentNullException.ThrowIfNull(presets[i]);
                    Ui.PushID(i);
                    try
                    {
                        if (Ui.MenuItem(presets[i].LabelUtf8, default, false, true))
                            return new(ToolFieldActionKind.Preset, i);
                    }
                    finally { Ui.PopID(); }
                }
                return default;
            }
            finally { Ui.EndPopup(); }
        }
        finally { Ui.PopID(); }
    }
}
