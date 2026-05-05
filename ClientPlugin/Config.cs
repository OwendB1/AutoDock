using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using Sandbox.Graphics.GUI;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using ClientPlugin.Settings.Tools;
using VRage.Input;
using VRageMath;


namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private float connectorSearchRadius = 5f;
    private Binding alternativeKeybind = new Binding(MyKeys.None);

    #endregion

    #region User interface

    public readonly string Title = "AutoDock";

    [Separator("Docking")]

    [Slider(1f, 10f, 0.5f, SliderAttribute.SliderType.Float, label: "Connector search radius", description: "Meters from each controlled-grid connector to scan for compatible connector pairs.")]
    public float ConnectorSearchRadius
    {
        get => connectorSearchRadius;
        set => SetField(ref connectorSearchRadius, value);
    }

    [Separator("Controls")]

    [Keybind(label: "Alternative activation key", description: "Optional extra binding. Ctrl+P is always active. Unbind by right clicking the button.")]
    public Binding AlternativeKeybind
    {
        get => alternativeKeybind;
        set => SetField(ref alternativeKeybind, value);
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
