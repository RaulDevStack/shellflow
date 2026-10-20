using Microsoft.UI.Input;
using ShellFlow.Core.Commands;
using Windows.System;
using Windows.UI.Core;

namespace ShellFlow.App.Services;

/// <summary>Maps native key events to <see cref="KeyChord"/> codes so XAML and the renderer share one shortcut table.</summary>
public static class KeyMapper
{
    public static KeyChord? FromVirtualKey(VirtualKey key)
    {
        var code = ToCode(key);
        if (code is null)
        {
            return null;
        }

        return new KeyChord(IsDown(VirtualKey.Control), IsDown(VirtualKey.Menu), IsDown(VirtualKey.Shift), code);
    }

    public static string? ToCode(VirtualKey key)
    {
        var k = (int)key;
        if (k is >= (int)VirtualKey.A and <= (int)VirtualKey.Z)
        {
            return "Key" + (char)k;
        }

        if (k is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
        {
            return "Digit" + (char)k;
        }

        if (k is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24)
        {
            return "F" + (k - (int)VirtualKey.F1 + 1);
        }

        return key switch
        {
            VirtualKey.Left => "ArrowLeft",
            VirtualKey.Right => "ArrowRight",
            VirtualKey.Up => "ArrowUp",
            VirtualKey.Down => "ArrowDown",
            VirtualKey.Tab => "Tab",
            VirtualKey.Enter => "Enter",
            VirtualKey.Escape => "Escape",
            VirtualKey.Space => "Space",
            VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            (VirtualKey)186 => "Semicolon",
            (VirtualKey)187 => "Equal",
            (VirtualKey)188 => "Comma",
            (VirtualKey)189 => "Minus",
            (VirtualKey)190 => "Period",
            (VirtualKey)191 => "Slash",
            (VirtualKey)192 => "Backquote",
            (VirtualKey)219 => "BracketLeft",
            (VirtualKey)220 => "Backslash",
            (VirtualKey)221 => "BracketRight",
            (VirtualKey)222 => "Quote",
            _ => null,
        };
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
