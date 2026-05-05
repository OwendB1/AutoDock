using VRage.Input;
using VRage.ModAPI;
using IMyVrageInput = VRage.Input.IMyInput;

namespace ClientPlugin.Settings.Tools;

public struct Binding
{
    public MyKeys Key;
    public bool Ctrl;
    public bool Alt;
    public bool Shift;

    public Binding(MyKeys key, bool ctrl = false, bool alt = false, bool shift = false)
    {
        Key = key;
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
    }

    public override string ToString()
    {
        if (Key == MyKeys.None)
            return "None";
            
        var ctrl = Ctrl ? "Ctrl+" : "";
        var alt = Alt ? "Alt+" : "";
        var shift = Shift ? "Shift+" : "";
        return $"{ctrl}{alt}{shift}{Key}";
    }

    public bool IsPressed(IMyVrageInput input) => Key != MyKeys.None && AreModifiersMatch(input) && input.IsKeyPress(Key);
    public bool HasPressed(IMyVrageInput input) => Key != MyKeys.None && AreModifiersMatch(input) && input.IsNewKeyPressed(Key);

    public MyKeyboardModifiers ToKeyboardModifiers()
    {
        var modifiers = MyKeyboardModifiers.None;
        if (Ctrl)
            modifiers |= MyKeyboardModifiers.Control;
        if (Alt)
            modifiers |= MyKeyboardModifiers.Alt;
        if (Shift)
            modifiers |= MyKeyboardModifiers.Shift;
        return modifiers;
    }

    private bool AreModifiersMatch(IMyVrageInput input)
    {
        return input.IsAnyCtrlKeyPressed() == Ctrl &&
               input.IsAnyAltKeyPressed() == Alt &&
               input.IsAnyShiftKeyPressed() == Shift;
    }
}
