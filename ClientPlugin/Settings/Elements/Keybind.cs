using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using ClientPlugin.Settings.Tools;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.Game;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;

namespace ClientPlugin.Settings.Elements;

internal class KeybindAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    private Func<Binding> propertyGetter;
    private Action<Binding> propertySetter;

    public KeybindAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        this.propertyGetter = () => (Binding)propertyGetter();
        this.propertySetter = b => propertySetter(b);

        var binding = this.propertyGetter();

        var label = new MyGuiControlLabel(text: Tools.Tools.GetLabelOrDefault(name, Label));

        var control = new MyControl(
            MyStringId.GetOrCompute($"{name.Replace(" ", "")}Keybind"),
            MyStringId.GetOrCompute(name),
            MyGuiControlTypeEnum.General,
            null,
            binding.Key,
            keyModifiers: binding.ToKeyboardModifiers());

        StringBuilder output = null;
        control.AppendBoundButtonNames(ref output, MyGuiInputDeviceEnum.Keyboard);
        MyControl.AppendUnknownTextIfNeeded(ref output, MyTexts.GetString(MyCommonTexts.UnknownControl_None));

        var button = new MyGuiControlButton(
            text: output,
            onButtonClick: OnRebindClick,
            onSecondaryButtonClick: OnUnbindClick,
            toolTip: Description)
        {
            VisualStyle = MyGuiControlButtonStyleEnum.ControlSetting,
            UserData = new ControlButtonData(control, MyGuiInputDeviceEnum.Keyboard),
        };

        return new List<Control>()
        {
            new Control(label, minWidth: Control.LabelMinWidth),
            new Control(button),
        };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(Binding)
    };

    private class ControlButtonData
    {
        public readonly MyControl Control;
        public readonly MyGuiInputDeviceEnum Device;

        public ControlButtonData(MyControl control, MyGuiInputDeviceEnum device)
        {
            Control = control;
            Device = device;
        }
    }

    private void OnRebindClick(MyGuiControlButton button)
    {
        var userData = (ControlButtonData)button.UserData;
        var messageText = MyCommonTexts.AssignControlKeyboard;
        if (userData.Device == MyGuiInputDeviceEnum.Mouse)
            messageText = MyCommonTexts.AssignControlMouse;

        // KEEN!!! MyGuiScreenOptionsMouseKeyboard.MyGuiControlAssignKeyMessageBox is PRIVATE!
        var screenClass = typeof(MyGuiScreenOptionsMouseKeyboard).GetNestedType(
            "MyGuiControlAssignKeyMessageBox",
            BindingFlags.NonPublic);

        var editBindingDialog = (MyGuiScreenBase)Activator.CreateInstance(
            screenClass,
            BindingFlags.CreateInstance,
            null,
            new object[]
            {
                userData.Device,
                userData.Control,
                messageText
            },
            null);

        editBindingDialog.Closed += (s, isUnloading) => StoreControl(button);
        MyGuiSandbox.AddScreen(editBindingDialog);
    }

    private void OnUnbindClick(MyGuiControlButton button)
    {
        void Callback(MyGuiScreenMessageBox.ResultEnum result)
        {
            if (result == MyGuiScreenMessageBox.ResultEnum.NO)
                return;

            var userData = (ControlButtonData)button.UserData;
            userData.Control.SetControl(userData.Device, MyKeys.None);

            StoreControl(button);
        }

        MyGuiScreenBase alert = MyGuiSandbox.CreateMessageBox(
            MyMessageBoxStyleEnum.Info,
            buttonType: MyMessageBoxButtonsType.YES_NO,
            messageText: new StringBuilder("Are you sure?"),
            messageCaption: new StringBuilder("UNBIND CONTROL"),
            yesButtonText: MyStringId.GetOrCompute("Confirm"),
            noButtonText: MyStringId.GetOrCompute("Cancel"),
            callback: Callback
        );

        MyGuiSandbox.AddScreen(alert);
    }

    private void StoreControl(MyGuiControlButton button)
    {
        StringBuilder output = null;
        var userData = (ControlButtonData)button.UserData;
        userData.Control.AppendBoundButtonNames(ref output, userData.Device);

        var binding = propertyGetter();
        binding.Key = userData.Control.GetKeyboardControl();
        ApplyKeyboardModifiers(ref binding, userData.Control.GetKeyboardModifier());
        propertySetter(binding);

        MyControl.AppendUnknownTextIfNeeded(ref output, MyTexts.GetString(MyCommonTexts.UnknownControl_None));
        button.Text = output.ToString();
        output.Clear();
    }

    private static void ApplyKeyboardModifiers(ref Binding binding, MyKeyboardModifiers modifiers)
    {
        binding.Ctrl = HasAny(modifiers, MyKeyboardModifiers.Control, MyKeyboardModifiers.LeftControl, MyKeyboardModifiers.RightControl);
        binding.Alt = HasAny(modifiers, MyKeyboardModifiers.Alt, MyKeyboardModifiers.LeftAlt, MyKeyboardModifiers.RightAlt);
        binding.Shift = HasAny(modifiers, MyKeyboardModifiers.Shift, MyKeyboardModifiers.LeftShift, MyKeyboardModifiers.RightShift);
    }

    private static bool HasAny(MyKeyboardModifiers value, params MyKeyboardModifiers[] flags)
    {
        foreach (MyKeyboardModifiers flag in flags)
        {
            if ((value & flag) != MyKeyboardModifiers.None)
                return true;
        }

        return false;
    }
}
