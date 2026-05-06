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
    private Binding previousPairKeybind = new Binding(MyKeys.Up, ctrl: true);
    private Binding nextPairKeybind = new Binding(MyKeys.Down, ctrl: true);
    private Binding previousConnectorKeybind = new Binding(MyKeys.Up, alt: true);
    private Binding nextConnectorKeybind = new Binding(MyKeys.Down, alt: true);
    private Binding saveAlignmentKeybind = new Binding(MyKeys.L, ctrl: true);
    private Binding removeAlignmentKeybind = new Binding(MyKeys.K, ctrl: true);
    private List<SavedConnectorAlignment> savedAlignments = new List<SavedConnectorAlignment>();

    #endregion

    #region User interface

    public readonly string Title = "AutoDock";

    [Separator("Docking")]

    [Slider(1f, 30f, 0.5f, SliderAttribute.SliderType.Float, label: "Connector search radius", description: "Meters from each controlled-grid connector to scan for compatible connector pairs.")]
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

    [Keybind(label: "Save pair alignment", description: "Save current relative alignment for currently locked connector pair. AutoDock will prefer it for this pair.")]
    public Binding SaveAlignmentKeybind
    {
        get => saveAlignmentKeybind;
        set => SetField(ref saveAlignmentKeybind, value);
    }

    [Keybind(label: "Remove saved alignment", description: "Remove saved alignment for currently locked connector pair or open saved alignment removal list.")]
    public Binding RemoveAlignmentKeybind
    {
        get => removeAlignmentKeybind;
        set => SetField(ref removeAlignmentKeybind, value);
    }

    public List<SavedConnectorAlignment> SavedAlignments
    {
        get => savedAlignments ?? (savedAlignments = new List<SavedConnectorAlignment>());
        set => savedAlignments = value ?? new List<SavedConnectorAlignment>();
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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
