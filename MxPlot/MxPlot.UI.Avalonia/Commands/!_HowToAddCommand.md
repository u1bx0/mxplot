# Adding a command to the Processing / Conversion menu

A *command* is a one-shot menu item: ask the user (a dialog, or nothing), do something with the data,
put the result somewhere. This folder holds them all. `Xxx` below stands for your command's name.

Not commands, and not covered here:

- **Tools** that keep the window in a state of their own while the user works (ROIs, panels): implement
  `IPlotterTool` in `Tools/`.
- **Operations on the window itself** (Save, Export, Copy, Duplicate, About) and the **Info tab**: they
  stay in `MatrixPlotter`.

## How a menu item gets to your code

```
hamburger menu (MatrixPlotter.Menu.cs)
  CommandItems("Group") builds one item per CommandCatalog entry of that group:
      ActAsync(() => c.Command.RunAsync(this))      // `this` is the MatrixPlotter, seen as ICommandHost
                                   |
                                   v
  IProcessingCommand.RunAsync(ICommandHost host)
                                   |
        +--------------------------+---------------------------------+
        | derives from ProcessingCommand                             | implements IProcessingCommand
        | (runs one IMatrixDataOperation)                            | directly (anything else)
        v                                                            v
  ProcessingCommand.RunAsync                                   your RunAsync: ask, work, and show the
    1  host.HideMenu()                                         result through `host` as you like
    2  AskAsync -> your dialog -> CommandPlan  (null = cancelled)
    3  pick the operand: the whole data | the active frame (SliceAt) | the Composite channel cube
    4  plan.Prepare, if any (e.g. a scan of the whole stack)
    5  progress and Cancel button (ProgressSession)
    6  plan.CreateOperation(run) is applied to the operand on a background thread
    7  plan.PrepareResult, plan.OnResultReady, and a history entry
    8  put the result: replace the data | a window that follows the source (Sync)
                       | a linked window | a new window
```

A command sees its window **only through `ICommandHost`** (`ICommandHost.cs`). It cannot touch the
window's controls or fields. If a command needs something the interface lacks, add a narrow,
high-level member ("show this result in a new window"), implemented explicitly in
`Views/MatrixPlotter.CommandHost.cs`. Do not expose window internals.

## Example A: a command that runs an Operation

The algorithm already exists in `MxPlot.Core` as an operation. An operation processes **every frame of
the data it is given** (see `MxPlot.Core/IOperation.cs`, "Frames"). For "This frame only" the runner
slices the frame out, or passes the Composite channel cube, so a command never passes a frame index.

```csharp
// MxPlot.Core - given; the algorithm lives in an operator (XxxOperator.Xxx)
public record XxxOperation(double Level, IProgress<int>? Progress = null,
    CancellationToken CancellationToken = default) : IMatrixDataOperation { ... }
```

**1. The dialog** (`Views/XxxDialog.cs`). Derive from `ProcessingDialogBase`. The parameter record holds
only what the operation needs; it knows nothing about frames, Sync or Replace. Which of the three
checkboxes appear is decided by the base constructor arguments; the dialog returns their values
together with the parameters.

```csharp
internal sealed class XxxDialog : ProcessingDialogBase
{
    internal sealed record XxxParameters(double Level);

    internal static Task<DialogAnswer<XxxParameters>?> ShowAsync(
        Window owner, bool isMultiFrame, bool isLinkWindow, IMatrixData? src)
        => new XxxDialog(isMultiFrame, isLinkWindow, src).ShowDialog<DialogAnswer<XxxParameters>?>(owner);

    private XxxDialog(bool isMultiFrame, bool isLinkWindow, IMatrixData? src)
        : base("Xxx", width: 260, isLinkWindow: isLinkWindow, src: src,
               thisFrameOnlyDefault: isMultiFrame ? false : (bool?)null, // null: no "This frame only"
               showSyncSource: true)                                     // showReplaceData: false hides Replace
    {
        var levelNud = ControlFactory.MakeNumericUpDown(1m, 0m, 1000m, 1m, width: 80);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(ControlFactory.MakeNudRow("Level:", levelNud, labelWidth: 60));
        var frameOptions = BuildFrameOptions();          // places "This frame only" / "Sync source data"
        if (frameOptions != null) content.Children.Add(frameOptions);

        FinalizeContent(content, onOk: () =>
            Close(Answer(new XxxParameters((double)(levelNud.Value ?? 1m)))), okLabel: "Apply");
    }
}
```

`Answer(...)` adds the checkboxes' values (`RunChoices`) to the parameters. A dialog with no parameters
closes with `Close(ReadChoices())` instead (see `TransposeDialog`). Do not build the checkboxes yourself.

**2. The command** (`Commands/XxxCommand.cs`). `AskAsync` shows the dialog and turns its answer into a
`CommandPlan`: how to build the operation and how to word the result. Return `null` when the user
cancels, or when the command does not apply. A command without a dialog returns its plan at once.

```csharp
internal class XxxCommand : ProcessingCommand
{
    protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
    {
        var answer = await XxxDialog.ShowAsync(host.Owner, isMultiFrame, host.IsReplaceBlocked, data);
        return answer == null ? null : CreatePlan(answer.Choices, answer.Params);
    }

    // internal static, so that a test can build the plan without the dialog
    internal static CommandPlan CreatePlan(RunChoices choices, XxxDialog.XxxParameters p) => new()
    {
        // required
        Choices = choices,
        FailureTitle = "Xxx Failed",
        CreateOperation = run => new XxxOperation(p.Level, run.Progress, run.Token),
        HistoryLabel = "Xxx",
        HistoryDetail = ctx => $"level={p.Level}",
        ResultTitle = source => $"Xxx of {source}",

        // only what differs from the defaults
        Progress = ProgressMode.WholeStackOnly,
        ProgressLabel = "Applying Xxx…",
    };
}
```

