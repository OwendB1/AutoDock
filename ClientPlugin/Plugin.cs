using System;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using System.Reflection; 
using VRage.Plugins;
using VRage.Utils;

// Set the assembly version manually if compiled by Pulsar (it won't create what was in AssemblyInfo.cs before)
#if !DEV_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif
    
namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "AutoDock";
    public static Plugin Instance { get; private set; }
    private Harmony harmony;
    private SettingsGenerator settingsGenerator;
    private AutoDockController autoDockController;
    private bool updateFailureLogged;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        MyLog.Default.WriteLineAndConsole($"{Name}: Init started.");

        try
        {
            Instance = this;

            MyLog.Default.WriteLineAndConsole($"{Name}: Creating settings generator.");
            Instance.settingsGenerator = new SettingsGenerator();

            MyLog.Default.WriteLineAndConsole($"{Name}: Creating AutoDock controller.");
            Instance.autoDockController = new AutoDockController();

            MyLog.Default.WriteLineAndConsole($"{Name}: Applying Harmony patches.");
            Instance.harmony = new Harmony("AutoDock");
            Instance.harmony.PatchAll();

            MyLog.Default.WriteLineAndConsole($"{Name}: Init completed.");
        }
        catch (Exception exception)
        {
            MyLog.Default.WriteLineAndConsole($"{Name}: Init failed: {exception}");
            throw;
        }
    }

    public void Dispose()
    {
        MyLog.Default.WriteLineAndConsole($"{Name}: Dispose.");
        AutoDockControlOverride.Clear();
        harmony?.UnpatchAll("AutoDock");
        autoDockController?.Dispose();

        harmony = null;
        autoDockController = null;
        Instance = null;
    }

    public void Update()
    {
        try
        {
            autoDockController?.Update();
        }
        catch (Exception exception)
        {
            if (!updateFailureLogged)
            {
                updateFailureLogged = true;
                MyLog.Default.WriteLineAndConsole($"{Name}: Update failed: {exception}");
            }
        }
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        MyLog.Default.WriteLineAndConsole($"{Name}: OpenConfigDialog.");

        SettingsGenerator generator = Instance?.settingsGenerator ?? settingsGenerator;
        if (generator == null)
        {
            MyLog.Default.WriteLineAndConsole($"{Name}: Creating settings generator on demand.");
            generator = new SettingsGenerator();
            if (Instance != null)
                Instance.settingsGenerator = generator;
            else
                settingsGenerator = generator;
        }

        generator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(generator.Dialog);
    }

    //TODO: Uncomment and use this method to load asset files
    /*public void LoadAssets(string folder)
    {

    }*/
}
