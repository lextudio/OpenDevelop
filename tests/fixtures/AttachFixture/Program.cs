namespace AttachFixture;

/// <summary>A long-running managed program so the debugger can attach to a process it did not
/// start. Attaching stops at the marked line once a breakpoint is set.</summary>
public static class Program
{
	public static void Main()
	{
		while (true)
		{
			Work();
			Thread.Sleep(200);
		}
	}

	static int Work()
	{
		var attachMarker = 7; // attach-breakpoint-line
		return attachMarker;
	}
}
