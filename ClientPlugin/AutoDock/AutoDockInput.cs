using ClientPlugin.Settings;
using ClientPlugin.Settings.Tools;
using Sandbox.Graphics.GUI;
using VRage.Input;
using VRage.Utils;

namespace ClientPlugin;

internal sealed class AutoDockInput
{
    private static readonly MyStringId ActivationControlId = MyStringId.GetOrCompute("AutoDock.ActivationKeybind");
    private static readonly MyStringId PreviousPairControlId = MyStringId.GetOrCompute("AutoDock.PreviousPairKeybind");
    private static readonly MyStringId NextPairControlId = MyStringId.GetOrCompute("AutoDock.NextPairKeybind");
    private static readonly MyStringId PreviousConnectorControlId = MyStringId.GetOrCompute("AutoDock.PreviousConnectorKeybind");
    private static readonly MyStringId NextConnectorControlId = MyStringId.GetOrCompute("AutoDock.NextConnectorKeybind");
    private static readonly MyStringId RotateAlignmentControlId = MyStringId.GetOrCompute("AutoDock.RotateAlignmentKeybind");

    private Binding registeredActivationKeybind = new Binding(MyKeys.None);
    private Binding registeredPreviousPairKeybind = new Binding(MyKeys.None);
    private Binding registeredNextPairKeybind = new Binding(MyKeys.None);
    private Binding registeredPreviousConnectorKeybind = new Binding(MyKeys.None);
    private Binding registeredNextConnectorKeybind = new Binding(MyKeys.None);
    private Binding registeredRotateAlignmentKeybind = new Binding(MyKeys.None);
    private MyControl activationControl;
    private MyControl previousPairControl;
    private MyControl nextPairControl;
    private MyControl previousConnectorControl;
    private MyControl nextConnectorControl;
    private MyControl rotateAlignmentControl;

    public void EnsureRegistered(IMyInput input)
    {
        bool changed = false;
        changed |= EnsureGameControl(
            input,
            ActivationControlId,
            "AutoDock Activate Docking",
            Config.Current.ActivationKeybind,
            ref registeredActivationKeybind,
            ref activationControl);
        changed |= EnsureGameControl(
            input,
            PreviousPairControlId,
            "AutoDock Previous Pair",
            Config.Current.PreviousPairKeybind,
            ref registeredPreviousPairKeybind,
            ref previousPairControl);
        changed |= EnsureGameControl(
            input,
            NextPairControlId,
            "AutoDock Next Pair",
            Config.Current.NextPairKeybind,
            ref registeredNextPairKeybind,
            ref nextPairControl);
        changed |= EnsureGameControl(
            input,
            PreviousConnectorControlId,
            "AutoDock Previous Connector",
            Config.Current.PreviousConnectorKeybind,
            ref registeredPreviousConnectorKeybind,
            ref previousConnectorControl);
        changed |= EnsureGameControl(
            input,
            NextConnectorControlId,
            "AutoDock Next Connector",
            Config.Current.NextConnectorKeybind,
            ref registeredNextConnectorKeybind,
            ref nextConnectorControl);
        changed |= EnsureGameControl(
            input,
            RotateAlignmentControlId,
            "AutoDock Rotate Alignment",
            Config.Current.RotateAlignmentKeybind,
            ref registeredRotateAlignmentKeybind,
            ref rotateAlignmentControl);

        if (changed)
            input.CreateKeyControlsPriorityMap();
    }

    public void Unregister()
    {
        IMyInput input = MyInput.Static;
        if (input == null)
            return;

        var unbound = new Binding(MyKeys.None);
        bool changed = false;
        changed |= EnsureGameControl(input, ActivationControlId, "AutoDock Activate Docking", unbound, ref registeredActivationKeybind, ref activationControl);
        changed |= EnsureGameControl(input, PreviousPairControlId, "AutoDock Previous Pair", unbound, ref registeredPreviousPairKeybind, ref previousPairControl);
        changed |= EnsureGameControl(input, NextPairControlId, "AutoDock Next Pair", unbound, ref registeredNextPairKeybind, ref nextPairControl);
        changed |= EnsureGameControl(input, PreviousConnectorControlId, "AutoDock Previous Connector", unbound, ref registeredPreviousConnectorKeybind, ref previousConnectorControl);
        changed |= EnsureGameControl(input, NextConnectorControlId, "AutoDock Next Connector", unbound, ref registeredNextConnectorKeybind, ref nextConnectorControl);
        changed |= EnsureGameControl(input, RotateAlignmentControlId, "AutoDock Rotate Alignment", unbound, ref registeredRotateAlignmentKeybind, ref rotateAlignmentControl);

        if (changed)
            input.CreateKeyControlsPriorityMap();
    }

    public bool IsActivationPressed(IMyInput input)
    {
        return IsControlNewPressed(activationControl, Config.Current.ActivationKeybind, input);
    }

    public bool IsCyclePreviousPressed(IMyInput input)
    {
        return IsControlNewPressed(previousPairControl, Config.Current.PreviousPairKeybind, input);
    }

    public bool IsCycleNextPressed(IMyInput input)
    {
        return IsControlNewPressed(nextPairControl, Config.Current.NextPairKeybind, input);
    }

    public bool IsCyclePreviousConnectorPressed(IMyInput input)
    {
        return IsControlNewPressed(previousConnectorControl, Config.Current.PreviousConnectorKeybind, input);
    }

    public bool IsCycleNextConnectorPressed(IMyInput input)
    {
        return IsControlNewPressed(nextConnectorControl, Config.Current.NextConnectorKeybind, input);
    }

    public bool IsRotateAlignmentPressed(IMyInput input)
    {
        return IsControlNewPressed(rotateAlignmentControl, Config.Current.RotateAlignmentKeybind, input);
    }

    private static bool EnsureGameControl(
        IMyInput input,
        MyStringId controlId,
        string displayName,
        Binding binding,
        ref Binding registeredBinding,
        ref MyControl control)
    {
        if (control != null && BindingsEqual(registeredBinding, binding))
            return false;

        control = new MyControl(
            controlId,
            MyStringId.GetOrCompute(displayName),
            MyGuiControlTypeEnum.General,
            null,
            binding.Key,
            keyModifiers: binding.ToKeyboardModifiers());

        input.AddDefaultControl(controlId, control);
        registeredBinding = binding;
        return true;
    }

    private static bool IsControlNewPressed(MyControl control, Binding binding, IMyInput input)
    {
        return control != null ? control.IsNewPressed() : binding.HasPressed(input);
    }

    private static bool BindingsEqual(Binding left, Binding right)
    {
        return left.Key == right.Key
               && left.Ctrl == right.Ctrl
               && left.Alt == right.Alt
               && left.Shift == right.Shift;
    }
}
