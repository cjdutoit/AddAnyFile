﻿using EnvDTE;

using EnvDTE80;

using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace MadsKristensen.AddAnyFile
{
	[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
	[InstalledProductRegistration("#110", "#112", Vsix.Version, IconResourceID = 400)]
	[ProvideMenuResource("Menus.ctmenu", 1)]
	[Guid(PackageGuids.guidAddAnyFilePkgString)]
	public sealed class AddAnyFilePackage : AsyncPackage
	{
		private const string SolutionItemsProjectName = "Solution Items";

		public static DTE2 _dte;

		protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			await JoinableTaskFactory.SwitchToMainThreadAsync();

			_dte = await GetServiceAsync(typeof(DTE)) as DTE2;
			Assumes.Present(_dte);

			Logger.Initialize(this, Vsix.Name);

			if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
			{
				CommandID menuCommandID = new CommandID(PackageGuids.guidAddAnyFileCmdSet, PackageIds.cmdidMyCommand);
				OleMenuCommand menuItem = new OleMenuCommand(ExecuteAsync, menuCommandID);
				mcs.AddCommand(menuItem);
			}
		}

		private async void ExecuteAsync(object sender, EventArgs e)
		{
			NewItemTarget target = NewItemTarget.Create(_dte);

			if (target == null)
			{
				MessageBox.Show(
						"Could not determine where to create the new file. Select a file or folder in Solution Explorer and try again.",
						Vsix.Name,
						MessageBoxButton.OK,
						MessageBoxImage.Error);
				return;
			}

			string input = PromptForFileName(target.Directory).TrimStart('/', '\\').Replace("/", "\\");

			if (string.IsNullOrEmpty(input))
			{
				return;
			}

			string[] parsedInputs = GetParsedInput(input);

			foreach (string name in parsedInputs)
			{
				try
				{
					await AddItemAsync(name, target);
				}
				catch (PathTooLongException ex)
				{
					MessageBox.Show("The file name is too long 😢", Vsix.Name, MessageBoxButton.OK, MessageBoxImage.Error);
					Logger.Log(ex);
				}
				catch (Exception ex)
				{
					Logger.Log(ex);
					MessageBox.Show(
							$"Error creating file '{name}':{Environment.NewLine}{ex.Message}",
							Vsix.Name,
							MessageBoxButton.OK,
							MessageBoxImage.Error);
				}
			}
		}

		private async System.Threading.Tasks.Task AddItemAsync(string name, NewItemTarget target)
		{
			if (name.EndsWith("\\", StringComparison.Ordinal))
			{
				if (target.IsSolutionOrSolutionFolder)
				{
					GetOrAddSolutionFolder(name, target);
				}
				else
				{
					AddProjectFolder(name, target);
				}
			}
			else
			{
				await AddFileAsync(name, target);
			}
		}

		private async System.Threading.Tasks.Task AddFileAsync(string name, NewItemTarget target)
		{
			FileInfo file;


			// If the file is being added to a solution folder, but that
			// solution folder doesn't have a corresponding directory on
			// disk, then write the file to the root of the solution instead.
			if (target.IsSolutionFolder && !Directory.Exists(target.Directory))
			{
				file = new FileInfo(Path.Combine(Path.GetDirectoryName(_dte.Solution.FullName), Path.GetFileName(name)));
			}
			else
			{
				file = new FileInfo(Path.Combine(target.Directory, name));
			}

			if (!IsFileNameValid(file.Name))
			{
				MessageBox.Show($"The file name '{file.Name}' is a system reserved name.", Vsix.Name, MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}

			PackageUtilities.EnsureOutputPath(file.DirectoryName);

			if (!file.Exists)
			{
				try
				{
					Project project;

					if (target.IsSolutionOrSolutionFolder)
					{
						project = GetOrAddSolutionFolder(Path.GetDirectoryName(name), target);
					}
					else
					{
						project = target.Project;
					}

					int position = await WriteFileAsync(project, file.FullName);
					if (target.ProjectItem != null && target.ProjectItem.IsKind(Constants.vsProjectItemKindVirtualFolder))
					{
						target.ProjectItem.ProjectItems.AddFromFile(file.FullName);
					}
					else
					{
						project.AddFileToProject(file);
					}

					VsShellUtilities.OpenDocument(this, file.FullName);

					// Move cursor into position.
					if (position > 0)
					{
						Microsoft.VisualStudio.Text.Editor.IWpfTextView view = ProjectHelpers.GetCurentTextView();

						if (view != null)
						{
							view.Caret.MoveTo(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, position));
						}
					}

					ExecuteCommandIfAvailable("SolutionExplorer.SyncWithActiveDocument");
					_dte.ActiveDocument.Activate();
				}
				catch (Exception ex)
				{
					Logger.Log(ex);
				}
			}
			else
			{
				MessageBox.Show($"The file '{file}' already exists.", Vsix.Name, MessageBoxButton.OK, MessageBoxImage.Information);
			}
		}

		private static bool IsFileNameValid(string fileName)
		{
			string[] list = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT0" };
			return !list.Contains(fileName, StringComparer.OrdinalIgnoreCase);
		}

		private static async Task<int> WriteFileAsync(Project project, string file)
		{
			string extension = Path.GetExtension(file);
			string template = await TemplateMap.GetTemplateFilePathAsync(project, file);

			if (!string.IsNullOrEmpty(template))
			{
				int index = template.IndexOf('$');

				if (index > -1)
				{
					template = template.Remove(index, 1);
				}

				await WriteToDiskAsync(file, template);
				return index;
			}

			await WriteToDiskAsync(file, string.Empty);

			return 0;
		}

		private static async System.Threading.Tasks.Task WriteToDiskAsync(string file, string content)
		{
			using (StreamWriter writer = new StreamWriter(file, false, GetFileEncoding(file)))
			{
				await writer.WriteAsync(content);
			}
		}

		private static Encoding GetFileEncoding(string file)
		{
			string[] noBom = { ".cmd", ".bat", ".json" };
			string ext = Path.GetExtension(file).ToLowerInvariant();

			if (noBom.Contains(ext))
			{
				return new UTF8Encoding(false);
			}

			return new UTF8Encoding(true);
		}

		private Project GetOrAddSolutionFolder(string name, NewItemTarget target)
		{
			if (target.IsSolution && string.IsNullOrEmpty(name))
			{
				// An empty solution folder name means we are not creating any solution 
				// folders for that item, and the file we are adding is intended to be 
				// added to the solution. Files cannot be added directly to the solution,
				// so there is a "Solution Items" folder that they are added to.
				return _dte.Solution.FindSolutionFolder(SolutionItemsProjectName)
						?? ((Solution2)_dte.Solution).AddSolutionFolder(SolutionItemsProjectName);
			}

			// Even though solution folders are always virtual, if the target directory exists,
			// then we will also create the new directory on disk. This ensures that any files
			// that are added to this folder will end up in the corresponding physical directory.
			if (Directory.Exists(target.Directory))
			{
				PackageUtilities.EnsureOutputPath(Path.Combine(target.Directory, name));
			}

			Project parent = target.Project;

			foreach (string segment in SplitPath(name))
			{
				// If we don't have a parent project yet, 
				// then this folder is added to the solution.
				if (parent == null)
				{
					parent = _dte.Solution.FindSolutionFolder(segment) ?? ((Solution2)_dte.Solution).AddSolutionFolder(segment);
				}
				else
				{
					parent = parent.FindSolutionFolder(segment) ?? ((SolutionFolder)parent.Object).AddSolutionFolder(segment);
				}
			}

			return parent;
		}

		private void AddProjectFolder(string name, NewItemTarget target)
		{
			// Make sure the directory exists before we add it to the project.
			PackageUtilities.EnsureOutputPath(Path.Combine(target.Directory, name));

			// We can't just add the final directory to the project because that will 
			// only add the final segment rather than adding each segment in the path.
			// Split the name into segments and add each folder individually.
			ProjectItems items = target.ProjectItem?.ProjectItems ?? target.Project.ProjectItems;
			string parentDirectory = target.Directory;

			foreach (string segment in SplitPath(name))
			{
				parentDirectory = Path.Combine(parentDirectory, segment);

				// Look for an existing folder in case it's already in the project.
				ProjectItem folder = items
						.OfType<ProjectItem>()
						.Where(item => segment.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
						.Where(item => item.IsKind(Constants.vsProjectItemKindPhysicalFolder, Constants.vsProjectItemKindVirtualFolder))
						.FirstOrDefault();

				if (folder == null)
				{
					folder = items.AddFromDirectory(parentDirectory);
				}

				items = folder.ProjectItems;
			}
		}

		private static string[] SplitPath(string path)
		{
			return path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
		}

		private static string[] GetParsedInput(string input)
		{
			// var tests = new string[] { "file1.txt", "file1.txt, file2.txt", ".ignore", ".ignore.(old,new)", "license", "folder/",
			//    "folder\\", "folder\\file.txt", "folder/.thing", "page.aspx.cs", "widget-1.(html,js)", "pages\\home.(aspx, aspx.cs)",
			//    "home.(html,js), about.(html,js,css)", "backup.2016.(old, new)", "file.(txt,txt,,)", "file_@#d+|%.3-2...3^&.txt" };
			Regex pattern = new Regex(@"[,]?([^(,]*)([\.\/\\]?)[(]?((?<=[^(])[^,]*|[^)]+)[)]?");
			List<string> results = new List<string>();
			Match match = pattern.Match(input);

			while (match.Success)
			{
				// Always 4 matches w. Group[3] being the extension, extension list, folder terminator ("/" or "\"), or empty string
				string path = match.Groups[1].Value.Trim() + match.Groups[2].Value;
				string[] extensions = match.Groups[3].Value.Split(',');

				foreach (string ext in extensions)
				{
					string value = path + ext.Trim();

					// ensure "file.(txt,,txt)" or "file.txt,,file.txt,File.TXT" returns as just ["file.txt"]
					if (value != "" && !value.EndsWith(".", StringComparison.Ordinal) && !results.Contains(value, StringComparer.OrdinalIgnoreCase))
					{
						results.Add(value);
					}
				}
				match = match.NextMatch();
			}
			return results.ToArray();
		}

		private string PromptForFileName(string folder)
		{
			DirectoryInfo dir = new DirectoryInfo(folder);
			FileNameDialog dialog = new FileNameDialog(dir.Name);

			IntPtr hwnd = new IntPtr(_dte.MainWindow.HWnd);
			System.Windows.Window window = (System.Windows.Window)HwndSource.FromHwnd(hwnd).RootVisual;
			dialog.Owner = window;

			bool? result = dialog.ShowDialog();
			return (result.HasValue && result.Value) ? dialog.Input : string.Empty;
		}

		private void ExecuteCommandIfAvailable(string commandName)
		{
			Command command;

			try
			{
				command = _dte.Commands.Item(commandName);
			}
			catch (ArgumentException)
			{
				// The command does not exist, so we can't execute it.
				return;
			}

			if (command.IsAvailable)
			{
				_dte.ExecuteCommand(commandName);
			}
		}
	}
}