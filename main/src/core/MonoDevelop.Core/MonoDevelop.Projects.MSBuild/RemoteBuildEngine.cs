﻿// 
// RemoteProjectBuilder.cs
//  
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
// 
// Copyright (c) 2009 Novell, Inc (http://www.novell.com)
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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MonoDevelop.Core;
using System.IO;
using MonoDevelop.Core.Execution;
using System.Linq;

namespace MonoDevelop.Projects.MSBuild
{
	/// <summary>
	/// A frontend for a build engine running in an external process
	/// </summary>
	class RemoteBuildEngine
	{
		RemoteProcessConnection connection;
		bool alive = true;
		static int count;
		int busy;
		int buildSessionLoggerId;

		static int loggerIdCounter;
		Dictionary<int, LoggerInfo> loggers = new Dictionary<int, LoggerInfo> ();

		public int ReferenceCount { get; set; }
		public DateTime ReleaseTime { get; set; }

		Dictionary<FilePath,Task<RemoteProjectBuilder>> remoteProjectBuilders = new Dictionary<FilePath, Task<RemoteProjectBuilder>> ();

		public RemoteBuildEngine (RemoteProcessConnection connection)
		{
			this.connection = connection;
			Interlocked.Increment (ref count);
			connection.AddListener (this);
		}

		/// <summary>
		/// Occurs when the remote process shuts down
		/// </summary>
		public event EventHandler Disconnected;

		/// <summary>
		/// Handle of the currently active build session, or null if there is no build session.
		/// </summary>
		/// <value>The build session identifier.</value>
		public object BuildSessionId { get; set; }

		/// <summary>
		/// Returns true if the provided project is currently loaded in the build engine
		/// </summary>
		public bool IsProjectLoaded (string projectFile)
		{
			lock (remoteProjectBuilders)
				return remoteProjectBuilders.ContainsKey (projectFile);
		}

		/// <summary>
		/// Gets or creates a project builder for the provided project
		/// </summary>
		/// <returns>The remote project builder.</returns>
		/// <param name="projectFile">Project to build.</param>
		public Task<RemoteProjectBuilder> GetRemoteProjectBuilder (string projectFile)
		{
			lock (remoteProjectBuilders) {
				if (remoteProjectBuilders.TryGetValue (projectFile, out var builder))
					return builder;

				builder = CreateRemoteProjectBuilder (projectFile);
				remoteProjectBuilders.Add (projectFile, builder);
				return builder;
			}
		}

		async Task<RemoteProjectBuilder> CreateRemoteProjectBuilder (string projectFile)
		{
			var builder = await LoadProject (projectFile).ConfigureAwait (false);
			var pb = new RemoteProjectBuilder (projectFile, builder, this);

			// Unlikely, but it may happen
			if (IsShuttingDown)
				pb.Shutdown ();
			
			return pb;
		}

		async Task<ProjectBuilder> LoadProject (string projectFile)
		{
			try {
				var pid = (await connection.SendMessage (new LoadProjectRequest { ProjectFile = projectFile }).ConfigureAwait (false)).ProjectId;
				return new ProjectBuilder (connection, pid);
			} catch {
				await CheckDisconnected ();
				throw;
			}
		}

		internal async Task UnloadProject (RemoteProjectBuilder remoteBuilder, ProjectBuilder builder)
		{
			lock (remoteProjectBuilders)
				remoteProjectBuilders.Remove (remoteBuilder.File);

			try {
				await connection.SendMessage (new UnloadProjectRequest { ProjectId = ((ProjectBuilder)builder).ProjectId }).ConfigureAwait (false);
			} catch (Exception ex) {
				LoggingService.LogError ("Project unloading failed", ex);
				if (!await CheckDisconnected ())
					throw;
			}
		}

		/// <summary>
		/// Marks this instance as being shutdown, so it should not be used to create new project builders.
		/// </summary>
		public void Shutdown ()
		{
			lock (remoteProjectBuilders) {
				if (IsShuttingDown)
					return;
				IsShuttingDown = true;
				foreach (var pb in remoteProjectBuilders.Values)
					pb.ContinueWith (t => t.Result.Shutdown (), TaskContinuationOptions.NotOnFaulted);
			}
		}

		/// <summary>
		/// Gets a value indicating whether this engine is shutting down, so it should not be
		/// used to get new project builders
		/// </summary>
		public bool IsShuttingDown { get; private set; }

