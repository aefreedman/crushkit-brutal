using System.Text;
using Brutal.ImGuiApi;
using Ui = global::Brutal.ImGuiApi.ImGui;

namespace CrushKit.Brutal.ImGui;

/// <summary>A stable application-owned category. Construct once, not while drawing.</summary>
public sealed class ToolCategory
{
    public string Id { get; }
    public string Label { get; }
    private readonly byte[] _id, _label, _selectable;
    internal ReadOnlySpan<byte> IdUtf8 => _id;
    internal ReadOnlySpan<byte> LabelUtf8 => _label;
    internal ReadOnlySpan<byte> SelectableUtf8 => _selectable;

    public ToolCategory(string id, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (id.Contains('\0') || label.Contains('\0') || label.Contains("##", StringComparison.Ordinal))
            throw new ArgumentException("Category text must not contain native label delimiters or NUL.");
        Id = id; Label = label;
        _id = Encoding.UTF8.GetBytes(id);
        _label = Encoding.UTF8.GetBytes(label);
        _selectable = Encoding.UTF8.GetBytes(label + "###category");
    }
}

/// <summary>Opt-in, immediate-mode tool composition. The caller owns its windows, selection and storage.</summary>
public static class ToolLayout
{
    /// <summary>Opens category navigation and the selected content region. Wide layouts use a sidebar;
    /// narrow layouts use a selector above content. Widths are logical ImGui units, normally derived
    /// from GetFontSize. Category IDs must be unique and stable within this panel.</summary>
    /// <remarks>Always dispose the returned scope, including when ContentVisible is false. Draw only
    /// the selected category inside it. Do not copy scopes or close their table/ID manually.</remarks>
    public static ToolLayoutScope Categories(ReadOnlySpan<byte> id, ReadOnlySpan<ToolCategory> categories,
        ref int selectedIndex, float minimumContentWidth)
    {
        ValidateId(id); ValidateWidth(minimumContentWidth);
        if (categories.IsEmpty) throw new ArgumentException("At least one category is required.", nameof(categories));
        if ((uint)selectedIndex >= (uint)categories.Length) throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        float widest = 0;
        foreach (var category in categories)
        {
            ArgumentNullException.ThrowIfNull(category);
            widest = Math.Max(widest, Ui.CalcTextSize(category.LabelUtf8).X);
        }
        var style = Ui.GetStyle();
        float navigationWidth = widest + style.FramePadding.X * 2;
        bool wide = Ui.GetContentRegionAvail().X >= navigationWidth + minimumContentWidth + style.CellPadding.X * 4;
        Ui.PushID(id);
        bool table = false;
        try
        {
            // Keep the same table ID in both modes so native widget identity survives resizing.
            table = Ui.BeginTable("##categories"u8, wide ? 2 : 1, ImGuiTableFlags.SizingFixedFit);
            if (!table) return new(false, wide, false);
            if (wide)
            {
                Ui.TableSetupColumn("Navigation"u8, ImGuiTableColumnFlags.WidthFixed, navigationWidth);
                Ui.TableSetupColumn("Content"u8, ImGuiTableColumnFlags.WidthStretch);
                Ui.TableNextRow(); Ui.TableNextColumn();
                for (int i = 0; i < categories.Length; i++)
                    if (SelectCategory(categories[i], i == selectedIndex)) selectedIndex = i;
                bool visible = Ui.TableNextColumn();
                return new(visible, true, true);
            }
            Ui.TableSetupColumn("Content"u8, ImGuiTableColumnFlags.WidthStretch);
            Ui.TableNextRow(); Ui.TableNextColumn();
            Ui.SetNextItemWidth(-1);
            if (Ui.BeginCombo("##category"u8, categories[selectedIndex].LabelUtf8, ImGuiComboFlags.None))
            {
                try
                {
                    for (int i = 0; i < categories.Length; i++)
                        if (SelectCategory(categories[i], i == selectedIndex)) selectedIndex = i;
                }
                finally { Ui.EndCombo(); }
            }
            // The combo preview may elide a long name; the selected category remains readable.
            Ui.TextWrapped(categories[selectedIndex].LabelUtf8);
            Ui.Separator();
            Ui.TableNextRow();
            bool contentVisible = Ui.TableNextColumn();
            return new(contentVisible, false, true);
        }
        catch
        {
            try { if (table) Ui.EndTable(); }
            finally { Ui.PopID(); }
            throw;
        }
    }

    /// <summary>Opens a wrapped label and full-width control region. At small widths the control
    /// stacks below its label. The label column uses minimumControlWidth too: use the same value
    /// for sibling rows to align them. Emit widgets with stable hidden IDs (for example ##value).</summary>
    /// <remarks>Dispose after drawing the control. Help is attached to the label, not a substitute
    /// for an always-visible validation warning. A row does not own/edit the supplied value.</remarks>
    public static ToolLayoutScope PropertyRow(ReadOnlySpan<byte> id, ReadOnlySpan<byte> label,
        float minimumControlWidth, ReadOnlySpan<byte> help = default) =>
        PropertyRowCore(id, label, minimumControlWidth, help, default, null);

