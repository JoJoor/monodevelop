﻿// 
// ProjectBuilder.cs
//  
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
//       Michael Hutchinson <m.j.hutchinson@gmail.com>
//
// Copyright (c) 2009-2011 Novell, Inc (http://www.novell.com)
// Copyright (c) 2011-2015 Xamarin Inc. (http://www.xamarin.com)
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
using System.IO;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Logging;
using Microsoft.Build.Execution;
using System.Xml;

namespace MonoDevelop.Projects.MSBuild
{
	partial class ProjectBuilder
	{
		readonly ProjectCollection engine;
		readonly string file;
		readonly BuildEngine buildEngine;

		public ProjectBuilder (BuildEngine buildEngine, ProjectCollection engine, string file)
		{
			this.file = file;
			this.engine = engine;
			this.buildEngine = buildEngine;
			Refresh ();
		}

		public MSBuildResult Run (
			ProjectConfigurationInfo[] configurations, IEngineLogWriter logWriter, MSBuildVerbosity verbosity,
			string[] runTargets, string[] evaluateItems, string[] evaluateProperties, Dictionary<string,string> globalProperties, int taskId)
		{
			if (runTargets == null || runTargets.Length == 0)
				throw new ArgumentException ("runTargets is empty");

			MSBuildResult result = null;

			BuildEngine.RunSTA (taskId, delegate {
				Project project = null;
				Dictionary<string, string> originalGlobalProperties = null;

				MSBuildLoggerAdapter loggerAdapter;

				if (buildEngine.BuildOperationStarted) {
					buildEngine.SetCurrentLogger (logWriter);
					loggerAdapter = buildEngine.SessionLoggerAdapter;
				}
				else
					loggerAdapter = new MSBuildLoggerAdapter (logWriter, verbosity);

				try {
					project = SetupProject (configurations);

					if (globalProperties != null) {
						originalGlobalProperties = new Dictionary<string, string> ();
						foreach (var p in project.GlobalProperties)
							originalGlobalProperties [p.Key] = p.Value;
						if (globalProperties != null) {
							foreach (var p in globalProperties)
								project.SetGlobalProperty (p.Key, p.Value);
						}
						project.ReevaluateIfNecessary ();
					}

					// Building the project will create items and alter properties, so we use a new instance
					var pi = project.CreateProjectInstance ();

					Build (pi, runTargets, loggerAdapter.Loggers);

					result = new MSBuildResult (loggerAdapter.BuildResult.ToArray ());

					if (evaluateProperties != null) {
						foreach (var name in evaluateProperties) {
							var prop = pi.GetProperty (name);
							result.Properties [name] = prop != null? prop.EvaluatedValue : null;
						}
					}

					if (evaluateItems != null) {
						foreach (var name in evaluateItems) {
							var grp = pi.GetItems (name);
							var list = new List<MSBuildEvaluatedItem> ();
							foreach (var item in grp) {
								var evItem = new MSBuildEvaluatedItem (name, UnescapeString (item.EvaluatedInclude));
								foreach (var m in item.Metadata) {
									evItem.Metadata [m.Name] = UnescapeString (m.EvaluatedValue);
								}
								list.Add (evItem);
							}
							result.Items[name] = list.ToArray ();
						}
					}
				} catch (Microsoft.Build.Exceptions.InvalidProjectFileException ex) {
					var r = new MSBuildTargetResult (
						file, false, ex.ErrorSubcategory, ex.ErrorCode, ex.ProjectFile,
						ex.LineNumber, ex.ColumnNumber, ex.EndLineNumber, ex.EndColumnNumber,
						ex.BaseMessage, ex.HelpKeyword);
					loggerAdapter.LogWriteLine (r.ToString ());
					result = new MSBuildResult (new [] { r });
				} finally {
					if (buildEngine.BuildOperationStarted)
						buildEngine.SetCurrentLogger (null);
					else
						loggerAdapter.Dispose ();
					
					if (project != null && globalProperties != null) {
						foreach (var p in globalProperties)
							project.RemoveGlobalProperty (p.Key);
						foreach (var p in originalGlobalProperties)
							project.SetGlobalProperty (p.Key, p.Value);
						project.ReevaluateIfNecessary ();
					}
				}
			});
			return result;
		}
		
		Project SetupProject (ProjectConfigurationInfo[] configurations)
		{
			Project project = null;

			var slnConfigContents = GenerateSolutionConfigurationContents (configurations);

			foreach (var pc in configurations) {
				var p = ConfigureProject (pc.ProjectFile, pc.Configuration, pc.Platform, slnConfigContents);
				if (pc.ProjectFile == file)
					project = p;
			}

			var projectDir = Path.GetDirectoryName (file);
			if (!string.IsNullOrEmpty (projectDir) && Directory.Exists (projectDir))
				Environment.CurrentDirectory = projectDir;
			return project;
		}

		Project ConfigureProject (string file, string configuration, string platform, string slnConfigContents)
		{			
			var p = engine.GetLoadedProjects (file).FirstOrDefault ();
			if (p == null) {
				var projectDir = Path.GetDirectoryName (file);

				// HACK: workaround to MSBuild bug #53019. We need to ensure that $(BaseIntermediateOutputPath) exists before
				// loading the project.
				if (!string.IsNullOrEmpty (projectDir))
					Directory.CreateDirectory (Path.Combine (projectDir, "obj"));

				var content = buildEngine.GetUnsavedProjectContent (file);
				if (content == null)
					p = engine.LoadProject (file);
				else {
					if (!string.IsNullOrEmpty (projectDir) && Directory.Exists (projectDir))
						Environment.CurrentDirectory = projectDir;
					var projectRootElement = ProjectRootElement.Create (new XmlTextReader (new StringReader (content)));
					projectRootElement.FullPath = file;

					// Use the engine's default tools version to load the project. We want to build with the latest
					// tools version.
					string toolsVersion = engine.DefaultToolsVersion;
					p = new Project (projectRootElement, engine.GlobalProperties, toolsVersion, engine);
				}
			}

			if (p.GetPropertyValue("Configuration") != configuration || (p.GetPropertyValue("Platform") ?? "") != (platform ?? ""))
			{
				p.SetGlobalProperty("CurrentSolutionConfigurationContents", slnConfigContents);
				p.SetGlobalProperty("Configuration", configuration);
				if (!string.IsNullOrEmpty(platform))
					p.SetGlobalProperty("Platform", platform);
				else
					p.RemoveGlobalProperty("Platform");

				p.ReevaluateIfNecessary();
			}

			return p;
		}


		/// <summary>
		/// Builds a list of targets with the specified loggers.
		/// </summary>
		internal void Build (ProjectInstance pi, string [] targets, IEnumerable<ILogger> loggers)
		{
			BuildResult results;

			if (!buildEngine.BuildOperationStarted) {
				BuildParameters parameters = new BuildParameters (engine);
				parameters.ResetCaches = false;
				parameters.EnableNodeReuse = true;
				BuildRequestData data = new BuildRequestData (pi, targets, parameters.HostServices);
				parameters.Loggers = loggers;
				results = BuildManager.DefaultBuildManager.Build (parameters, data);
			} else {
				BuildRequestData data = new BuildRequestData (pi, targets);
				results = BuildManager.DefaultBuildManager.BuildRequest (data);
			}
		}
	}
}