		/// <summary>
		/// Cancels a task currently in progress
		/// </summary>
		/// <param name="taskId">Task identifier.</param>
		public async Task CancelTask (int taskId)
		{
			try {
				await connection.SendMessage (new CancelTaskRequest { TaskId = taskId });
			} catch {
				await CheckDisconnected ();
				throw;
			}
		}

		/// <summary>
		/// Sets global msbuild properties
		/// </summary>
		public async Task SetGlobalProperties (Dictionary<string, string> properties)
		{
			try {
				await connection.SendMessage (new SetGlobalPropertiesRequest { Properties = properties });
			} catch {
				await CheckDisconnected ();
				throw;
			}
		}

		/// <summary>
		/// Indicates that a build session is starting
		/// </summary>
		public async Task BeginBuildOperation (TextWriter logWriter, MSBuildVerbosity verbosity)
		{
			buildSessionLoggerId = RegisterLogger (logWriter, null);
			try {
				await connection.SendMessage (new BeginBuildRequest { LogWriterId = buildSessionLoggerId, Verbosity = verbosity });
			} catch {
				UnregisterLogger (buildSessionLoggerId);
				await CheckDisconnected ();
				throw;
			}
		}

		/// <summary>
		/// Indicates that a build session has finished.
		/// </summary>
		/// <returns>The build operation.</returns>
		public async Task EndBuildOperation ()
		{
			try {
				await connection.SendMessage (new EndBuildRequest ());
				await connection.ProcessPendingMessages ();
			} catch {
				await CheckDisconnected ();
				throw;
			} finally {
				UnregisterLogger (buildSessionLoggerId);
			}
		}

		/// <summary>
		/// Pings the remote process. Will cause a desconnection if the ping
		/// message can't be delivered.
		/// </summary>
		/// <returns>The ping.</returns>
		public async Task Ping ()
		{
			await connection.SendMessage (new PingRequest ());
		}

		async Task<bool> CheckAlive ()
		{
			if (!alive)
				return false;
			try {
				await Ping ();
				return true;
			} catch {
				alive = false;
				return false;
			}
		}

		internal async Task<bool> CheckDisconnected ()
		{
			if (!await CheckAlive ()) {
				if (Disconnected != null)
					Disconnected (this, EventArgs.Empty);
				return true;
			}
			return false;
		}

		public void Dispose ()
		{
			Interlocked.Decrement (ref count);
			try {
				alive = false;
				connection.Disconnect ().Ignore ();
			} catch {
				// Ignore
			}
		}

		[MessageHandler]
		void OnLogMessage (LogMessage msg)
		{
			LoggerInfo logger = null;
			lock (loggers) {
				if (!loggers.TryGetValue (msg.LoggerId, out logger))
					return;
			}
			if (msg.LogText != null)
				logger.Writer.Write (msg.LogText);
			if (msg.Events != null && logger.Logger != null) {
				foreach (var e in msg.Events)
					logger.Logger.NotifyEvent (e);
			}
		}

		class LoggerInfo
		{
			public TextWriter Writer;
			public MSBuildLogger Logger;
		}

		/// <summary>
		/// Registers a logger to be used by the remote engine
		/// </summary>
		/// <returns>The logger id.</returns>
		/// <remarks>Project builders register a logger and provide the logger id
		/// to the remote project builders. The remote process sends log messages
		/// that include the logger id, and this class redirects the log to
		/// one of the registered loggers.</remarks>
		public int RegisterLogger (TextWriter writer, MSBuildLogger logger)
		{
			lock (loggers) {
				var i = loggerIdCounter++;
				loggers [i] = new LoggerInfo { Writer = writer, Logger = logger };
				return i;
			}
		}

		/// <summary>
		/// Unregisters a registered logger
		/// </summary>
		/// <param name="id">Identifier.</param>
		public void UnregisterLogger (int id)
		{
			lock (loggers) {
				loggers.Remove (id);
			}
		}

		/// <summary>
		/// Indicates that this engine is currently busy building a project
		/// </summary>
		/// <returns><c>true</c>, if busy was set, <c>false</c> otherwise.</returns>
		public bool SetBusy ()
		{
			return Interlocked.Increment (ref busy) == 1;
		}

		/// <summary>
		/// Indicates that the builder is not busy anymore
		/// </summary>
		public void ResetBusy ()
		{
			Interlocked.Decrement (ref busy);
		}

		/// <summary>
		/// Gets a value indicating whether this build engine is busy building a project.
		/// </summary>
		/// <value><c>true</c> if is busy; otherwise, <c>false</c>.</value>
		public bool IsBusy {
			get {
				return busy > 0;
			}
		}
	}
}
