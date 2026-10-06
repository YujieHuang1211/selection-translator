namespace HuaCi.Selection;

public enum SelectionGestureKind { Drag, MultiClick, ShiftClick }

/// <param name="TextCursorSeen">Whether an I-beam cursor was seen at press or release. When false the caller
/// should re-check the cursor shortly after release (e.g. drags from RichEdit's line-selection margin keep an arrow cursor).</param>
public readonly record struct SelectionGesture(SelectionGestureKind Kind, int X, int Y, bool TextCursorSeen);

/// <summary>
/// Decides from raw left-button down/up events whether the user probably just selected text:
/// a drag, a double/triple click, or a shift+click. Pure logic, no Win32 calls, so it is unit-testable.
/// </summary>
public sealed class SelectionGestureDetector
{
    private readonly uint _doubleClickTime;
    private readonly int _doubleClickDistance;
    private readonly int _dragThreshold;

    private bool _isDown;
    private int _downX, _downY;
    private bool _downTextCursor;
    private bool _downShift;
    private int _clickCount;
    private int _lastDownX, _lastDownY;
    private uint _lastDownTime;

    public SelectionGestureDetector(uint doubleClickTime = 500, int doubleClickDistance = 4, int dragThreshold = 6)
    {
        _doubleClickTime = doubleClickTime;
        _doubleClickDistance = doubleClickDistance;
        _dragThreshold = dragThreshold;
    }

    public void OnLeftDown(InputEvent e)
    {
        bool sameSpot = Math.Abs(e.X - _lastDownX) <= _doubleClickDistance && Math.Abs(e.Y - _lastDownY) <= _doubleClickDistance;
        bool inTime = _clickCount > 0 && unchecked(e.Time - _lastDownTime) <= _doubleClickTime;
        _clickCount = sameSpot && inTime ? _clickCount + 1 : 1;

        _isDown = true;
        _downX = _lastDownX = e.X;
        _downY = _lastDownY = e.Y;
        _lastDownTime = e.Time;
        _downTextCursor = e.IsTextCursor;
        _downShift = e.Shift;
    }

    public SelectionGesture? OnLeftUp(InputEvent e)
    {
        if (!_isDown) return null;
        _isDown = false;

        bool textCursor = _downTextCursor || e.IsTextCursor;

        int dx = e.X - _downX, dy = e.Y - _downY;
        if (dx * dx + dy * dy >= _dragThreshold * _dragThreshold)
            return new SelectionGesture(SelectionGestureKind.Drag, e.X, e.Y, textCursor);

        if (_clickCount >= 2)
            return new SelectionGesture(SelectionGestureKind.MultiClick, e.X, e.Y, textCursor);

        if (_downShift)
            return new SelectionGesture(SelectionGestureKind.ShiftClick, e.X, e.Y, textCursor);

        return null;
    }

    /// <summary>Forget click history (e.g. after a click on our own UI).</summary>
    public void Reset()
    {
        _isDown = false;
        _clickCount = 0;
    }
}
