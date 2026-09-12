using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Gamehook.Domain.Interface;

namespace Gamehook.UI.Controls;

// Small HxD-style byte grid: address gutter + hex bytes + ASCII column. Bytes are tinted by
// whichever mapper property owns them (via IProperty.BuildRequest(), no extra region-mapping
// code needed); clicking a byte reports its owning property back through PropertyClicked.
public sealed class HexEditorView : Control
{
    private const int BytesPerRow = 16;
    private const double RowHeight = 20;
    private const double CellWidth = 24;
    private const double AddressColumnWidth = 90;
    private const double AsciiGap = 16;
    private const double AsciiCharWidth = 10;

    public static readonly StyledProperty<ReadOnlyMemory<byte>> BytesProperty =
        AvaloniaProperty.Register<HexEditorView, ReadOnlyMemory<byte>>(nameof(Bytes));

    public static readonly StyledProperty<ulong> StartingAddressProperty =
        AvaloniaProperty.Register<HexEditorView, ulong>(nameof(StartingAddress));

    public static readonly StyledProperty<ulong> AddressBaseProperty =
        AvaloniaProperty.Register<HexEditorView, ulong>(nameof(AddressBase));

    public static readonly StyledProperty<Dictionary<string, IProperty>?> PropertiesProperty =
        AvaloniaProperty.Register<HexEditorView, Dictionary<string, IProperty>?>(nameof(Properties));

    public static readonly StyledProperty<string?> RegionIdProperty =
        AvaloniaProperty.Register<HexEditorView, string?>(nameof(RegionId));

    public static readonly StyledProperty<IProperty?> SelectedPropertyProperty =
        AvaloniaProperty.Register<HexEditorView, IProperty?>(nameof(SelectedProperty));

    public static readonly StyledProperty<ObservableCollection<IProperty>?> PinnedPropertiesProperty =
        AvaloniaProperty.Register<HexEditorView, ObservableCollection<IProperty>?>(nameof(PinnedProperties));

    public static readonly StyledProperty<int> RefreshTokenProperty =
        AvaloniaProperty.Register<HexEditorView, int>(nameof(RefreshToken));

