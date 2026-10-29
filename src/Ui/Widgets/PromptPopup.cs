using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// What a button asks before it acts, opened beside it so the answer is where the pointer already is: a question
/// (a delete), or a one-line field (a link). Enter or its button takes it; Esc or a click outside drops it.
/// </summary>
internal sealed class PromptPopup : IPopup
{
    private const string Control = "EpochAssistPrompt";
    private const float Pad = 8f, Gap = 6f, FieldHeight = 26f, ButtonHeight = 24f, MaxWidth = 380f;

    private readonly ButtonPrompt _prompt;
    private readonly Rect _anchor;
    private readonly string _question;
    private string _text;
    private string? _error;
    private bool _focused;
    private Rect _rect, _field, _ok, _cancel;

    public PromptPopup(Rect anchor, ButtonPrompt prompt)
    {
        _anchor = anchor;
        _prompt = prompt;
        _question = Safe.Call(prompt.Question, string.Empty, "prompt.question");
        _text = prompt.Input && prompt.Initial != null ? Safe.Call(prompt.Initial, string.Empty, "prompt.initial") : string.Empty;
        Measure();
    }

    public Rect Rect => _rect;

    // The question wraps inside the popup's width.
    private static GUIStyle? s_words;
    private static GUIStyle? s_wordsFrom;
    private static GUIStyle Words
    {
        get
        {
            var from = Widgets.Style(() => Theme.Label, GUI.skin.label);
            if (s_words == null || s_wordsFrom != from) (s_words, s_wordsFrom) = (new GUIStyle(from) { wordWrap = true }, from);
            return s_words;
        }
    }

    // The question's lines, the field, the reason a link wasn't taken, then the buttons at the right.
    private void Measure()
    {
        float inner = MaxWidth - 2f * Pad;
        if (!_prompt.Input) inner = Mathf.Clamp(TextCache.Width(_question, Words) + 4f, 220f, inner);
        float h = Pad + TextCache.Height(_question, Words, inner);
        if (_prompt.Input) h += Gap + FieldHeight;
        if (_error != null) h += Gap + TextCache.Height(_error, Words, inner);
        h += Gap + ButtonHeight + Pad;
        _rect = Popups.Place(new Vector2(inner + 2f * Pad, h), _anchor);

        float y = _rect.y + Pad + TextCache.Height(_question, Words, inner);
        if (_prompt.Input)
        {
            _field = new Rect(_rect.x + Pad, y + Gap, inner, FieldHeight);
            y = _field.yMax;
        }
        if (_error != null) y += Gap + TextCache.Height(_error, Words, inner);
        float cancelWidth = Controls.ButtonWidth("Cancel"), okWidth = Controls.ButtonWidth(_prompt.Ok);
        _cancel = new Rect(_rect.xMax - Pad - cancelWidth, y + Gap, cancelWidth, ButtonHeight);
        _ok = new Rect(_cancel.x - Gap - okWidth, y + Gap, okWidth, ButtonHeight);
    }

    // The popup layer uses every click over it, so its buttons are hit here.
    public void HandleEvent(Event e)
    {
        if (e.type != EventType.MouseDown || e.button != 0) return;
        if (_ok.Contains(e.mousePosition)) Take();
        else if (_cancel.Contains(e.mousePosition)) Popups.Close(commit: false);
    }

    public bool HandleKey(Event e)
    {
        if (e.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return false;
        Take();
        return true;
    }

    // Keys go to the field; Enter takes it.
    public bool HandleNav(MenuNav nav)
    {
        if (nav.Activate) Take();
        return false;
    }

    public void Closed(bool commit)
    {
        if (GUI.GetNameOfFocusedControl() == Control) GUIUtility.keyboardControl = 0;
    }

    private void Take()
    {
        string? why = Safe.Call(() => _prompt.Submit(_text.Trim()), "couldn't be done", "prompt.submit");
        if (why == null)
        {
            Popups.Close(commit: true);
            return;
        }
        _error = why;
        Measure();
    }

    public void Draw()
    {
        if (Event.current.type == EventType.Repaint)
        {
            Theme.DrawSliced(_rect, Theme.PopupBg, Theme.ControlRadius + 1);
            float inner = _rect.width - 2f * Pad;
            float qh = TextCache.Height(_question, Words, inner);
            Widgets.Paint(new Rect(_rect.x + Pad, _rect.y + Pad, inner, qh), _question, Words);
            if (_error != null)
            {
                float top = (_prompt.Input ? _field.yMax : _rect.y + Pad + qh) + Gap;
                var warn = new GUIStyle(Words) { normal = { textColor = Controls.ConflictColor } };
                Widgets.Paint(new Rect(_rect.x + Pad, top, inner, TextCache.Height(_error, Words, inner)), _error, warn);
            }
        }
        if (_prompt.Input)
        {
            GUI.SetNextControlName(Control);
            _text = GUI.TextField(_field, _text, 2048, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
            if (!_focused && Event.current.type == EventType.Repaint)
            {
                _focused = true;
                GUI.FocusControl(Control);
            }
        }
        // Drawn only: the layer hands their clicks to HandleEvent.
        Controls.Button(_ok, _prompt.Ok, "prompt.ok", primary: true);
        Controls.Button(_cancel, "Cancel", "prompt.cancel");
    }
}
