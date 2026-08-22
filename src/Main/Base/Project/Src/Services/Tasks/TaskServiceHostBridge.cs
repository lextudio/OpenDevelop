using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui
{
	public enum HostTaskType
	{
		Error,
		Warning,
		Message
	}

	public class HostTask
	{
		public HostTask(FileName fileName, string message, int column, int line, HostTaskType taskType)
		{
			FileName = fileName;
			Message = message;
			Column = column;
			Line = line;
			TaskType = taskType;
		}

		public FileName FileName { get; }
		public string Message { get; }
		public int Column { get; }
		public int Line { get; }
		public HostTaskType TaskType { get; }
	}

	public interface ITaskListService
	{
		void Add(HostTask task);
		void ClearExceptCommentTasks();
		bool SomethingWentWrong { get; }
		bool HasCriticalErrors(bool treatWarningsAsErrors);
	}

	public static class HostTaskService
	{
		static ITaskListService Service
			=> ServiceSingleton.ServiceProvider.GetService(typeof(ITaskListService)) as ITaskListService;

		public static void Add(HostTask task)
		{
			Service?.Add(task);
		}

		public static void ClearExceptCommentTasks()
		{
			Service?.ClearExceptCommentTasks();
		}

		public static bool SomethingWentWrong => Service?.SomethingWentWrong ?? false;

		public static bool HasCriticalErrors(bool treatWarningsAsErrors)
			=> Service?.HasCriticalErrors(treatWarningsAsErrors) ?? false;

		public static IOutputCategory BuildMessageViewCategory {
			get {
				IOutputPad outputPad = ServiceSingleton.ServiceProvider.GetService(typeof(IOutputPad)) as IOutputPad;
				return outputPad?.GetOrCreateCategory("Build");
			}
		}
	}
}
