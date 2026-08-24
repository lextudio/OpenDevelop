namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>Runtime-neutral command gateway for a designer document.</summary>
public sealed class DesignerCommandController
{
	readonly Dictionary<string, CommandRegistration> commands = new(StringComparer.Ordinal);
	bool executing;

	public event EventHandler? StateChanged;

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

	sealed record CommandRegistration(Func<bool> CanExecute, Func<bool> Execute);
}
