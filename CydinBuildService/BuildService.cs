﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Text;
using Mono.Addins.Setup;
using System.Xml;
using CydinBuildService.n127_0_0_1;
using ICSharpCode.SharpZipLib.Zip;
using System.Net;
using System.Xml.Serialization;

namespace CydinBuildService
{
	public class BuildService
	{
		AutoResetEvent buildEvent = new AutoResetEvent (false);
		bool running = false;
		Thread builderThread;
		BuildContext mainContext = new BuildContext ();
		Queue<ServerEventArgs> eventQueue = new Queue<ServerEventArgs> ();

		public BuildService ()
		{
		}
		
		public void Start (string url)
		{
			mainContext.Server = new Server ();
			mainContext.LocalSettings = Settings.Default;
			mainContext.LocalSettings.Dump ();
			
			if (url == null)
				url = mainContext.LocalSettings.WebSiteUrl;
			if (url != null)
				mainContext.Server.Url = url + "/WebService/Server.asmx";
			
			int i = mainContext.Server.Url.IndexOf ("/WebService");
			mainContext.ServerUrl = mainContext.Server.Url.Substring (0, i);
			LogService.WriteLine ("Connecting to: " + mainContext.ServerUrl);
			
			if (running)
				return;
			
			if (mainContext.LocalSettings.AppArmorSandbox)
				SandboxService.Initialize ();
			
			running = true;
			builderThread = new Thread (Run);
			builderThread.Start ();
		}

		public bool IsRunning
		{
			get { return running; }
		}

		public void Stop ()
		{
			if (builderThread != null) {
				running = false;
				buildEvent.Set ();
				if (!builderThread.Join (5000))
					builderThread.Abort ();
				builderThread = null;
			}
		}

		void Run ()
		{
			mainContext.Event += HandleEvent;
			mainContext.Disconnected += HandleDisconnected;
			QueueBuildAll ();
			
			while (running) {
				if (!mainContext.Connected) {
					ConnectToServer (mainContext);
					if (!mainContext.Connected) {
						Thread.Sleep (5000);
						continue;
					}
				}
				
				if (running && mainContext.Connected) {
					ServerEventArgs ev;
					do {
						// Consume the event queue
						ev = null;
						lock (eventQueue) {
							if (eventQueue.Count > 0)
								ev = eventQueue.Dequeue ();
						}
						if (ev != null) {
							Build (mainContext, ev);
						}
					} while (ev != null);
				}
				
				if (!buildEvent.WaitOne (mainContext.LocalSettings.PollWaitMinutes * 1000 * 60))
					QueueBuildAll ();
					
			}
			mainContext.Status = "Stopped";
		}

		void HandleDisconnected (object sender, EventArgs e)
		{
			// Wait a bit and try reconnecting
			Thread.Sleep (5000);
			buildEvent.Set ();
		}

		void HandleEvent (object sender, ServerEventArgs e)
		{
			LogService.WriteLine ("Got event: " + e.AppId + " " + e.ProjectId + " " + e.EventId + " " + e.EventArgs);
			lock (eventQueue) {
				eventQueue.Enqueue (e);
			}
			buildEvent.Set ();
		}
		
		void QueueBuildAll ()
		{
			ServerEventArgs e = new ServerEventArgs () { EventId = "BuildAll", AppId=-1, ProjectId=-1 };
			lock (eventQueue) {
				eventQueue.Enqueue (e);
			}
		}
		
		bool ConnectToServer (BuildContext ctx)
		{
			try {
				if (ctx.Connect ()) {
					ctx.Connected = true;
					return true;
				}
				else {
					if (ctx.FirstNotAuthorised) {
						LogService.WriteLine ("ERROR: Connection to server not authorized.");
						LogService.WriteLine ("To enable connections from this service, go to the administration page");
						LogService.WriteLine ("in the server and click on the Change Service option.");
						ctx.FirstNotAuthorised = false;
					} else
						LogService.WriteLine ("Connection not authorized. Trying again.");
				}
			} catch (Exception ex) {
				LogService.WriteLine ("Connection failed: " + ex.Message);
			}
			ctx.Connected = false;
			return false;
		}
		
