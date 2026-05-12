using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using ClientPlugin.Settings.Tools;
using VRage.Input;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private float connectorSearchRadius = 5f;
    private Binding activationKeybind = new Binding(MyKeys.P, ctrl: true);
    private Binding landingActivationKeybind = new Binding(MyKeys.P, alt: true);
    private Binding previousPairKeybind = new Binding(MyKeys.Up, ctrl: true);
    private Binding nextPairKeybind = new Binding(MyKeys.Down, ctrl: true);
    private Binding previousConnectorKeybind = new Binding(MyKeys.Up, alt: true);
    private Binding nextConnectorKeybind = new Binding(MyKeys.Down, alt: true);
    private Binding rotateAlignmentKeybind = new Binding(MyKeys.R, ctrl: true);
    private int autoDockTimeoutFrames = 60 * 15;
    private double autoDockPositionTolerance = 0.12;
    private double autoDockOrientationTolerance = 0.01;
    private double autoDockControlStepSeconds = 1.0 / 60.0;
    private double autoDockLinearProportionalGain = 0.65;
    private double autoDockLinearIntegralGain = 0.22;
    private double autoDockLinearDerivativeGain = 1.35;
    private double autoDockLinearIntegralLimit = 6.0;
    private double autoDockLinearIntegralActivationDistance = 5.0;
    private double autoDockLinearBrakePadding = 0.05;
    private double autoDockMaxLinearAcceleration = 4.0;
    private double autoDockFinalApproachMaxLinearAcceleration = 3.0;
    private double autoDockFinalApproachDistance = 2.0;
    private double autoDockFinalApproachEntryDistanceTolerance = 0.35;
    private double autoDockFinalApproachPlanarTolerance = 0.25;
    private double autoDockFinalApproachOrientationThreshold = 0.35;
    private double autoDockAngularProportionalGain = 2.0;
    private double autoDockAngularIntegralGain = 0.08;
    private double autoDockAngularIntegralLimit = 0.35;
    private double autoDockMaxAngularVelocity = 0.65;
    private double autoDockAngularVelocityGain = 1.8;
    private double autoDockAngularSofteningThreshold = 0.35;
    private float autoDockAngularMinTorque = 0.35f;
    private float autoDockAngularMaxTorque = 0.8f;

    #endregion

    #region User interface

    public readonly string Title = "AutoDock";

    [Separator("Docking")]

    [Slider(1f, 30f, 0.5f, label: "Connector search radius", description: "Meters from each controlled-grid connector to scan for compatible connector pairs.")]
    public float ConnectorSearchRadius
    {
        get => connectorSearchRadius;
        set => SetField(ref connectorSearchRadius, value);
    }

    [Separator("Controls")]

    [Keybind(label: "Activate docking", description: "Press once to preview connector pairs. Press again to connect selected pair.")]
    public Binding ActivationKeybind
    {
        get => activationKeybind;
        set => SetField(ref activationKeybind, value);
    }

    [Keybind(label: "Activate landing", description: "Press once to preview landing gear contact points over terrain. Press again to begin landing.")]
    public Binding LandingActivationKeybind
    {
        get => landingActivationKeybind;
        set => SetField(ref landingActivationKeybind, value);
    }

    [Keybind(label: "Previous pair", description: "Select previous connector pair while AutoDock preview is active. When current connector is exhausted, continue on previous connector with possible pairs.")]
    public Binding PreviousPairKeybind
    {
        get => previousPairKeybind;
        set => SetField(ref previousPairKeybind, value);
    }

    [Keybind(label: "Next pair", description: "Select next connector pair while AutoDock preview is active. When current connector is exhausted, continue on next connector with possible pairs.")]
    public Binding NextPairKeybind
    {
        get => nextPairKeybind;
        set => SetField(ref nextPairKeybind, value);
    }

    [Keybind(label: "Previous connector", description: "Immediately jump to previous controlled-ship connector that has possible docking pairs.")]
    public Binding PreviousConnectorKeybind
    {
        get => previousConnectorKeybind;
        set => SetField(ref previousConnectorKeybind, value);
    }

    [Keybind(label: "Next connector", description: "Immediately jump to next controlled-ship connector that has possible docking pairs.")]
    public Binding NextConnectorKeybind
    {
        get => nextConnectorKeybind;
        set => SetField(ref nextConnectorKeybind, value);
    }

    [Keybind(label: "Cycle lock rotation", description: "Cycle through 8 connector lock rotation steps for selected connector pair while AutoDock preview is active.")]
    public Binding RotateAlignmentKeybind
    {
        get => rotateAlignmentKeybind;
        set => SetField(ref rotateAlignmentKeybind, value);
    }

    [Separator("Advanced")]

    [Slider(60f, 3600f, 30f, SliderAttribute.SliderType.Integer, label: "Auto-dock timeout (frames)", description: "How long AutoDock keeps trying before it gives up. 60 frames is about 1 second.")]
    public int AutoDockTimeoutFrames
    {
        get => autoDockTimeoutFrames;
        set => SetField(ref autoDockTimeoutFrames, value);
    }

    [Slider(0.01f, 1f, 0.01f, label: "Position tolerance", description: "How close the two connectors must be before AutoDock treats their position as lined up. Lower is stricter.")]
    public double AutoDockPositionTolerance
    {
        get => autoDockPositionTolerance;
        set => SetField(ref autoDockPositionTolerance, value);
    }

    [Slider(0.001f, 0.1f, 0.001f, label: "Rotation tolerance", description: "How closely the ship must match the target rotation before AutoDock treats rotation as lined up. Lower is stricter.")]
    public double AutoDockOrientationTolerance
    {
        get => autoDockOrientationTolerance;
        set => SetField(ref autoDockOrientationTolerance, value);
    }

    [Slider(0.005f, 0.1f, 0.001f, label: "Control step (seconds)", description: "Internal time step used by AutoDock control math. Smaller reacts more often, larger smooths things out.")]
    public double AutoDockControlStepSeconds
    {
        get => autoDockControlStepSeconds;
        set => SetField(ref autoDockControlStepSeconds, value);
    }

    [Separator("Linear Tuning")]

    [Slider(0f, 2f, 0.01f, label: "Proportional gain", description: "How strongly AutoDock pushes toward target based on current distance. Higher feels more aggressive.")]
    public double AutoDockLinearProportionalGain
    {
        get => autoDockLinearProportionalGain;
        set => SetField(ref autoDockLinearProportionalGain, value);
    }

    [Slider(0f, 1f, 0.01f, label: "Integral gain", description: "How much AutoDock keeps adding pressure when small position error lingers. Helps finish alignment.")]
    public double AutoDockLinearIntegralGain
    {
        get => autoDockLinearIntegralGain;
        set => SetField(ref autoDockLinearIntegralGain, value);
    }

    [Slider(0f, 3f, 0.01f, label: "Derivative gain", description: "How much AutoDock slows closing speed as it approaches target. Higher adds more damping.")]
    public double AutoDockLinearDerivativeGain
    {
        get => autoDockLinearDerivativeGain;
        set => SetField(ref autoDockLinearDerivativeGain, value);
    }

    [Slider(0f, 20f, 0.1f, label: "Integral limit", description: "Caps how much stored position correction can build up over time. Keeps long errors from overreacting.")]
    public double AutoDockLinearIntegralLimit
    {
        get => autoDockLinearIntegralLimit;
        set => SetField(ref autoDockLinearIntegralLimit, value);
    }

    [Slider(0.5f, 20f, 0.1f, label: "Integral start distance", description: "Only start building stored position correction when ship is within this many meters. Prevents buildup while far away.")]
    public double AutoDockLinearIntegralActivationDistance
    {
        get => autoDockLinearIntegralActivationDistance;
        set => SetField(ref autoDockLinearIntegralActivationDistance, value);
    }

    [Slider(0f, 1f, 0.01f, label: "Brake padding", description: "Extra safety distance reserved for braking before contact. Higher starts slowing down sooner.")]
    public double AutoDockLinearBrakePadding
    {
        get => autoDockLinearBrakePadding;
        set => SetField(ref autoDockLinearBrakePadding, value);
    }

    [Slider(0.5f, 10f, 0.1f, label: "Max acceleration", description: "Fastest translation push AutoDock may command during normal approach. Higher moves ship harder.")]
    public double AutoDockMaxLinearAcceleration
    {
        get => autoDockMaxLinearAcceleration;
        set => SetField(ref autoDockMaxLinearAcceleration, value);
    }

    [Separator("Final Approach")]

    [Slider(0.5f, 10f, 0.1f, label: "Max acceleration", description: "Fastest translation push allowed during last meters before docking. Lower makes final approach gentler.")]
    public double AutoDockFinalApproachMaxLinearAcceleration
    {
        get => autoDockFinalApproachMaxLinearAcceleration;
        set => SetField(ref autoDockFinalApproachMaxLinearAcceleration, value);
    }

    [Slider(0.2f, 10f, 0.1f, label: "Distance", description: "How far out AutoDock waits at staging point before committing to straight-in final approach.")]
    public double AutoDockFinalApproachDistance
    {
        get => autoDockFinalApproachDistance;
        set => SetField(ref autoDockFinalApproachDistance, value);
    }

    [Slider(0f, 2f, 0.01f, label: "Entry tolerance", description: "How close to final approach distance ship must be before AutoDock may switch into straight-in approach.")]
    public double AutoDockFinalApproachEntryDistanceTolerance
    {
        get => autoDockFinalApproachEntryDistanceTolerance;
        set => SetField(ref autoDockFinalApproachEntryDistanceTolerance, value);
    }

    [Slider(0.01f, 2f, 0.01f, label: "Planar tolerance", description: "How little sideways error is allowed before straight-in final approach can begin.")]
    public double AutoDockFinalApproachPlanarTolerance
    {
        get => autoDockFinalApproachPlanarTolerance;
        set => SetField(ref autoDockFinalApproachPlanarTolerance, value);
    }

    [Slider(0.01f, 1.5f, 0.01f, label: "Rotation threshold", description: "How well the ship must be rotated before straight-in final approach can begin.")]
    public double AutoDockFinalApproachOrientationThreshold
    {
        get => autoDockFinalApproachOrientationThreshold;
        set => SetField(ref autoDockFinalApproachOrientationThreshold, value);
    }

    [Separator("Angular Tuning")]

    [Slider(0f, 10f, 0.05f, label: "Proportional gain", description: "How strongly AutoDock tries to rotate toward target angle right now. Higher turns harder.")]
    public double AutoDockAngularProportionalGain
    {
        get => autoDockAngularProportionalGain;
        set => SetField(ref autoDockAngularProportionalGain, value);
    }

    [Slider(0f, 1f, 0.01f, label: "Integral gain", description: "How much AutoDock keeps adding rotation pressure when small angle error lingers.")]
    public double AutoDockAngularIntegralGain
    {
        get => autoDockAngularIntegralGain;
        set => SetField(ref autoDockAngularIntegralGain, value);
    }

    [Slider(0f, 2f, 0.01f, label: "Integral limit", description: "Caps stored rotation correction so it does not build up too far.")]
    public double AutoDockAngularIntegralLimit
    {
        get => autoDockAngularIntegralLimit;
        set => SetField(ref autoDockAngularIntegralLimit, value);
    }

    [Slider(0.05f, 3f, 0.01f, label: "Max velocity", description: "Fastest rotation speed AutoDock is allowed to aim for.")]
    public double AutoDockMaxAngularVelocity
    {
        get => autoDockMaxAngularVelocity;
        set => SetField(ref autoDockMaxAngularVelocity, value);
    }

    [Slider(0.1f, 5f, 0.05f, label: "Velocity gain", description: "How hard AutoDock pushes to reach desired rotation speed. Higher feels snappier.")]
    public double AutoDockAngularVelocityGain
    {
        get => autoDockAngularVelocityGain;
        set => SetField(ref autoDockAngularVelocityGain, value);
    }

    [Slider(0.01f, 2f, 0.01f, label: "Softening threshold", description: "Below this angle error, AutoDock starts easing off rotation torque instead of using full force.")]
    public double AutoDockAngularSofteningThreshold
    {
        get => autoDockAngularSofteningThreshold;
        set => SetField(ref autoDockAngularSofteningThreshold, value);
    }

    [Slider(0f, 1f, 0.01f, label: "Min torque", description: "Lowest rotation force AutoDock will use while fine-tuning alignment.")]
    public float AutoDockAngularMinTorque
    {
        get => autoDockAngularMinTorque;
        set => SetField(ref autoDockAngularMinTorque, value);
    }

    [Slider(0.05f, 1f, 0.01f, label: "Max torque", description: "Highest rotation force AutoDock will use while turning into alignment.")]
    public float AutoDockAngularMaxTorque
    {
        get => autoDockAngularMaxTorque;
        set => SetField(ref autoDockAngularMaxTorque, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    #endregion
}