    private static readonly Typeface MonoTypeface = new("Consolas,Menlo,monospace");
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#191A1C"));
    private static readonly IBrush AlternateRowBrush = new SolidColorBrush(Color.Parse("#1D1F22"));
    private static readonly IBrush GutterBrush = new SolidColorBrush(Color.Parse("#171819"));
    private static readonly IBrush AddressBrush = new SolidColorBrush(Color.Parse("#6F9FD4"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#D9DDE3"));
    private static readonly IBrush AsciiBrush = new SolidColorBrush(Color.Parse("#AAB0B8"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#292C30")), 1);
    // Blue marks whatever is currently selected (a clicked property or a raw byte drag);
    // red marks properties pinned in the Pinned panel, regardless of selection.
    private static readonly IPen HighlightPen = new Pen(new SolidColorBrush(Color.Parse("#56A8FF")), 2);
    private static readonly IPen PinnedPropertyPen = new Pen(new SolidColorBrush(Color.Parse("#E23F3F")), 2);
    // Amber marks the byte currently being typed into, distinct from selection blue/pinned red.
    private static readonly IBrush EditingBrush = new SolidColorBrush(Color.Parse("#4D3A12"));
    private static readonly IPen EditingPen = new Pen(new SolidColorBrush(Color.Parse("#E8A33D")), 2);
    private static readonly IBrush EditingTextBrush = new SolidColorBrush(Color.Parse("#FFD98A"));

    private ReadOnlyMemory<byte> bytes = ReadOnlyMemory<byte>.Empty;
    private ulong regionStart;
    private IProperty?[] byteOwners = [];
    private Rect viewport;
    private IProperty? hoveredProperty;
    private int selectionAnchor = -1;
    private int selectionEnd = -1;
    private bool selecting;
    private readonly HashSet<IProperty> pinnedProperties = [];
    private readonly TextBlock tooltipName = new() { Foreground = new SolidColorBrush(Color.Parse("#E3E6EB")), FontWeight = FontWeight.SemiBold };
    private readonly TextBlock tooltipValue = new() { Foreground = new SolidColorBrush(Color.Parse("#82B8F2")), FontFamily = new FontFamily("Consolas,Menlo,monospace") };
    private readonly TextBlock tooltipDescription = new() { Foreground = new SolidColorBrush(Color.Parse("#9AA4B2")), TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };

    public ReadOnlyMemory<byte> Bytes
    {
        get => GetValue(BytesProperty);
        set => SetValue(BytesProperty, value);
    }

    public ulong StartingAddress
    {
        get => GetValue(StartingAddressProperty);
        set => SetValue(StartingAddressProperty, value);
    }

    // Display-only bus-address base. StartingAddress remains region-relative so selections and
    // raw writes continue using driver coordinates.
    public ulong AddressBase
    {
        get => GetValue(AddressBaseProperty);
        set => SetValue(AddressBaseProperty, value);
    }

    public Dictionary<string, IProperty>? Properties
    {
        get => GetValue(PropertiesProperty);
        set => SetValue(PropertiesProperty, value);
    }

    public string? RegionId
    {
        get => GetValue(RegionIdProperty);
        set => SetValue(RegionIdProperty, value);
    }

    public IProperty? SelectedProperty
    {
        get => GetValue(SelectedPropertyProperty);
        set => SetValue(SelectedPropertyProperty, value);
    }

    public ObservableCollection<IProperty>? PinnedProperties
    {
        get => GetValue(PinnedPropertiesProperty);
        set => SetValue(PinnedPropertiesProperty, value);
    }

    public int RefreshToken
    {
        get => GetValue(RefreshTokenProperty);
        set => SetValue(RefreshTokenProperty, value);
    }

    public event EventHandler<IProperty>? PropertyClicked;
    public event EventHandler<HexSelection>? BytesSelected;
    public event EventHandler<HexByteEditRequested>? ByteEditRequested;

    // Double-click to type a replacement hex byte. `editingText` holds 0-2 typed hex digits;
    // editing commits (raises ByteEditRequested) on Enter, Tab, losing focus, or a second typed
    // digit, and applies optimistically to the local display right away so typing feels instant -
    // ByteEditRequested's caller reports back via RevertByte if the actual device write failed.
    private int editingOffset = -1;
    private string editingText = "";
    private int offsetOfLastEdit = -1;

    // Used by MainWindow to scroll the byte grid to a property picked in the tree.
    public Rect? GetPropertyBounds(IProperty property)
    {
        var request = property.BuildRequest();
        if (request is null || request.RegionId != RegionId)
        {
            return null;
        }

        var start = (int)(request.StartingAddress - regionStart);
        var end = start + request.Length - 1;
        if (end < 0 || start >= bytes.Length)
        {
            return null;
        }

        start = Math.Max(start, 0);
        end = Math.Min(end, bytes.Length - 1);

        var startRow = start / BytesPerRow;
        var endRow = end / BytesPerRow;
        return new Rect(AddressColumnWidth, startRow * RowHeight, BytesPerRow * CellWidth, (endRow - startRow + 1) * RowHeight);
    }

    public HexEditorView()
    {
        ClipToBounds = true;
        Focusable = true;
        ToolTip.SetTip(this, new StackPanel
        {
            Spacing = 2,
            Children = { tooltipName, tooltipValue, tooltipDescription },
        });
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
        ToolTip.SetServiceEnabled(this, false);
        EffectiveViewportChanged += (_, e) => { viewport = e.EffectiveViewport; InvalidateVisual(); };
        LostFocus += (_, _) => CommitEdit();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Memory refreshes rebuild byte ownership, but must not close tooltip under pointer.
        // Region switches invalidate hovered property's location, so close only then.
        if (change.Property == RegionIdProperty)
        {
            HidePropertyToolTip();
        }

        if (change.Property == RegionIdProperty)
        {
            ClearSelection();
            CancelEdit();
        }

        if (change.Property == BytesProperty || change.Property == StartingAddressProperty || change.Property == PropertiesProperty || change.Property == RegionIdProperty || change.Property == RefreshTokenProperty)
        {
            RebuildRegion();
            UpdatePropertyToolTip();
        }

        if (change.Property == AddressBaseProperty)
        {
            InvalidateVisual();
        }

        if (change.Property == SelectedPropertyProperty && SelectedProperty is not null)
        {
            ClearSelection();
        }

        if (change.Property == SelectedPropertyProperty || change.Property == RegionIdProperty)
        {
            InvalidateVisual();
        }

        if (change.Property == PinnedPropertiesProperty)
        {
            if (change.OldValue is ObservableCollection<IProperty> oldPinned)
            {
                oldPinned.CollectionChanged -= OnPinnedPropertiesChanged;
            }

            if (change.NewValue is ObservableCollection<IProperty> newPinned)
            {
                newPinned.CollectionChanged += OnPinnedPropertiesChanged;
            }

            RebuildPinnedProperties();
            InvalidateVisual();
        }
    }

    private void OnPinnedPropertiesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildPinnedProperties();
        InvalidateVisual();
    }

    private void RebuildPinnedProperties()
    {
        pinnedProperties.Clear();
        if (PinnedProperties is { } pinned)
        {
            foreach (var property in pinned)
            {
                pinnedProperties.Add(property);
            }
        }
    }

    private void RebuildRegion()
    {
        var regionId = RegionId;

        if (Bytes.IsEmpty || regionId is null)
        {
            bytes = ReadOnlyMemory<byte>.Empty;
            regionStart = 0;
            byteOwners = [];
            ClearSelection();
            Height = 0;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        bytes = Bytes;
        regionStart = StartingAddress;
        byteOwners = new IProperty?[bytes.Length];

        if (Properties is { } properties)
        {
            foreach (var property in properties.Values)
            {
                var request = property.BuildRequest();
                if (request is null || request.RegionId != regionId)
                {
                    continue;
                }

                var start = (int)(request.StartingAddress - regionStart);
                for (var i = 0; i < request.Length && start + i < byteOwners.Length; i++)
                {
                    if (start + i >= 0)
                    {
                        byteOwners[start + i] = property;
                    }
                }
            }
        }

        var rowCount = (bytes.Length + BytesPerRow - 1) / BytesPerRow;
        Height = Math.Max(rowCount * RowHeight, 0);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rowCount = (bytes.Length + BytesPerRow - 1) / BytesPerRow;
        var width = AddressColumnWidth + BytesPerRow * CellWidth + AsciiGap + BytesPerRow * AsciiCharWidth;
        return new Size(width, rowCount * RowHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (bytes.Length == 0)
        {
            return;
        }

        var clip = viewport.Height > 0 ? viewport : Bounds;
        var firstRow = Math.Max(0, (int)(clip.Y / RowHeight) - 1);
        var lastRow = Math.Min((bytes.Length + BytesPerRow - 1) / BytesPerRow - 1, (int)((clip.Y + clip.Height) / RowHeight) + 1);

        var selected = SelectedProperty?.BuildRequest();
        var span = bytes.Span;

        context.FillRectangle(BackgroundBrush, Bounds);
        context.FillRectangle(GutterBrush, new Rect(0, 0, AddressColumnWidth - 8, Bounds.Height));
        context.DrawLine(GridPen, new Point(AddressColumnWidth - 8, 0), new Point(AddressColumnWidth - 8, Bounds.Height));

        for (var row = firstRow; row <= lastRow; row++)
        {
            var y = row * RowHeight;
            var rowStart = row * BytesPerRow;

            if (row % 2 == 1)
            {
                context.FillRectangle(AlternateRowBrush, new Rect(AddressColumnWidth - 7, y, Bounds.Width - AddressColumnWidth + 7, RowHeight));
            }

            context.DrawText(
                new FormattedText($"{AddressBase + (ulong)rowStart:X6}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 13, AddressBrush),
                new Point(0, y + 2));

            for (var col = 0; col < BytesPerRow; col++)
            {
                var offset = rowStart + col;
                if (offset >= span.Length)
                {
                    break;
                }

                var x = AddressColumnWidth + col * CellWidth;
                var owner = byteOwners[offset];

                if (owner is not null)
                {
                    context.FillRectangle(new SolidColorBrush(ColorForProperty(owner.Name), 0.35), new Rect(x, y, CellWidth - 2, RowHeight - 2));
                }

                if (owner is not null && pinnedProperties.Contains(owner))
                {
                    context.DrawRectangle(PinnedPropertyPen, new Rect(x, y, CellWidth - 2, RowHeight - 2));
                }

                var isSelectedProperty = selected is not null && owner is not null && ReferenceEquals(owner, SelectedProperty)
                    && offset >= (int)(selected.StartingAddress - regionStart) && offset < (int)(selected.StartingAddress - regionStart) + selected.Length;

                if (isSelectedProperty || (IsSelected(offset) && owner is null))
                {
                    if (owner is null)
                    {
                        context.FillRectangle(new SolidColorBrush(Color.Parse("#315B82"), 0.8), new Rect(x, y, CellWidth - 2, RowHeight - 2));
                    }

                    context.DrawRectangle(HighlightPen, new Rect(x, y, CellWidth - 2, RowHeight - 2));
                }

                if (offset == editingOffset)
                {
                    context.FillRectangle(EditingBrush, new Rect(x, y, CellWidth - 2, RowHeight - 2));
                    context.DrawRectangle(EditingPen, new Rect(x, y, CellWidth - 2, RowHeight - 2));
                }

                var hexText = offset == editingOffset ? (editingText + "__")[..2] : span[offset].ToString("X2");
                context.DrawText(
                    new FormattedText(hexText, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 13,
                        offset == editingOffset ? EditingTextBrush : TextBrush),
                    new Point(x + 2, y + 2));

                var asciiX = AddressColumnWidth + BytesPerRow * CellWidth + AsciiGap + col * AsciiCharWidth;
                var character = span[offset] is >= 0x20 and < 0x7F ? (char)span[offset] : '.';
                context.DrawText(
                    new FormattedText(character.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 13, AsciiBrush),
                    new Point(asciiX, y + 2));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var position = e.GetPosition(this);
        var offset = OffsetAt(position);

        if (editingOffset >= 0 && offset != editingOffset)
        {
            CommitEdit();
        }

        if (e.ClickCount == 2 && offset is { } editOffset && IsHexColumn(position))
        {
            BeginEdit(editOffset);
            e.Handled = true;
            return;
        }

        if (offset is { } unmappedOffset && byteOwners[unmappedOffset] is null)
        {
            selectionAnchor = unmappedOffset;
            selectionEnd = unmappedOffset;
            selecting = true;
            Focus();
            e.Pointer.Capture(this);
            RaiseSelection();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (PropertyAt(position) is { } property)
        {
            PropertyClicked?.Invoke(this, property);
        }
    }

    private bool IsHexColumn(Point position)
    {
        var relativeX = position.X - AddressColumnWidth;
        return relativeX >= 0 && relativeX < BytesPerRow * CellWidth;
    }

    private void BeginEdit(int offset)
    {
        ClearSelection();
        editingOffset = offset;
        editingText = "";
        Focus();
        InvalidateVisual();
    }

    // Applies immediately to the local display so typing feels instant; ByteEditRequested's
    // subscriber calls RevertByte if the write actually fails.
    private void CommitEdit()
    {
        if (editingOffset < 0)
        {
            return;
        }

        var offset = editingOffset;
        var text = editingText;
        editingOffset = -1;
        editingText = "";
        offsetOfLastEdit = offset;

        if (text.Length == 0 || RegionId is not { } regionId)
        {
            InvalidateVisual();
            return;
        }

        var newValue = byte.Parse(text.PadLeft(2, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var address = regionStart + (ulong)offset;
        var originalValue = bytes.Span[offset];
        if (newValue == originalValue)
        {
            InvalidateVisual();
            return;
        }

        var array = bytes.ToArray();
        array[offset] = newValue;
        bytes = array;
        InvalidateVisual();

        var owner = offset < byteOwners.Length ? byteOwners[offset] : null;
        ByteEditRequested?.Invoke(this, new HexByteEditRequested(regionId, address, originalValue, newValue, owner));
    }

    private void CancelEdit()
    {
        editingOffset = -1;
        editingText = "";
        InvalidateVisual();
    }

    // Called by the ByteEditRequested subscriber when the device write actually failed, to undo
    // the optimistic local edit. No-ops if the view has since moved on (region change, a newer
    // edit to the same byte, or a real poll already overwrote it with something else).
    public void RevertByte(string regionId, ulong address, byte originalValue)
    {
        if (RegionId != regionId || address < regionStart)
        {
            return;
        }

        var offset = (int)(address - regionStart);
        if (offset < 0 || offset >= bytes.Length)
        {
            return;
        }

        var array = bytes.ToArray();
        array[offset] = originalValue;
        bytes = array;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (editingOffset < 0)
        {
            MoveSelection(e);
            return;
        }

        if (TryGetHexDigit(e.Key, out var digit))
        {
            editingText = editingText.Length >= 2 ? digit.ToString() : editingText + digit;
            if (editingText.Length == 2)
            {
                CommitEdit();
                // auto-advance to the next byte so pasting/typing a run of bytes flows naturally
                if (editingOffset < 0 && offsetOfLastEdit + 1 < byteOwners.Length)
                {
                    BeginEdit(offsetOfLastEdit + 1);
                }
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (MoveEditingCursor(e))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Back:
                if (editingText.Length > 0) editingText = editingText[..^1];
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                CommitEdit();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;
        }
    }

    // Arrow keys retain direct-edit flow: commit any entered nibble, then put the editor on
    // adjacent byte. This is separate from MoveSelection because hex editing may cover bytes
    // that belong to mapped properties too.
    private bool MoveEditingCursor(KeyEventArgs e)
    {
        var offsetDelta = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -BytesPerRow,
            Key.Down => BytesPerRow,
            _ => 0,
        };

        if (offsetDelta == 0)
        {
            return false;
        }

        var target = editingOffset + offsetDelta;
        CommitEdit();
        if (target >= 0 && target < byteOwners.Length)
        {
            BeginEdit(target);
        }

        e.Handled = true;
        return true;
    }

    private void MoveSelection(KeyEventArgs e)
    {
        var offsetDelta = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -BytesPerRow,
            Key.Down => BytesPerRow,
            _ => 0,
        };

        if (offsetDelta == 0 || selectionEnd < 0)
        {
            return;
        }

        var target = selectionEnd + offsetDelta;
        if (target < 0 || target >= byteOwners.Length || byteOwners[target] is not null)
        {
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            SetSelectionEnd(target);
        }
        else
        {
            selectionAnchor = target;
            selectionEnd = target;
        }

        RaiseSelection();
        InvalidateVisual();
        e.Handled = true;
    }

    private static bool TryGetHexDigit(Key key, out char digit)
    {
        if (key is >= Key.D0 and <= Key.D9) { digit = (char)('0' + (key - Key.D0)); return true; }
        if (key is >= Key.NumPad0 and <= Key.NumPad9) { digit = (char)('0' + (key - Key.NumPad0)); return true; }
        if (key is >= Key.A and <= Key.F) { digit = (char)('A' + (key - Key.A)); return true; }
        digit = default;
        return false;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!selecting)
        {
            return;
        }

        selecting = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (selecting && OffsetAt(e.GetPosition(this)) is { } offset)
        {
            SetSelectionEnd(offset);
            RaiseSelection();
            InvalidateVisual();
            e.Handled = true;
        }

        var property = PropertyAt(e.GetPosition(this));
        if (ReferenceEquals(property, hoveredProperty))
        {
            return;
        }

        HidePropertyToolTip();
        hoveredProperty = property;
        UpdatePropertyToolTip();
        ToolTip.SetIsOpen(this, property is not null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HidePropertyToolTip();
    }

    private IProperty? PropertyAt(Point position)
    {
        var offset = OffsetAt(position);
        return offset is { } value ? byteOwners[value] : null;
    }

    private int? OffsetAt(Point position)
    {
        var relativeX = position.X - AddressColumnWidth;
        if (position.Y < 0 || position.Y >= ((bytes.Length + BytesPerRow - 1) / BytesPerRow) * RowHeight)
        {
            return null;
        }

        var col = -1;
        if (relativeX >= 0 && relativeX < BytesPerRow * CellWidth)
        {
            col = (int)(relativeX / CellWidth);
        }
        else
        {
            var relativeAsciiX = position.X - AddressColumnWidth - BytesPerRow * CellWidth - AsciiGap;
            if (relativeAsciiX >= 0 && relativeAsciiX < BytesPerRow * AsciiCharWidth)
            {
                col = (int)(relativeAsciiX / AsciiCharWidth);
            }
        }

        if (col < 0)
        {
            return null;
        }

        var row = (int)(position.Y / RowHeight);
        var offset = row * BytesPerRow + col;
        return offset >= 0 && offset < byteOwners.Length ? offset : null;
    }

    private bool IsSelected(int offset) => selectionAnchor >= 0 &&
        offset >= Math.Min(selectionAnchor, selectionEnd) && offset <= Math.Max(selectionAnchor, selectionEnd);

    private void SetSelectionEnd(int offset)
    {
        var direction = Math.Sign(offset - selectionAnchor);
        var end = selectionAnchor;
        while (end != offset)
        {
            var next = end + direction;
            if (byteOwners[next] is not null)
            {
                break;
            }

            end = next;
        }

        selectionEnd = end;
    }

    private void ClearSelection()
    {
        selectionAnchor = -1;
        selectionEnd = -1;
        selecting = false;
    }

    private void RaiseSelection()
    {
        if (selectionAnchor < 0 || selectionEnd < 0 || RegionId is null)
        {
            return;
        }

        var start = Math.Min(selectionAnchor, selectionEnd);
        var end = Math.Max(selectionAnchor, selectionEnd);
        BytesSelected?.Invoke(this, new HexSelection(
            RegionId,
            regionStart + (ulong)start,
            bytes.Slice(start, end - start + 1)));
    }

    private void HidePropertyToolTip()
    {
        ToolTip.SetIsOpen(this, false);
        hoveredProperty = null;
    }

    private void UpdatePropertyToolTip()
    {
        if (hoveredProperty is not { } property)
        {
            return;
        }

        tooltipName.Text = property.Name;
        tooltipValue.Text = $"Value: {FormatValue(property.Value)}";
        tooltipDescription.Text = property.Description;
        tooltipDescription.IsVisible = !string.IsNullOrEmpty(property.Description);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static Color ColorForProperty(string name)
    {
        var hash = 0;
        foreach (var character in name)
        {
            hash = hash * 31 + character;
        }

        var hue = (uint)hash % 360;
        return new HsvColor(1.0, hue, 0.55, 0.85).ToRgb();
    }
}

public sealed record HexSelection(string RegionId, ulong StartingAddress, ReadOnlyMemory<byte> Bytes);

public sealed record HexByteEditRequested(string RegionId, ulong Address, byte OriginalValue, byte NewValue, IProperty? Owner);