    /// <summary>Cached application label, subdued units/effect metadata and structured help.
    /// This overload does not infer ranges/types, disable widgets or own the edited value.</summary>
    public static ToolLayoutScope PropertyRow(ToolProperty property, float minimumControlWidth)
    {
        ArgumentNullException.ThrowIfNull(property);
        return PropertyRowCore(property.IdUtf8, property.LabelUtf8, minimumControlWidth,
            default, property.MetadataUtf8, property.Help);
    }

    private static ToolLayoutScope PropertyRowCore(ReadOnlySpan<byte> id, ReadOnlySpan<byte> label,
        float minimumControlWidth, ReadOnlySpan<byte> help, ReadOnlySpan<byte> metadata, ToolHelp? structuredHelp)
    {
        ValidateId(id); ValidateWidth(minimumControlWidth);
        var style = Ui.GetStyle();
        // Equal label/control minimums give sibling rows a shared alignment without measuring
        // their entire schema. Long labels wrap; their text cannot move the next control.
        float labelWidth = minimumControlWidth;
        bool wide = Ui.GetContentRegionAvail().X >= labelWidth + minimumControlWidth + style.CellPadding.X * 4;
        Ui.PushID(id);
        bool table = false;
        try
        {
            // One or two columns, but the same table ID and ID-stack depth in either mode.
            table = Ui.BeginTable("##property"u8, wide ? 2 : 1, ImGuiTableFlags.SizingFixedFit);
            if (!table) return new(false, wide, false);
            if (wide) Ui.TableSetupColumn("Label"u8, ImGuiTableColumnFlags.WidthFixed, labelWidth);
            Ui.TableSetupColumn("Control"u8, ImGuiTableColumnFlags.WidthStretch);
            Ui.TableNextRow(); Ui.TableNextColumn();
            Ui.AlignTextToFramePadding();
            Ui.TextWrapped(label);
            if (!help.IsEmpty) Ui.SetItemTooltip(help);
            structuredHelp?.ShowForLastItem();
            if (!metadata.IsEmpty)
            {
                ToolHelp.Muted(metadata);
                structuredHelp?.ShowForLastItem();
            }
            if (!wide) Ui.TableNextRow();
            bool visible = Ui.TableNextColumn();
            Ui.SetNextItemWidth(-1);
            return new(visible, wide, table);
        }
        catch
        {
            try { if (table) Ui.EndTable(); }
            finally { Ui.PopID(); }
            throw;
        }
    }

    /// <summary>Compact section heading with optional wrapped explanation.</summary>
    public static void Heading(ReadOnlySpan<byte> title, ReadOnlySpan<byte> description = default)
    {
        Ui.SeparatorText(title);
        if (!description.IsEmpty) Ui.TextWrapped(description);
    }

    /// <summary>Places the next ordinary button on the previous item's line only if it fits.
    /// Extra width accommodates controls whose native decoration is wider than a button.</summary>
    public static void SameLineIfFits(ReadOnlySpan<byte> label, float extraWidth = 0)
    {
        if (!float.IsFinite(extraWidth) || extraWidth < 0) throw new ArgumentOutOfRangeException(nameof(extraWidth));
        var style = Ui.GetStyle();
        float required = Ui.CalcTextSize(label).X + style.FramePadding.X * 2 + extraWidth;
        float right = Ui.GetCursorScreenPos().X + Ui.GetContentRegionAvail().X;
        if (Ui.GetItemRectMax().X + style.ItemSpacing.X + required <= right) Ui.SameLine();
    }

    private static bool SelectCategory(ToolCategory category, bool selected)
    {
        Ui.PushID(category.IdUtf8);
        try { return Ui.Selectable(category.SelectableUtf8, selected, ImGuiSelectableFlags.None, default); }
        finally { Ui.PopID(); }
    }

    private static void ValidateWidth(float width)
    {
        if (!float.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
    }

    private static void ValidateId(ReadOnlySpan<byte> id)
    {
        if (id.IsEmpty || id.Contains((byte)0)) throw new ArgumentException("Expected a nonempty UTF-8 ID without NUL.", nameof(id));
    }
}

/// <summary>A borrowed native table/ID scope, valid only inside its current ImGui window/frame.
/// Use a local using declaration; do not copy, retain, or manually end the underlying scope.</summary>
public ref struct ToolLayoutScope
{
    public bool ContentVisible { get; }
    public bool IsWide { get; }
    private bool _active, _table;

    internal ToolLayoutScope(bool contentVisible, bool isWide, bool table)
    {
        ContentVisible = contentVisible; IsWide = isWide;
        _active = true; _table = table;
    }

    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        try { if (_table) { _table = false; Ui.EndTable(); } }
        finally { Ui.PopID(); }
    }
}