		void Build (BuildContext ctx, ServerEventArgs ev)
		{
			try {
				ctx.ServerSettings = ctx.Server.GetSettings ();
			}
			catch (Exception ex) {
				HandleError (ctx, ex);
				return;
			}
			
			foreach (ApplicationInfo app in ctx.ServerSettings.Applications.Where (ap => ev.AppId == -1 || ev.AppId == ap.Id)) {
				try {
					ctx.Application = app;
					UpdateSourceTags (ctx, ev);
					BuildProjects (ctx, ev);
				}
				catch (Exception ex) {
					if (!HandleError (ctx, ex))
						return;
				}
			}
			
			try {
				ctx.Status = "Waiting";
			}
			catch (Exception ex) {
				HandleError (ctx, ex);
				return;
			}
		}
		
		bool HandleError (BuildContext ctx, Exception ex)
		{
			try {
				ctx.Status = "ERROR: " + ex.Message;
				ctx.Log (ex);
				return true;
			} catch (Exception e) {
				LogService.WriteLine (e);
				ctx.Connected = false;
				return false;
			}
		}
		
		public void RefreshAppRelease (BuildContext ctx, AppReleaseInfo release)
		{
			string filePath = release.GetAssembliesPath (ctx);

			string timestamp = release.LastUpdateTime.ToString ();
			string timestampFile = Path.Combine (filePath, "__timestamp");
			try {
				if (File.Exists (timestampFile)) {
					string date = File.ReadAllText (timestampFile);
					if (date == timestamp)
						return;
				}
			} catch (Exception ex) {
				ctx.Log (ex);
			}

			Util.ResetFolder (filePath);
			
			ctx.Status = "Downloading assembly package for release " + release.AppVersion;
			
			WebRequest req = HttpWebRequest.Create (ctx.ServerUrl + release.ZipUrl);
			WebResponse res = req.GetResponse ();
			string wfname = res.Headers["Content-Disposition"];
			string ext;
			if (string.IsNullOrEmpty (wfname))
				ext = ".zip";
			else
				ext = Path.GetExtension (wfname);
			
			string file = Path.Combine (filePath, "__release" + ext);
			res.GetResponseStream ().SaveToFile (file);

			ctx.Status = "Extracting assemblies for release " + release.AppVersion;
			
			if (ext == ".dmg") {
				ExtractDmg (ctx, filePath, file);
			}
			else {
				try {
					using (Stream fs = File.OpenRead (file)) {
						ZipFile zfile = new ZipFile (fs);
						foreach (ZipEntry ze in zfile) {
							string fname = ze.Name.ToLower ();
							if (fname.EndsWith (".dll") || fname.EndsWith (".exe")) {
								using (Stream s = zfile.GetInputStream (ze)) {
									s.SaveToFile (Path.Combine (filePath, Path.GetFileName (ze.Name)));
								}
							}
						}
					}
				} catch (ZipException) {
					// Maybe it is a dmg after all
					ExtractDmg (ctx, filePath, file);
				}
			}
			File.Delete (file);
			File.WriteAllText (timestampFile, timestamp);
			ctx.Status = "Assembly package for release " + release.AppVersion + " isntalled";
		}
		
		void ExtractDmg (BuildContext ctx, string filePath, string file)
		{
			ctx.Status ="Extracting DMG";
			string tempPath = Path.Combine (filePath, "__tmp_release");
			string tempPath2 = Path.Combine (filePath, "__tmp_release2");
			Util.ResetFolder (tempPath);
			try {
				Run7z (file, tempPath);
				string hfs = Directory.GetFiles (tempPath, "*.hfs").FirstOrDefault ();
				if (hfs == null) {
					ctx.Log (LogSeverity.Error, "Unknown release archive: " + file);
					return;
				}
				Run7z (hfs, tempPath2);
				CopyDlls (tempPath2, filePath);
			} finally {
				if (Directory.Exists (tempPath))
					Directory.Delete (tempPath, true);
				if (Directory.Exists (tempPath2))
					Directory.Delete (tempPath2, true);
			}
		}
		
		void Run7z (string archiveFile, string targetDir)
		{
			RunCommand (true, "7z", "x -o" + targetDir + " " + archiveFile, new StringBuilder (), new StringBuilder (), Timeout.Infinite);
		}
		
