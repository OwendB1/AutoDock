using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Utils;

namespace ClientPlugin.Settings.Elements;

internal class SliderAttribute : Attribute, IElement
{
    private static readonly FieldInfo CanHideOthersField = typeof(MyGuiScreenBase)
        .GetField("m_canHideOthers", BindingFlags.NonPublic | BindingFlags.Instance);

    public enum SliderType
    {
        Integer,
        Float,
    }

    public readonly float Min;
    public readonly float Max;
    public readonly float Step;
    public readonly SliderType Type;
    public readonly string Label;
    public readonly string Description;

    public SliderAttribute(float min, float max, float step = 1f, SliderType type = SliderType.Float, string label = null, string description = null)
    {
        Min = min;
        Max = max;
        Step = step;
        Type = type;
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        object currentValue = propertyGetter();
        Type propertyType = currentValue?.GetType() ?? typeof(float);
        var valueLabel = new MyGuiControlLabel();

        void ValueUpdate(MyGuiControlSlider element)
        {
            switch (Type)
            {
                case SliderType.Integer:
                    int intValue = Convert.ToInt32(element.Value);
                    propertySetter(intValue);
                    valueLabel.Text = intValue.ToString();
                    break;

                case SliderType.Float:
                    if (propertyType == typeof(double))
                        propertySetter((double)element.Value);
                    else
                        propertySetter(element.Value);
                    valueLabel.Text = MyValueFormatter.GetFormatedFloat(element.Value, element.LabelDecimalPlaces);
                    break;
            }
        }

        bool SpecifyValue(MyGuiControlSlider element)
        {
            object dialogValue = propertyGetter() ?? currentValue;
            var config = MySandboxGame.Config;
            MyGuiScreenDialogAmount screen = new MyGuiScreenDialogAmount(
                Min,
                Max,
                MyCommonTexts.DialogAmount_SetValueCaption,
                defaultAmount: Convert.ToSingle(dialogValue),
                parseAsInteger: Type == SliderType.Integer,
                backgroundTransition: config?.UIBkOpacity ?? 0f,
                guiTransition: config?.UIOpacity ?? 0f);

            screen.OnConfirmed += (value) => element.Value = value;

            // Much needed visual change requires reflection due to private types
            CanHideOthersField?.SetValue(screen, true);

            MyGuiSandbox.AddScreen(screen);
            return true;
        }

        var slider = new MyGuiControlSlider(
            toolTip: Description,
            defaultValue: Convert.ToSingle(currentValue),
            minValue: Min,
            maxValue: Max,
            intValue: Type == SliderType.Integer)
        {
            MinimumStepOverride = Step,
        };

        if (Type == SliderType.Float)
        {
            slider.LabelDecimalPlaces = (int)Math.Max(1, Math.Ceiling(-Math.Log10(2f * Step)));
        }

        slider.ValueChanged += ValueUpdate;
        slider.SliderSetValueManual = SpecifyValue;

        ValueUpdate(slider);

        var label = new MyGuiControlLabel(text: Tools.Tools.GetLabelOrDefault(name, Label));
        label.SetToolTip(Description);
        valueLabel.SetToolTip(Description);
        return new List<Control>()
        {
            new Control(label, minWidth: Control.LabelMinWidth),
            new Control(slider, fillFactor: 1f, rightMargin: 0.005f),
            new Control(valueLabel, minWidth: 0.06f),
        };
    }

    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(double),
        typeof(float),
        typeof(int),
    };
}
