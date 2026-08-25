namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>Runtime-neutral command gateway for a designer document.</summary>
public sealed class DesignerCommandController
{
	readonly Dictionary<string, CommandRegistration> commands = new(StringComparer.Ordinal);
	bool executing;

	public event EventHandler? StateChanged;
	public IReadOnlyList<string> RegisteredCommands => commands.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
	public bool IsExecuting => executing;

	public void RegisterStandard(Func<bool> canUndo, Func<bool> undo, Func<bool> canRedo, Func<bool> redo,
		Func<bool>? canDelete = null, Func<bool>? delete = null)
	{
		Register(DesignerCommandNames.Undo, canUndo, undo);
		Register(DesignerCommandNames.Redo, canRedo, redo);
		if (canDelete != null && delete != null)
			Register(DesignerCommandNames.Delete, canDelete, delete);
	}

	public void Register(string name, Func<bool> canExecute, Func<bool> execute)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(canExecute);
		ArgumentNullException.ThrowIfNull(execute);
		commands[name] = new CommandRegistration(canExecute, execute);
		StateChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool CanExecute(string name) => !executing && commands.TryGetValue(name, out var command) && command.CanExecute();

	public bool Execute(string name)
	{
		if (!CanExecute(name)) return false;
		executing = true;
		try {
			return commands[name].Execute();
		} finally {
			executing = false;
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	public void Invalidate() => StateChanged?.Invoke(this, EventArgs.Empty);
	public DesignerCommandState State(string name) => new(name, commands.ContainsKey(name), CanExecute(name), executing);
	public IReadOnlyList<DesignerCommandState> Snapshot() => RegisteredCommands.Select(State).ToArray();

	sealed record CommandRegistration(Func<bool> CanExecute, Func<bool> Execute);
}

public static class DesignerCommandNames
{
	public const string Undo = "Undo";
	public const string Redo = "Redo";
	public const string Delete = "Delete";
	public const string Align = "Align";
	public const string Distribute = "Distribute";
	public const string MatchSize = "MatchSize";
	public const string Nudge = "Nudge";
}

public sealed record DesignerCommandState(string Name, bool Registered, bool CanExecute, bool IsExecuting);
