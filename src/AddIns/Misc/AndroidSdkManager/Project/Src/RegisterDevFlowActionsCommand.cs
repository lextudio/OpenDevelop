// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

using ICSharpCode.Core;

namespace ICSharpCode.AndroidSdkManager
{
	/// <summary>
	/// Forces AndroidSdkManager.dll to load at startup so DevFlow can discover od.androidsdk.*
	/// actions without depending on the Tools menu item being clicked first.
	/// </summary>
	public sealed class RegisterDevFlowActionsCommand : AbstractCommand
	{
		public override void Run()
		{
		}
	}
}