		void CopyDlls (string dir, string targetDir)
		{
			foreach (var f in Directory.GetFiles (dir)) {
				if (f.IndexOf ("/moonlight/") != -1)
					continue;
				var ext = Path.GetExtension (f);
				if (ext == ".dll" || ext == ".exe") {
					string tfile = Path.Combine (targetDir, Path.GetFileName (f));
					File.Copy (f, tfile, true);
				}
			}
			foreach (var d in Directory.GetDirectories (dir))
				CopyDlls (d, targetDir);
		}

		public void UpdateSourceTags (BuildContext ctx, ServerEventArgs ev)
		{
			ctx.Status = "Updating version control source tags";
			foreach (SourceInfo s in ctx.Server.GetSources (ctx.AppId)) {
				if (ev.ProjectId != -1 && s.ProjectId != ev.ProjectId)
					continue;
				ctx.Status = "Updating version control source tags for project '" + s.ProjectName + "'";
				UpdateSourceTags (ctx, s, true);
			}
		}

		public void UpdateSourceTags (BuildContext ctx, SourceInfo source, bool force)
		{
			if (!force && (DateTime.Now - source.LastFetchTime).TotalMinutes < 5)
				return;
			ctx.Server.SetSourceStatus (ctx.AppId, source.Id, SourceTagStatus.Fetching, "");
			try {
				List<SourceTagInfo> newTags = new List<SourceTagInfo> (source.SourceTags);
				HashSet<string> newUrls = new HashSet<string> ();
				
				SourcePuller sp = VersionControl.GetSourcePuller (source.Type);
				foreach (SourceTagInfo st in sp.GetChildUrls (ctx, source)) {
					newUrls.Add (st.Url);
					SourceTagInfo currentTag = source.GetSourceTag (st.Url);
					if (currentTag == null) {
						st.Status = SourceTagStatus.Waiting;
						newTags.Add (st);
					}
					else {
						if (currentTag.LastRevision != st.LastRevision) {
							source.CleanSources (ctx, currentTag);
							currentTag.LastRevision = st.LastRevision;
							currentTag.Status = SourceTagStatus.Waiting;
						}
					}
				}
				foreach (SourceTagInfo st in source.SourceTags) {
					if (!newUrls.Contains (st.Url))
						newTags.Remove (st);
				}
				source.LastFetchTime = DateTime.Now;
				ctx.Server.UpdateSourceTags (ctx.AppId, source.Id, DateTime.Now, newTags.ToArray ());
				ctx.Server.SetSourceStatus (ctx.AppId, source.Id, SourceTagStatus.Ready, "");
			}
			catch (Exception ex) {
				ctx.Server.SetSourceStatus (ctx.AppId, source.Id, SourceTagStatus.FetchError, ex.Message);
				ctx.Log (ex);
			}
		}

		void BuildProjects (BuildContext ctx, ServerEventArgs ev)
		{
			foreach (SourceInfo source in ctx.Server.GetSources (ctx.AppId)) {
				if (ev.ProjectId != -1 && ev.ProjectId != source.ProjectId)
					continue;
				foreach (SourceTagInfo stag in source.SourceTags) {
					try {
						string sourcesPath = source.GetAddinSourcePath (ctx, stag);
						if (!Directory.Exists (sourcesPath) || !Directory.Exists (stag.GetPackagePath (ctx)) || stag.Status == SourceTagStatus.Waiting) {
							BuildSource (ctx, source, stag);
						}
					}
					catch (Exception ex) {
						try {
							File.AppendAllText (stag.GetLogFile (ctx), "<pre>" + HttpUtility.HtmlEncode (ex.ToString ()) + "</pre>");
							PushFiles (ctx, source, stag, true);
							ctx.Server.SetSourceTagStatus (ctx.AppId, stag.Id, SourceTagStatus.BuildError);
						}
						catch { }
					}
				}
			}
		}
		
