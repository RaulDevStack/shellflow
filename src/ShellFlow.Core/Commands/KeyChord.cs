using System.Diagnostics.CodeAnalysis;

namespace ShellFlow.Core.Commands;

/// <summary>
/// A keyboard shortcut. <see cref="Code"/> uses DOM <c>KeyboardEvent.code</c> names ("KeyP", "Digit1", "ArrowLeft"),
/// which the terminal renderer reports directly and the native UI maps from virtual keys.
/// </summary>
public readonly record struct KeyChord(bool Ctrl, bool Alt, bool Shift, string Code)
{
    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = "ArrowLeft",
        ["Right"] = "ArrowRight",
        ["Up"] = "ArrowUp",
        ["Down"] = "ArrowDown",
        ["Tab"] = "Tab",
        ["Enter"] = "Enter",
        ["Esc"] = "Escape",
        ["Escape"] = "Escape",
        ["Space"] = "Space",
        ["Backspace"] = "Backspace",
        ["Delete"] = "Delete",
        ["Del"] = "Delete",
        ["Insert"] = "Insert",
        ["Ins"] = "Insert",
        ["Home"] = "Home",
        ["End"] = "End",
        ["PageUp"] = "PageUp",
        ["PgUp"] = "PageUp",
        ["PageDown"] = "PageDown",
        ["PgDn"] = "PageDown",
        [","] = "Comma",
        ["Comma"] = "Comma",
        ["."] = "Period",
        ["Period"] = "Period",
        ["="] = "Equal",
        ["Plus"] = "Equal",
        ["Equal"] = "Equal",
        ["-"] = "Minus",
        ["Minus"] = "Minus",
        ["["] = "BracketLeft",
        ["BracketLeft"] = "BracketLeft",
        ["]"] = "BracketRight",
        ["BracketRight"] = "BracketRight",
        [";"] = "Semicolon",
        ["Semicolon"] = "Semicolon",
        ["'"] = "Quote",
        ["Quote"] = "Quote",
        ["/"] = "Slash",
        ["Slash"] = "Slash",
        ["\\"] = "Backslash",
        ["Backslash"] = "Backslash",
        ["`"] = "Backquote",
        ["Backquote"] = "Backquote",
        ["ArrowLeft"] = "ArrowLeft",
        ["ArrowRight"] = "ArrowRight",
        ["ArrowUp"] = "ArrowUp",
        ["ArrowDown"] = "ArrowDown",
    };

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["ArrowLeft"] = "Left",
        ["ArrowRight"] = "Right",
        ["ArrowUp"] = "Up",
        ["ArrowDown"] = "Down",
        ["Comma"] = ",",
        ["Period"] = ".",
        ["Equal"] = "=",
        ["Minus"] = "-",
        ["BracketLeft"] = "[",
        ["BracketRight"] = "]",
        ["Semicolon"] = ";",
        ["Quote"] = "'",
        ["Slash"] = "/",
        ["Backslash"] = "\\",
        ["Backquote"] = "`",
        ["Escape"] = "Esc",
    };

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"\"{text}\" is not a valid shortcut.");

    public static bool TryParse([NotNullWhen(true)] string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
        {
            return false;
        }

        bool ctrl = false, alt = false, shift = false;
        string? code = null;

        // "Ctrl++" is accepted as Ctrl+Plus.
        var normalized = text.Trim().EndsWith("++", StringComparison.Ordinal) ? text.Trim()[..^1] + "Plus" : text.Trim();
        foreach (var raw in normalized.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                return false;
            }

            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
            else if (code is null && TryParseKey(part, out var parsed))
            {
                code = parsed;
            }
            else
            {
                return false;
            }
        }

        if (code is null)
        {
            return false;
        }

        chord = new KeyChord(ctrl, alt, shift, code);
        return true;
    }

    /// <summary>True for codes the parser can produce (used to validate codes reported by the renderer).</summary>
    public static bool IsKnownCode(string? code) => code is not null && TryParseKey(ToDisplayKey(code), out var parsed) && parsed == code;

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        parts.Add(ToDisplayKey(Code));
        return string.Join('+', parts);
    }

    private static string ToDisplayKey(string code)
    {
        if (DisplayNames.TryGetValue(code, out var name))
        {
            return name;
        }

        if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4)
        {
            return code[3..];
        }

        if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6)
        {
            return code[5..];
        }

        return code;
    }

    private static bool TryParseKey(string part, [NotNullWhen(true)] out string? code)
    {
        code = null;
        if (part.Length == 1 && char.IsAsciiLetter(part[0]))
        {
            code = "Key" + char.ToUpperInvariant(part[0]);
            return true;
        }

        if (part.Length == 1 && char.IsAsciiDigit(part[0]))
        {
            code = "Digit" + part;
            return true;
        }

        if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part.AsSpan(1), out var f) && f is >= 1 and <= 24)
        {
            code = "F" + f;
            return true;
        }

        if (NamedKeys.TryGetValue(part, out var named))
        {
            code = named;
            return true;
        }

        return false;
    }
}
