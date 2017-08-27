//
// MSBuildSolutionExtension.cs
//
// Author:
//       Lluis Sanchez <llsan@microsoft.com>
//
// Copyright (c) 2017 Microsoft
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MonoDevelop.Projects.MSBuild
{
	class MSBuildSolutionExtension: SolutionExtension
	{
		public static readonly object MSBuildProjectOperationId = typeof (MSBuildSolutionExtension);
		static int operations;

		internal protected override Task OnBeginBuildOperation (ConfigurationSelector configuration, OperationContext operationContext)
		{
			operationContext.SessionData [MSBuildProjectOperationId] = Interlocked.Increment (ref operations);
			return base.OnBeginBuildOperation (configuration, operationContext);
		}

		internal protected override async Task OnEndBuildOperation (ConfigurationSelector configuration, OperationContext operationContext, BuildResult result)
		{
			await MSBuildProjectService.EndBuildSessions ((int)operationContext.SessionData [MSBuildProjectOperationId]);
			await base.OnEndBuildOperation (configuration, operationContext, result);
		}
	}
}