		bool BuildSource (BuildContext ctx, SourceInfo vcs, SourceTagInfo stag)
		{
			// Fetch the source

			ctx.Status = "Fetching project " + vcs.ProjectName + " (" + stag.Name + ")";

			Util.ResetFolder (stag.GetPackagePath (ctx));
			
			string logFile = stag.GetLogFile (ctx);

			File.AppendAllText (logFile, "<p><b>Fetching Source</b></p>");
			ctx.Server.SetSourceTagStatus (ctx.AppId, stag.Id, SourceTagStatus.Fetching);
			try {
				FetchSource (ctx, vcs, stag, logFile);
			}
			catch (Exception ex) {
				File.AppendAllText (logFile, HttpUtility.HtmlEncode (ex.Message) + "<br/><br/>");
				PushFiles (ctx, vcs, stag, true);
				ctx.Server.SetSourceTagStatus (ctx.AppId, stag.Id, SourceTagStatus.FetchError);
				ctx.Log (ex);
				return false;
			}

			// Build the add-in
			
			ctx.Status = "Building project " + vcs.ProjectName;
			File.AppendAllText (logFile, "<p><b>Building Solution</b></p>");
			ctx.Server.SetSourceTagStatus (ctx.AppId, stag.Id, SourceTagStatus.Building);
			try {
				BuildSource (ctx, vcs, stag, logFile);
			}
			catch (Exception ex) {
				File.AppendAllText (logFile, "<p>" + HttpUtility.HtmlEncode (ex.Message) + "</p>");
				PushFiles (ctx, vcs, stag, true);
				ctx.Server.SetSourceTagStatus (ctx.AppId, stag.Id, SourceTagStatus.BuildError);
				ctx.Log (ex);
				return false;
			}

			PushFiles (ctx, vcs, stag, false);
			ctx.Server.SetSourceTagBuiltAsync (ctx.AppId, stag.Id);
			return true;
		}

		void FetchSource (BuildContext ctx, SourceInfo vcs, SourceTagInfo stag, string logFile)
		{
			StringBuilder sb = new StringBuilder ();
			try {
				SourcePuller sp = VersionControl.GetSourcePuller (vcs.Type);
				sp.Fetch (ctx, vcs.Id, stag, sb, sb);
			}
			finally {
				File.AppendAllText (logFile, "<pre>" + HttpUtility.HtmlEncode (sb.ToString ()) + "</pre><br/><br/>");
			}
		}

		public string NormalizePath (string path)
		{
			if (Path.DirectorySeparatorChar == '\\')
				return path.Replace ('/', '\\');
			else
				return path.Replace ('\\', '/');
		}

		void BuildSource (BuildContext ctx, SourceInfo source, SourceTagInfo stag, string logFile)
		{
			SourcePuller sp = VersionControl.GetSourcePuller (source.Type);
			sp.PrepareForBuild (ctx, source.Id, stag);
			
			string destPath = Path.GetFullPath (source.GetAddinSourcePath (ctx, stag));
			string sourcePath = destPath;

			if (!string.IsNullOrEmpty (source.Directory))
				sourcePath = Path.Combine (sourcePath, NormalizePath (source.Directory));

			sourcePath = Path.GetFullPath (sourcePath);
			
			if (sourcePath != destPath && !sourcePath.StartsWith (destPath))
				throw new Exception ("Invalid source directory: " + source.Directory);

			string projectFile = sourcePath;
			if (!File.Exists (projectFile) && !projectFile.EndsWith (".xml"))
				projectFile = Path.Combine (sourcePath, "addin-project.xml");
			else
				sourcePath = Path.GetDirectoryName (projectFile);

			if (!File.Exists (projectFile)) {
				string msg = "addin-project.xml file not found";
				if (!string.IsNullOrEmpty (source.Directory))
					msg += " (looked in '" + source.Directory + "')";
				throw new Exception (msg);
			}

			AddinProject addinProject;
			StreamReader sr = new StreamReader (projectFile);
			using (sr) {
				XmlSerializer ser = new XmlSerializer (typeof(AddinProject));
				addinProject = (AddinProject) ser.Deserialize (sr);
			}
			
			if (string.IsNullOrEmpty (addinProject.AppVersion))
				throw new Exception ("Target application version not specified in addin-project.xml");
			
			AppReleaseInfo rel = ctx.Server.GetAppReleases (ctx.AppId).Where (r => r.AppVersion == addinProject.AppVersion).FirstOrDefault ();
			if (rel == null)
				throw new Exception ("Application release " + addinProject.AppVersion + " not found.");
			
			RefreshAppRelease (ctx, rel);

			// Delete old packages

			foreach (string f in Directory.GetFiles (sourcePath, "*.mpack"))
				File.Delete (f);
			
			List<AddinData> addins = new List<AddinData> ();
			
			foreach (AddinProjectAddin addin in addinProject.Addins) {
				addins.Add (BuildProjectAddin (ctx, stag, logFile, sourcePath, rel, addin));
			}
			ctx.Server.SetSourceTagBuildData (ctx.AppId, stag.Id, addins.ToArray ());
		}
		
