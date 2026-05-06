using Sandbox;
using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin;

internal sealed class SavedAlignmentRemovalScreen : MyGuiScreenBase
{
    private const int MaxVisibleRows = 8;

    private readonly List<Entry> entries = new List<Entry>();
    private readonly Action<SavedConnectorAlignment> removeAlignment;
    private readonly string currentGridName;
    private readonly List<MyGuiControlLabel> rowLabels = new List<MyGuiControlLabel>();

    private MyGuiControlLabel summaryLabel;
    private MyGuiControlLabel statusLabel;
    private int selectedIndex;
    private int? pendingRemovalIndex;

    public SavedAlignmentRemovalScreen(IEnumerable<SavedConnectorAlignment> savedAlignments, string currentGridName, Action<SavedConnectorAlignment> removeAlignment)
        : base(
            new Vector2(0.5f, 0.5f),
            MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            new Vector2(0.52f, 0.42f),
            false,
            null,
            MySandboxGame.Config.UIBkOpacity,
            MySandboxGame.Config.UIOpacity)
    {
        this.removeAlignment = removeAlignment ?? throw new ArgumentNullException(nameof(removeAlignment));
        this.currentGridName = currentGridName;

        foreach (SavedConnectorAlignment savedAlignment in savedAlignments)
        {
            if (savedAlignment == null)
                continue;

            entries.Add(new Entry(savedAlignment, savedAlignment.GetDisplayName(currentGridName)));
        }

        entries.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));

        EnabledBackgroundFade = true;
        m_closeOnEsc = true;
        m_drawEvenWithoutFocus = true;
        CanHideOthers = true;
        CanBeHidden = true;
        CloseButtonEnabled = false;
    }

    public override string GetFriendlyName() => "AutoDockSavedAlignmentRemovalScreen";

    public override void LoadContent()
    {
        base.LoadContent();
        RecreateControls(true);
    }

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        Controls.Clear();
        rowLabels.Clear();
        AddCaption("REMOVE SAVED ALIGNMENT");

        summaryLabel = new MyGuiControlLabel(text: "")
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(-0.235f, -0.145f),
        };
        Controls.Add(summaryLabel);

        for (int i = 0; i < MaxVisibleRows; i++)
        {
            var rowLabel = new MyGuiControlLabel(text: "")
            {
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                Position = new Vector2(-0.235f, -0.095f + i * 0.032f),
            };
            rowLabels.Add(rowLabel);
            Controls.Add(rowLabel);
        }

        statusLabel = new MyGuiControlLabel(text: "")
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(-0.235f, 0.17f),
        };
        Controls.Add(statusLabel);

        RefreshControls();
    }

    public void HandleExternalInput(IMyInput input)
    {
        if (input == null || entries.Count == 0)
            return;

        if (input.IsNewKeyPressed(MyKeys.Up))
        {
            SelectRelative(-1);
            return;
        }

        if (input.IsNewKeyPressed(MyKeys.Down))
        {
            SelectRelative(1);
            return;
        }

        if (input.IsNewKeyPressed(MyKeys.Enter))
            ActivateSelection();
    }

    private void SelectRelative(int offset)
    {
        if (entries.Count == 0)
            return;

        selectedIndex = (selectedIndex + offset) % entries.Count;
        if (selectedIndex < 0)
            selectedIndex += entries.Count;

        pendingRemovalIndex = null;
        RefreshControls();
    }

    private void ActivateSelection()
    {
        if (entries.Count == 0)
            return;

        if (pendingRemovalIndex != selectedIndex)
        {
            pendingRemovalIndex = selectedIndex;
            RefreshControls();
            return;
        }

        Entry entry = entries[selectedIndex];
        removeAlignment(entry.Alignment);
        entries.RemoveAt(selectedIndex);
        if (selectedIndex >= entries.Count)
            selectedIndex = Math.Max(0, entries.Count - 1);

        pendingRemovalIndex = null;
        RefreshControls();
    }

    private void RefreshControls()
    {
        summaryLabel.Text = entries.Count == 0
            ? "No saved alignments."
            : $"Saved alignments: {entries.Count}";

        int firstVisibleIndex = GetFirstVisibleIndex();
        for (int row = 0; row < rowLabels.Count; row++)
        {
            int entryIndex = firstVisibleIndex + row;
            MyGuiControlLabel rowLabel = rowLabels[row];
            if (entryIndex >= entries.Count)
            {
                rowLabel.Text = string.Empty;
                continue;
            }

            Entry entry = entries[entryIndex];
            bool selected = entryIndex == selectedIndex;
            bool armed = pendingRemovalIndex == entryIndex;
            string prefix = armed ? "[CONFIRM] " : selected ? "> " : "  ";
            rowLabel.Text = prefix + entry.DisplayName;
            rowLabel.ColorMask = armed ? Color.Red : selected ? Color.LightBlue : Color.White;
        }

        if (entries.Count == 0)
        {
            statusLabel.Text = "Esc: close";
            return;
        }

        Entry selectedEntry = entries[selectedIndex];
        statusLabel.Text = pendingRemovalIndex == selectedIndex
            ? $"Press Enter again to remove {selectedEntry.DisplayName}. Esc cancels."
            : "Up/Down: select. Enter: mark remove. Enter again: confirm. Esc: close.";
    }

    private int GetFirstVisibleIndex()
    {
        if (entries.Count <= MaxVisibleRows)
            return 0;

        int maxFirst = entries.Count - MaxVisibleRows;
        int firstVisibleIndex = selectedIndex - MaxVisibleRows / 2;
        if (firstVisibleIndex < 0)
            firstVisibleIndex = 0;
        if (firstVisibleIndex > maxFirst)
            firstVisibleIndex = maxFirst;

        return firstVisibleIndex;
    }

    private sealed class Entry
    {
        public readonly SavedConnectorAlignment Alignment;
        public readonly string DisplayName;

        public Entry(SavedConnectorAlignment alignment, string displayName)
        {
            Alignment = alignment;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown grid" : displayName;
        }
    }
}
