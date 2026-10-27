using System.Globalization;
using System.Text;
using Brutal.ImGuiApi;
using Brutal.Pointers;
using Ui = global::Brutal.ImGuiApi.ImGui;

namespace CrushKit.Brutal.ImGui;

public enum ToolNumberMode { Input, Drag, Slider }

/// <summary>Explicit presentation, not a schema or validator. Construct once per field description.
/// Drag/slider bounds are useful interaction bounds; exact native text entry is deliberately not clamped.</summary>
public sealed class ToolDoubleEditor
{
    public ToolNumberMode Mode { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public float DragSpeed { get; }
    public string Format { get; }
    public string Suffix { get; }
    private readonly byte[] _format;
    internal ReadOnlySpan<byte> FormatUtf8 => _format;

    private ToolDoubleEditor(ToolNumberMode mode, double minimum, double maximum, float speed, string format, string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(format);
        // Native printf receives only one numeric value. Do not permit arbitrary conversions/arguments.
        if (format.Length < 4 || !format.StartsWith("%.", StringComparison.Ordinal) ||
            !int.TryParse(format.AsSpan(2, format.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out int digits) ||
            digits is < 0 or > 17 || format[^1] is not ('f' or 'g' or 'e') || (digits == 0 && format[^1] == 'g'))
            throw new ArgumentException("Expected a single numeric format such as %.3f or %.17g (0–17 digits; g requires at least 1).", nameof(format));
        if (mode != ToolNumberMode.Input && (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= maximum || !double.IsFinite(maximum - minimum)))
            throw new ArgumentOutOfRangeException(nameof(minimum));
        // Native slider arithmetic requires finite differences and half-range endpoints.
        if (mode == ToolNumberMode.Slider && (minimum < -double.MaxValue / 2 || maximum > double.MaxValue / 2 || !double.IsFinite(maximum - minimum)))
            throw new ArgumentOutOfRangeException(nameof(maximum));
        if (!float.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        Mode = mode; Minimum = minimum; Maximum = maximum; DragSpeed = speed;
        Format = format; Suffix = suffix; _format = ToolControls.EncodeNumericFormat(format, suffix);
    }

    /// <summary>Returns an immutable descriptor with a literal unit suffix in the native value display.</summary>
    public ToolDoubleEditor WithSuffix(string suffix) => new(Mode, Minimum, Maximum, DragSpeed, Format, suffix);

    public static ToolDoubleEditor Input(string format) => new(ToolNumberMode.Input, 0, 0, 1, format);
    public static ToolDoubleEditor Drag(double minimum, double maximum, float speed, string format) => new(ToolNumberMode.Drag, minimum, maximum, speed, format);
    public static ToolDoubleEditor Slider(double minimum, double maximum, string format) => new(ToolNumberMode.Slider, minimum, maximum, 1, format);
}

/// <summary>Exact signed 64-bit native storage. Input accepts the whole Int64 range; bounded drag/slider
/// descriptors use the native half-range restriction to avoid arithmetic overflow. No floating conversion.</summary>
public sealed class ToolInt64Editor
{
    public ToolNumberMode Mode { get; }
    public long Minimum { get; }
    public long Maximum { get; }
    public float DragSpeed { get; }
    public string Suffix { get; }
    private readonly byte[] _format;
    internal ReadOnlySpan<byte> FormatUtf8 => _format;
    private ToolInt64Editor(ToolNumberMode mode, long minimum, long maximum, float speed, string suffix = "")
    {
        if (mode != ToolNumberMode.Input && (minimum >= maximum || minimum < long.MinValue / 2 || maximum > long.MaxValue / 2))
            throw new ArgumentOutOfRangeException(nameof(minimum), "Native bounded integer controls require ordered half-range endpoints.");
        if (!float.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        Mode = mode; Minimum = minimum; Maximum = maximum; DragSpeed = speed;
        Suffix = suffix; _format = ToolControls.EncodeNumericFormat("%lld", suffix);
    }
    /// <summary>Returns an immutable descriptor with a literal unit suffix; native S64 storage is unchanged.</summary>
    public ToolInt64Editor WithSuffix(string suffix) => new(Mode, Minimum, Maximum, DragSpeed, suffix);

    public static ToolInt64Editor Input() => new(ToolNumberMode.Input, 0, 0, 1);
    public static ToolInt64Editor Drag(long minimum, long maximum, float speed) => new(ToolNumberMode.Drag, minimum, maximum, speed);
    public static ToolInt64Editor Slider(long minimum, long maximum) => new(ToolNumberMode.Slider, minimum, maximum, 1);
}

/// <summary>Public native numeric controls. The application still stages/validates the returned value.
/// Put these inside a property row; hidden widget IDs remain application-owned.</summary>
public static class ToolControls
{
    public static unsafe bool EditDouble(ReadOnlySpan<byte> id, ref double value, ToolDoubleEditor editor,
        bool enabled = true, ToolHelp? help = null, ImGuiInputTextFlags inputFlags = ImGuiInputTextFlags.None)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ValidateId(id);
        ValidateInputFlags(editor.Mode, inputFlags);
        bool changed;
        Ui.BeginDisabled(!enabled);
        try
        {
            double min = editor.Minimum, max = editor.Maximum;
            fixed (double* pointer = &value)
                changed = editor.Mode switch
                {
                    ToolNumberMode.Input => Ui.InputScalar(id, ImGuiDataType.Double, (Ptr)pointer, default, default, editor.FormatUtf8, inputFlags),
                    ToolNumberMode.Drag => Ui.DragScalar(id, ImGuiDataType.Double, (Ptr)pointer, editor.DragSpeed, (Ptr)(&min), (Ptr)(&max), editor.FormatUtf8, ImGuiSliderFlags.None),
                    _ => Ui.SliderScalar(id, ImGuiDataType.Double, (Ptr)pointer, (Ptr)(&min), (Ptr)(&max), editor.FormatUtf8, ImGuiSliderFlags.None)
                };
        }
        finally { Ui.EndDisabled(); }
        help?.ShowForLastItem(!enabled);
        if (!enabled) help?.DrawDisabledReason();
        return changed;
    }

    public static unsafe bool EditInt64(ReadOnlySpan<byte> id, ref long value, ToolInt64Editor editor,
        bool enabled = true, ToolHelp? help = null, ImGuiInputTextFlags inputFlags = ImGuiInputTextFlags.None)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ValidateId(id);
        ValidateInputFlags(editor.Mode, inputFlags);
        bool changed;
        Ui.BeginDisabled(!enabled);
        try
        {
            long min = editor.Minimum, max = editor.Maximum;
            fixed (long* pointer = &value)
                changed = editor.Mode switch
                {
                    ToolNumberMode.Input => Ui.InputScalar(id, ImGuiDataType.S64, (Ptr)pointer, default, default, editor.FormatUtf8, inputFlags),
                    ToolNumberMode.Drag => Ui.DragScalar(id, ImGuiDataType.S64, (Ptr)pointer, editor.DragSpeed, (Ptr)(&min), (Ptr)(&max), editor.FormatUtf8, ImGuiSliderFlags.None),
                    _ => Ui.SliderScalar(id, ImGuiDataType.S64, (Ptr)pointer, (Ptr)(&min), (Ptr)(&max), editor.FormatUtf8, ImGuiSliderFlags.None)
                };
        }
        finally { Ui.EndDisabled(); }
        help?.ShowForLastItem(!enabled);
        if (!enabled) help?.DrawDisabledReason();
        return changed;
    }

    internal static byte[] EncodeNumericFormat(string format, string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        foreach (char character in suffix)
            if (char.IsControl(character)) throw new ArgumentException("Unit suffixes must be single-line native text without control characters.", nameof(suffix));
        // Suffixes are literal text, never additional printf conversions (%n, %s, etc.).
        return Encoding.UTF8.GetBytes(suffix.Length == 0 ? format : format + " " + suffix.Replace("%", "%%", StringComparison.Ordinal));
    }

    private static void ValidateInputFlags(ToolNumberMode mode, ImGuiInputTextFlags flags)
    {
        if (mode != ToolNumberMode.Input && flags != ImGuiInputTextFlags.None)
            throw new ArgumentException("Input flags apply only to Input descriptors; drag/slider edits have native immediate semantics.", nameof(flags));
    }

    internal static void ValidateId(ReadOnlySpan<byte> id)
    {
        if (id.IsEmpty || id.Contains((byte)0)) throw new ArgumentException("Expected a nonempty UTF-8 ID without NUL.", nameof(id));
    }
}