		AddinData BuildProjectAddin (BuildContext ctx, SourceTagInfo stag, string logFile, string sourcePath, AppReleaseInfo rel, AddinProjectAddin addin)
		{
			SetupService ss = new SetupService ();
			bool generatedXplatPackage = false;
			HashSet<string> foundPlatforms = new HashSet<string> ();
			AddinData ainfo = new AddinData ();
			
			foreach (AddinProjectSource psource in addin.Sources) {
				if (string.IsNullOrEmpty (psource.AddinFile))
					throw new Exception ("AddinFile element not found in addin-project.xml");
	
				string platforms = psource.Platforms;
				if (string.IsNullOrEmpty (platforms))
					platforms = ctx.Application.Platforms;
	
				string[] platformList = platforms.Split (new string[] { " " }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string plat in platformList)
					if (!foundPlatforms.Add (plat))
						throw new Exception ("Platform " + plat + " specificed in more than open Project element");
	
				string outFile = NormalizePath (psource.AddinFile);
	
				if (!string.IsNullOrEmpty (psource.BuildFile)) {
	
					// Build the project
					
					// Move the sources to the work area (which is sandboxed)
					
					if (!Directory.Exists (ctx.LocalSettings.WorkAreaPath))
						Directory.CreateDirectory (ctx.LocalSettings.WorkAreaPath);
					
					string workArea = Path.Combine (ctx.LocalSettings.WorkAreaPath, "builder");
					if (Directory.Exists (workArea))
						Directory.Delete (workArea, true);
					
					Directory.Move (sourcePath, workArea);
					
					try {
		
						string solFile = Path.Combine (workArea, NormalizePath (psource.BuildFile));
		
						string ops = " \"/p:ReferencePath=" + rel.GetAssembliesPath (ctx) + "\"";
						
						if (!string.IsNullOrEmpty (psource.BuildConfiguration))
							ops += " \"/property:Configuration=" + psource.BuildConfiguration + "\"";
						
						ops = ops + " \"" + solFile + "\"";
		
						StringBuilder output = new StringBuilder ();
						try {
							// Clean the project
							RunCommand (true, ctx.LocalSettings.MSBuildCommand, "/t:Clean " + ops, output, output, Timeout.Infinite);
							
							// Build
							RunCommand (true, ctx.LocalSettings.MSBuildCommand, ops, output, output, Timeout.Infinite);
						}
						finally {
							output = output.Replace (workArea, "/build");
							File.AppendAllText (logFile, "<pre>" + HttpUtility.HtmlEncode (output.ToString ()) + "</pre>");
						}
					}
					finally {
						Directory.Move (workArea, sourcePath);
					}
				}
	
				// Generate the package
	
				string tmpPath = Path.Combine (sourcePath, "tmp");
				
				File.AppendAllText (logFile, "<p><b>Building Package</b></p>");
				LocalStatusMonitor monitor = new LocalStatusMonitor ();
				try {
					if (Directory.Exists (tmpPath))
						Directory.Delete (tmpPath, true);
					Directory.CreateDirectory (tmpPath);
					ss.BuildPackage (monitor, tmpPath, Path.Combine (sourcePath, outFile));
					string file = Directory.GetFiles (tmpPath, "*.mpack").FirstOrDefault ();
					if (file == null)
						throw new Exception ("Add-in generation failed");
	
					AddinInfo ai = ReadAddinInfo (file);
					ainfo.AddinVersion = ai.Version;
					ainfo.AddinId = Mono.Addins.Addin.GetIdName (ai.Id);
	
					if (!generatedXplatPackage && platformList.Length > 0) {
						File.Copy (file, Path.Combine (stag.GetPackagePath (ctx), "All.mpack"), true);
						generatedXplatPackage = true;
					}
					else {
						foreach (string plat in platformList) {
							File.Copy (file, Path.Combine (stag.GetPackagePath (ctx), plat + ".mpack"), true);
						}
					}
				}
				finally {
					try {
						Directory.Delete (tmpPath, true);
					}
					catch { }
					File.AppendAllText (logFile, "<pre>" + HttpUtility.HtmlEncode (monitor.ToString ()) + "</pre>");
				}
			}
			ainfo.Platforms = string.Join (" ", foundPlatforms.ToArray ());
			ainfo.AppVersion = rel.AppVersion;
			return ainfo;
		}

