// 
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
				try {
					Console.WriteLine("SETTING UP PROJECT " + Path.GetFileName(file));
					var s = System.Diagnostics.Stopwatch.StartNew();
					project = SetupProject (configurations);
					Console.WriteLine("DONE " + s.ElapsedMilliseconds);
					InitLogger (logWriter);

					ILogger[] loggers;
					var logger = new LocalLogger (file);
					if (logWriter != null) {
						var consoleLogger = new ConsoleLogger (GetVerbosity (verbosity), LogWrite, null, null);
						var eventLogger = new TargetLogger (logWriter.RequiredEvents, LogEvent);
						loggers = new ILogger[] { logger, consoleLogger, eventLogger };
					} else {
						loggers = new ILogger[] { logger };
					}

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

					//building the project will create items and alter properties, so we use a new instance
					var pi = project.CreateProjectInstance ();

					Console.WriteLine("BUILDING " + Path.GetFileName(file));
					s = System.Diagnostics.Stopwatch.StartNew();
//					pi.Build (runTargets, loggers);
					Build(pi,runTargets, loggers);
					Console.WriteLine("DONE " + s.ElapsedMilliseconds);

					result = new MSBuildResult (logger.BuildResult.ToArray ());

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
					LogWriteLine (r.ToString ());
					result = new MSBuildResult (new [] { r });
				} finally {
					DisposeLogger ();
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
				Console.WriteLine("LOADING " + Path.GetFileName(file));
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
				Console.WriteLine("NOT REUSING CONF FOR " + Path.GetFileName(file));
				Console.WriteLine("Current");
				Console.WriteLine("   CurrentSolutionConfigurationContents: " + p.GetPropertyValue("CurrentSolutionConfigurationContents"));
				Console.WriteLine("   Configuration: " + p.GetPropertyValue("Configuration"));
				Console.WriteLine("   Platform: " + p.GetPropertyValue("Platform"));
				Console.WriteLine("New");
				Console.WriteLine("   CurrentSolutionConfigurationContents: " + slnConfigContents);
				Console.WriteLine("   Configuration: " + configuration);
				Console.WriteLine("   Platform: " + platform);
				p.SetGlobalProperty("CurrentSolutionConfigurationContents", slnConfigContents);
				p.SetGlobalProperty("Configuration", configuration);
				if (!string.IsNullOrEmpty(platform))
					p.SetGlobalProperty("Platform", platform);
				else
					p.RemoveGlobalProperty("Platform");

				p.ReevaluateIfNecessary();
			} else {
				Console.WriteLine("REUSING CONF FOR " + Path.GetFileName(file));
			}

			return p;
		}


        /// <summary>
		/// Builds a list of targets with the specified loggers.
		/// </summary>
		internal bool Build(ProjectInstance pi, string[] targets, IEnumerable<ILogger> loggers)
		{
			if (null == targets)
			{
				targets = Array.Empty<string>();
			}

			BuildResult results;

			BuildManager buildManager = BuildManager.DefaultBuildManager;

			BuildParameters parameters = new BuildParameters(engine);
			parameters.ResetCaches = false;
			parameters.EnableNodeReuse = true;
			BuildRequestData data = new BuildRequestData(pi, targets, parameters.HostServices);

			if (loggers != null)
			{
				parameters.Loggers = (loggers is ICollection<ILogger>) ? ((ICollection<ILogger>)loggers) : new List<ILogger>(loggers);

				// Enables task parameter logging based on whether any of the loggers attached
				// to the Project have their verbosity set to Diagnostic. If no logger has
				// been set to log diagnostic then the existing/default value will be persisted.
				parameters.LogTaskInputs = parameters.LogTaskInputs || loggers.Any(logger => logger.Verbosity == LoggerVerbosity.Diagnostic);
			}

			parameters.MaxNodeCount = 1;

			results = buildManager.Build(parameters, data);

			return results.OverallResult == BuildResultCode.Success;
		}
	}
}