The optional members of `CommandPlan` (defaults in parentheses):

| Member | What it does |
|---|---|
| `WholeData` (`false`) | The operation always gets the whole data: no "This frame only", Composite cube or Sync. For operations that are about the stack itself (Reverse Stack, Extract Dimension, Grayscale). |
| `Progress` (`Indeterminate`) | `None`, `Indeterminate` (a bar, no Cancel), `WholeStackOnly` (progress and Cancel only when more than one frame is processed), `Always`. Needs `ProgressLabel`. |
| `KeepsCompositeMode` (`true`) | Whether a result made from a Composite channel cube stays in Composite mode. |
| `CopiesDisplayState` (`false`) | Whether a result window starts with the source's range and LUT settings. |
| `ResultIsLinked` (`false`) | The new window is refreshed together with the source (an extract), not recomputed. |
| `OutOfMemoryMessage` | Wording for an `OutOfMemoryException`. |
| `Prepare` | Work before the operation, such as a scan of the whole stack; its result reaches `CreateOperation` as `run.State`. May throw `OperationCanceledException` to end the run silently. |
| `PrepareResult` | Finishes the result before it is shown; also runs for every refresh of a Sync window. The default copies the source's properties (not with `WholeData`). |
| `OnResultReady` | For what only the first result needs (not run again by a Sync refresh). |

Real commands to copy from: `SpatialFilterCommand` (the plain case), `FftCommand` (`PrepareResult`),
`NormalizeCommand` (`Prepare`), `TransposeCommand` (`OnResultReady`), `ReverseStackCommand`
(`WholeData`).

**3. Register it** in `CommandCatalog.cs`. The menu builds the item from this entry.

```csharp
new("Filters", "Xxx…", "Tooltip text", MenuIcons.AutoFix, new XxxCommand()),
// an optional last argument decides when the item is offered:
//     host => host.Data?.FrameCount > 1
```

The groups `Filters`, `Intensity`, `Frequency`, `Geometry & Dimensions` (Processing tab) and
`Conversion` (Data tab) already exist. A new group needs one line in `MatrixPlotter.Menu.cs`, next
to the existing ones:

```csharp
processingTabBody.Children.Add(
    ControlFactory.MakeMenuGroup("Xxx", CommandItems("Xxx"), icon: MenuIcons.Processing));
```

**4. Test it** (`Tests.MxPlot.UI.Avalonia.Headless`) with `FakeCommandHost`, a window that records what
the command asks of it. Answer in place of the dialog by overriding `AskAsync`:

```csharp
private sealed class AskedXxx(XxxDialog.XxxParameters answer, RunChoices? choices = null) : XxxCommand
{
    protected override Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        => Task.FromResult<CommandPlan?>(CreatePlan(choices ?? RunChoices.None, answer));
}

[Fact]
public async Task Xxx_ShowsTheResultInANewWindow()
{
    var host = new FakeCommandHost { Data = data };

    await new AskedXxx(new(1.0)).RunAsync(host);

    Assert.Equal("show:Xxx of Src:", host.Calls.Last());   // host.Calls records what the command did
    var shown = host.Shown!;                                // the result
}
```

See `ProcessingCommandsTests` (each command) and `ProcessingCommandRunnerTests` (the shared steps).

## Example B: a command that does not run an Operation

Anything one-shot that needs only what `ICommandHost` offers implements `IProcessingCommand` directly.
There is no plan and no shared runner: the command asks, works and shows the result itself.

```csharp
internal sealed class ShowRangeCommand : IProcessingCommand
{
    public async Task RunAsync(ICommandHost host)
    {
        var data = host.Data;
        if (data == null) return;
        host.HideMenu();                                    // close the menu panel first
        var (min, max) = data.GetValueRange(data.ActiveIndex);
        await host.ShowMessageAsync("Value Range", $"Min = {min}\nMax = {max}");
    }
}
```

It is registered in `CommandCatalog` exactly like Example A (`new("Intensity", "Show Range…", "...", icon,
new ShowRangeCommand())`), and tested the same way, with `FakeCommandHost`.

What a command can do through `host` (`ICommandHost.cs`):

| Need | Member |
|---|---|
| The data, title, dialog owner | `Data`, `Title`, `Owner` |
| Close the menu panel | `HideMenu()` |
| Progress with an optional Cancel button | `BeginProgress(label, cancellable)`; dispose the returned `ProgressSession` to end it |
| A message | `ShowMessageAsync(title, message)` |
| Show a result | `ReplaceData`, `ShowResult`, `ShowLinkedResult`, `ShowSyncedResult` |
| Composite mode | `IsCompositeMode`, `TryExtractCompositeCube`, `DescribeCompositeCube` |
| Other | `IsReplaceBlocked`, `IsThisFrameOnlyAChoice`, `DisplayedRange`, `OverlaysJson` |

`ConvertValueTypeCommand` is a real example of this kind that asks with a dialog and shows a progress
bar and a result. It cannot be an Operation command because the conversion is not an operation in Core.

## Rules

- An operation processes every frame of what it is given. Do not add a frame index or a
  single-frame branch to an operator; slice first (`data.SliceAt(index)`). The result of a slice has
  no metadata, which is why `PrepareResult` copies the source's properties.
- Do not keep a `CancellationTokenSource` in a field. Progress and cancellation come from
  `ProgressSession` (`host.BeginProgress`; the runner does it for Operation commands).
- The catalog holds one shared instance per command, so a command keeps no per-run state in its fields.
  Whatever a run needs lives in the plan's closures.
- Comments in the source are in English and describe the code as it is now.