		void PushFiles (BuildContext ctx, SourceInfo source, SourceTagInfo stag, bool safe)
		{
			try {
				ctx.Status = "Uploading files for project " + source.ProjectName;
				foreach (string file in Directory.GetFiles (stag.GetPackagePath (ctx))) {
					ctx.Log (LogSeverity.Info, "Uploading [" + source.ProjectName + "] " + Path.GetFileName (file));
					WebRequest req = HttpWebRequest.Create (ctx.ServerUrl + "/package/upload");
					req.Headers ["applicationId"] = ctx.AppId.ToString ();
					req.Headers ["sourceTagId"] = stag.Id.ToString ();
					req.Headers ["fileName"] = Path.GetFileName (file);
					req.Method = "POST";
					req.GetRequestStream ().WriteFile (file);
					using (StreamReader s = new StreamReader (req.GetResponse ().GetResponseStream ())) {
						if (s.ReadToEnd () != "OK")
							throw new Exception ("File upload failed");
					}
				}
			} catch {
				if (!safe)
					throw;
			}
			finally {
				ctx.Status = "Files uploaded";
			}
		}
		
		internal static AddinInfo ReadAddinInfo (string file)
		{
			ZipFile zfile = new ZipFile (file);
			try {
				foreach (ZipEntry ze in zfile) {
					if (ze.Name == "addin.info") {
						using (Stream s = zfile.GetInputStream (ze)) {
							return AddinInfo.ReadFromAddinFile (new StreamReader (s));
						}
					}
				}
				throw new InstallException ("Addin configuration file not found in package.");
			}
			finally {
				zfile.Close ();
			}
		}

		internal static void RunCommand (bool sandboxed, string command, string args, StringBuilder output, StringBuilder error, int timeout)
		{
			RunCommand (sandboxed, command, args, output, error, timeout, null);
		}
		
		internal static void RunCommand (bool sandboxed, string command, string args, StringBuilder output, StringBuilder error, int timeout, string workDir)
		{
//			Console.WriteLine ("> " + command + " " + args);
			Process p = new Process ();
			ProcessStartInfo pinfo = p.StartInfo;
			pinfo.FileName = command;
			pinfo.Arguments = args;
			if (workDir != null)
				pinfo.WorkingDirectory = workDir;

			pinfo.UseShellExecute = false;
			pinfo.RedirectStandardOutput = true;
			pinfo.RedirectStandardError = true;
			pinfo.CreateNoWindow = true;

			p.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
			{
				lock (output)
					output.Append (e.Data + "\n");
			};
			p.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
			{
				lock (error)
					error.Append (e.Data + "\n");
			};
			try {
				if (sandboxed)
					SandboxService.EnterSandbox ();
				p.Start ();
			} finally {
				if (sandboxed)
					SandboxService.ExitSandbox ();
			}
			p.BeginOutputReadLine ();
			p.BeginErrorReadLine ();

			if (!p.WaitForExit (timeout)) {
				// Wait for 5 minutes
				output.AppendLine ("\nAborted.");
				try {
					p.Kill ();
				}
				catch { }

				throw new Exception ("Source fetch took more than 5 minutes. Aborted.");
			}
			if (p.ExitCode != 0) {
				throw new Exception ("Command failed.");
			}
		}
		
		internal static string GetTempDirectory ()
		{
			string dir = Path.Combine (Path.GetTempPath (), "Cydin");
			Random r = new Random ();
			string name;
			do {
				name = r.Next ().ToString ("x");
			} while (Directory.Exists (Path.Combine (dir, name)));
			return Path.Combine (dir, name);
		}
	}
	
	static class ModelExtensions
	{
		public static SourceTagInfo GetSourceTag (this SourceInfo source, string url)
		{
			if (source.SourceTags == null)
				return null;
			return source.SourceTags.FirstOrDefault (s => s.Url == url);
		}
		
		public static void CleanSources (this SourceInfo source, BuildContext ctx, SourceTagInfo sourceTag)
		{
			if (Directory.Exists (sourceTag.GetPackagePath (ctx)))
				Directory.Delete (sourceTag.GetPackagePath (ctx), true);
		}
		
		public static string GetPackagePath (this SourceTagInfo stag, BuildContext ctx)
		{
			string path = ctx.LocalSettings.PackagesPath;
			return Path.GetFullPath (Path.Combine (path, stag.Id.ToString ()));
		}
		
		public static string GetAddinSourcePath (this SourceInfo source, BuildContext ctx, SourceTagInfo stag)
		{
			SourcePuller sp = VersionControl.GetSourcePuller (source.Type);
			return sp.GetSourceTagPath (ctx, source.Id, stag.Id);
		}
		
/*		public static string GetAddinSourcePath (this SourceTagInfo stag)
		{
			string path = Settings.Default.SourcesPath;
			return Path.GetFullPath (Path.Combine (path, stag.Id.ToString ()));
		}*/
		
		public static string GetAssembliesPath (this AppReleaseInfo rel, BuildContext ctx)
		{
			return Path.GetFullPath (Path.Combine (ctx.LocalSettings.AppReleasesPath, rel.AppVersion));
		}
		
		public static string GetLogFile (this SourceTagInfo stag, BuildContext ctx)
		{
			return Path.Combine (stag.GetPackagePath (ctx), "log.html");
		}
	}
	
	public class BuildContext
	{
		public ApplicationInfo Application;
		public Settings LocalSettings;
		public SettingsInfo ServerSettings;
		public Server Server;
		public bool FirstNotAuthorised = true;
		
		public string ServerUrl;
		
		public bool Connected;
		StreamReader eventsReader;
		Thread eventsThread;
		
		string status;
		
		public string Status {
			get { return status; }
			set {
				status = value;
				Server.BeginSetBuildServiceStatus (value, null, null);
				LogService.WriteLine (status);
			}
		}
		
		public int AppId {
			get { return Application.Id; }
		}
		
		public void Log (Exception ex)
		{
			string txt = "Error [" + DateTime.Now.ToLongTimeString () + "] " + ex.ToString ();
			LogService.WriteLine (txt);
			Server.Log (LogSeverity.Error, ex.ToString ());
		}
		
		public void Log (LogSeverity severity, string message)
		{
			string txt = severity + " [" + DateTime.Now.ToLongTimeString () + "] " + message;
			LogService.WriteLine (txt);
			Server.Log (severity, message);
		}
		
		public bool Connect ()
		{
			if (!Server.ConnectBuildService ())
				return false;
			
			if (LocalSettings.LiveEventsConnection) {
				if (eventsThread != null) {
					eventsThread.Abort ();
					eventsThread = null;
				}
				try {
					WebRequest req = HttpWebRequest.Create (ServerUrl + "/service/events");
					req.ConnectionGroupName = "EventListener";
					req.Timeout = Timeout.Infinite;
					eventsReader = new StreamReader (req.GetResponse ().GetResponseStream ());
				
					eventsThread = new Thread (ReadEvents);
					eventsThread.IsBackground = true;
					eventsThread.Start ();
				} catch {
					return false;
				}
			}
			return true;
		}
		
		void ReadEvents ()
		{
			do {
				try {
					string lin;
					do {
						lin = eventsReader.ReadLine ();
					}
					while (lin != "[event]" && lin != null);
					
					if (lin == null)
						throw new Exception ("Disconnected");
					
					ServerEventArgs args = new ServerEventArgs ();
					args.EventId = eventsReader.ReadLine ();
					args.AppId = int.Parse (eventsReader.ReadLine ());
					args.ProjectId = int.Parse (eventsReader.ReadLine ());
					int nargs = int.Parse (eventsReader.ReadLine ());
					List<string> eargs = new List<string> ();
					for (int n=0; n<nargs; n++)
						eargs.Add (eventsReader.ReadLine ());
					args.EventArgs = eargs.ToArray ();
					if (Event != null)
						Event (this, args);
				} catch (Exception ex) {
					try {
						eventsReader.Close ();
					} catch { }
					Connected = false;
					eventsThread = null;
					eventsReader = null;
					ThreadPool.QueueUserWorkItem (delegate {
						if (Disconnected != null)
							Disconnected (this, EventArgs.Empty);
					});
					return;
				}
			} while (true);
		}
		
		public event EventHandler Disconnected;
		public event EventHandler<ServerEventArgs> Event;
	}
	
	
	public class ServerEventArgs: EventArgs
	{
		public string EventId;
		public int AppId;
		public int ProjectId;
		public string[] EventArgs;
	}
}