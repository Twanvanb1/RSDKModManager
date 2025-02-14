﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IniFile;
using ModManagerCommon;
using ModManagerCommon.Forms;

namespace RSDKModManager
{
	public partial class MainForm : Form
	{
		public MainForm()
		{
			InitializeComponent();
		}

		private bool checkedForUpdates;

		const string updatePath = ".updates";
		const string loaderinipath = "RSDKModManager.ini";
		const string oldloaderinipath = "mods/RSDKModManager.ini";
		const string _modconfigpath = "mods/modConfig.ini";
		string modconfigpath = _modconfigpath;
		RSDKLoaderInfo loaderini;
		InstalledGame currentGame;
		bool isv5 = false;
		Dictionary<string, RSDKModInfo> mods;

		readonly ModUpdater modUpdater = new ModUpdater();
		BackgroundWorker updateChecker;
		private bool manualModUpdate;

		private static bool UpdateTimeElapsed(UpdateUnit unit, int amount, DateTime start)
		{
			if (unit == UpdateUnit.Always)
			{
				return true;
			}

			TimeSpan span = DateTime.UtcNow - start;

			switch (unit)
			{
				case UpdateUnit.Hours:
					return span.TotalHours >= amount;

				case UpdateUnit.Days:
					return span.TotalDays >= amount;

				case UpdateUnit.Weeks:
					return span.TotalDays / 7.0 >= amount;

				default:
					throw new ArgumentOutOfRangeException(nameof(unit), unit, null);
			}
		}

		private static void SetDoubleBuffered(Control control, bool enable)
		{
			PropertyInfo doubleBufferPropertyInfo = control.GetType().GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
			doubleBufferPropertyInfo?.SetValue(control, enable, null);
		}

		private void MainForm_Load(object sender, EventArgs e)
		{
			// Try to use TLS 1.2
			try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
			if (!Debugger.IsAttached)
				Environment.CurrentDirectory = Application.StartupPath;
			SetDoubleBuffered(modListView, true);
			if (File.Exists(loaderinipath))
				loaderini = IniSerializer.Deserialize<RSDKLoaderInfo>(loaderinipath);
			else if (File.Exists(oldloaderinipath))
			{
				loaderini = IniSerializer.Deserialize<RSDKLoaderInfo>(oldloaderinipath);
				loaderini.Games = new List<InstalledGame>();
			}
			else
				loaderini = new RSDKLoaderInfo();

			checkUpdateStartup.Checked = loaderini.UpdateCheck;
			checkUpdateModsStartup.Checked = loaderini.ModUpdateCheck;
			comboUpdateFrequency.SelectedIndex = (int)loaderini.UpdateUnit;
			numericUpdateFrequency.Value = loaderini.UpdateFrequency;
			autoCloseCheckBox.Checked = loaderini.AutoClose;
		}

		static readonly System.Text.RegularExpressions.Regex githubdl = new System.Text.RegularExpressions.Regex(@"https://github.com/([^/]+/[^/]+)/releases/download/.+/(.+)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
		private void HandleUri(string uri)
		{
			if (WindowState == FormWindowState.Minimized)
			{
				WindowState = FormWindowState.Normal;
			}

			Activate();

			string protocol = uri.Remove(uri.IndexOf(':'));
			Game? game = GameInfo.GetGame(protocol);

			if (!game.HasValue)
			{
				MessageBox.Show(this, $"Unknown URL {uri}!", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			string folder;
			if (currentGame.Game == game.Value)
				folder = currentGame.Folder;
			else
				folder = loaderini.Games.FirstOrDefault(a => a.Game == game.Value)?.Folder;

			if (folder == null)
			{
				MessageBox.Show(this, $"{GameInfo.GetName(game.Value)} is not installed!\n\nURI: {uri}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			Uri url;
			string name;
			string author;
			string[] split = uri.Substring(protocol.Length + 1).Split(',');
			url = new Uri(split[0]);
			Dictionary<string, string> fields = new Dictionary<string, string>(split.Length - 1);
			for (int i = 1; i < split.Length; i++)
			{
				int ind = split[i].IndexOf(':');
				fields.Add(split[i].Substring(0, ind).ToLowerInvariant(), split[i].Substring(ind + 1));
			}
			string gbItemType = null;
			long gbItemId = 0;
			if (fields.ContainsKey("gb_itemtype") && fields.ContainsKey("gb_itemid"))
			{
				try
				{
					gbItemType = fields["gb_itemtype"];
					gbItemId = long.Parse(fields["gb_itemid"]);
				}
				catch (Exception ex)
				{
					MessageBox.Show(this,
									$"Malformed One-Click Install URI \"{uri}\" caused parse failure:\n{ex.Message}",
									"URI Parse Failure",
									MessageBoxButtons.OK,
									MessageBoxIcon.Error);

					return;
				}

				GameBananaItem gbi;

				try
				{
					gbi = GameBananaItem.Load(gbItemType, gbItemId);

					if (gbi is null)
					{
						throw new Exception("GameBananaItem was unexpectedly null");
					}
				}
				catch (Exception ex)
				{
					MessageBox.Show(this,
									$"GameBanana API query failed:\n{ex.Message}",
									"GameBanana API Failure",
									MessageBoxButtons.OK,
									MessageBoxIcon.Error);

					return;
				}
				name = gbi.Name;
				author = gbi.OwnerName;
			}
			else if (fields.ContainsKey("name") && fields.ContainsKey("author"))
			{
				name = Uri.UnescapeDataString(fields["name"]);
				author = Uri.UnescapeDataString(fields["author"]);
			}
			else
			{
				MessageBox.Show(this,
								$"One-Click Install URI \"{uri}\" did not contain required fields.",
								"URI Parse Failure",
								MessageBoxButtons.OK,
								MessageBoxIcon.Error);

				return;
			}

			var dummyInfo = new ModInfo
			{
				Name = name,
				Author = author
			};

			DialogResult result = MessageBox.Show(this, $"Do you want to install mod \"{dummyInfo.Name}\" by {dummyInfo.Author} from {url.DnsSafeHost}?", "Mod Download", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

			if (result != DialogResult.Yes)
			{
				return;
			}

			#region create update folder
			do
			{
				try
				{
					result = DialogResult.Cancel;
					if (!Directory.Exists(updatePath))
					{
						Directory.CreateDirectory(updatePath);
					}
				}
				catch (Exception ex)
				{
					result = MessageBox.Show(this, "Failed to create temporary update directory:\n" + ex.Message
					                               + "\n\nWould you like to retry?", "Directory Creation Failed", MessageBoxButtons.RetryCancel);
				}
			} while (result == DialogResult.Retry);
			#endregion

			string dummyPath = dummyInfo.Name;

			foreach (char c in Path.GetInvalidFileNameChars())
			{
				dummyPath = dummyPath.Replace(c, '_');
			}

			dummyPath = Path.Combine(folder, "mods", dummyPath);

			var updates = new List<ModDownload>
			{
				new ModDownload(dummyInfo, dummyPath, url.AbsoluteUri, null, 0)
			};

			using (var progress = new ModDownloadDialog(updates, updatePath))
			{
				progress.ShowDialog(this);
			}

			do
			{
				try
				{
					result = DialogResult.Cancel;
					Directory.Delete(updatePath, true);
				}
				catch (Exception ex)
				{
					result = MessageBox.Show(this, "Failed to remove temporary update directory:\n" + ex.Message
					                               + "\n\nWould you like to retry? You can remove the directory manually later.",
						"Directory Deletion Failed", MessageBoxButtons.RetryCancel);
				}
			} while (result == DialogResult.Retry);

			try
			{
				string modinipath = Path.Combine(dummyPath, "mod.ini");
				RSDKModInfo modInfo = IniSerializer.Deserialize<RSDKModInfo>(modinipath);
				if (modInfo.GameBananaItemType == null && modInfo.GitHubRepo == null && modInfo.UpdateUrl == null)
				{
					if (gbItemType != null)
					{
						modInfo.GameBananaItemType = gbItemType;
						modInfo.GameBananaItemId = gbItemId;
						IniSerializer.Serialize(modInfo, modinipath);
					}
					else
					{
						var match = githubdl.Match(split[0]);
						if (match.Success)
						{
							modInfo.GitHubRepo = match.Groups[1].Value;
							modInfo.GitHubAsset = match.Groups[2].Value;
							IniSerializer.Serialize(modInfo, modinipath);
						}
					}
				}
			}
			catch { }

			if (currentGame.Game == game.Value) LoadModList();
		}

		private void MainForm_Shown(object sender, EventArgs e)
		{
			if (CheckForUpdates())
				return;

			if (loaderini.Games.Count == 0)
			{
				if (!string.IsNullOrEmpty(loaderini.EXEFile))
				{
					InstalledGame game = new InstalledGame() { Folder = Environment.CurrentDirectory, EXEFile = loaderini.EXEFile, ModUpdateTime = loaderini.ModUpdateTime };
					if (loaderini.Game.HasValue)
					{
						game.Game = loaderini.Game.Value;
						game.Name = GameInfo.GetName(game.Game);
					}
					else
						using (GameSelectForm gsf = new GameSelectForm(GetDefaultGame(game.EXEFile.ToLowerInvariant())))
						{
							gsf.ShowDialog();
							game.Game = gsf.Game;
							game.Name = gsf.GameName;
						}
					loaderini.Games.Add(game);
					loaderini.Game = null;
					loaderini.EXEFile = null;
					loaderini.ModUpdateTime = 0;
				}
				else if (!AddGame())
				{
					Close();
					return;
				}
			}

			gameSelector.BeginUpdate();
			foreach (var item in loaderini.Games)
				gameSelector.Items.Add(item.Name);
			gameSelector.Items.Add("New Game");
			gameSelector.EndUpdate();

			gameSelector.SelectedIndex = loaderini.LastGame;
			removeGameButton.Enabled = loaderini.Games.Count > 1;

			List<string> uris = Program.UriQueue.GetUris();

			foreach (string str in uris)
			{
				HandleUri(str);
			}

			Program.UriQueue.UriEnqueued += UriQueueOnUriEnqueued;
		}

		private bool AddGame()
		{
			using (OpenFileDialog dlg = new OpenFileDialog() { DefaultExt = "exe", Filter = "RSDK EXE Files|RSDKv*.exe;Nexus_64.exe;restored.exe;SonicForever.exe;Sonic2Absolute.exe|All Files|*", RestoreDirectory = true, Title = "Locate the game's executable." })
				if (dlg.ShowDialog(this) == DialogResult.OK)
				{
					InstalledGame game = new InstalledGame() { Folder = Path.GetDirectoryName(dlg.FileName), EXEFile = Path.GetFileName(dlg.FileName) };
					using (GameSelectForm gsf = new GameSelectForm(GetDefaultGame(game.EXEFile.ToLowerInvariant())))
					{
						gsf.ShowDialog();
						game.Game = gsf.Game;
						game.Name = gsf.GameName;
					}
					loaderini.Games.Add(game);
					return true;
				}
				else
					return false;
		}

		private void UriQueueOnUriEnqueued(object sender, OnUriEnqueuedArgs args)
		{
			args.Handled = true;

			if (InvokeRequired)
			{
				Invoke((Action<object, OnUriEnqueuedArgs>)UriQueueOnUriEnqueued, sender, args);
				return;
			}

			HandleUri(args.Uri);
		}

		private void LoadModList()
		{
			modTopButton.Enabled = modUpButton.Enabled = modDownButton.Enabled = modBottomButton.Enabled = configureModButton.Enabled = false;
			modDescription.Text = "Description: No mod selected.";
			modListView.Items.Clear();
			mods = new Dictionary<string, RSDKModInfo>();
			string modDir = Path.GetFullPath(Path.Combine(currentGame.Folder, "mods"));

			if (!Directory.Exists(modDir))
				Directory.CreateDirectory(modDir);

			foreach (string filename in RSDKModInfo.GetModFiles(new DirectoryInfo(modDir)))
			{
				mods.Add((Path.GetDirectoryName(filename) ?? string.Empty).Substring(modDir.Length + 1), IniSerializer.Deserialize<RSDKModInfo>(filename));
			}

			Dictionary<string, bool> modlist = new Dictionary<string, bool>();
			if (File.Exists(modconfigpath))
			{
				ModConfig modConfig = IniSerializer.Deserialize<ModConfig>(modconfigpath);
				if (modConfig.Mods != null && modConfig.Mods.Mods != null)
				{
					modlist = modConfig.Mods.Mods;
					isv5 = modConfig.IsV5;
				}
			}

			if (modlist.Count == 0)
				isv5 = currentGame.EXEFile.Contains("RSDKv5");

			modListView.BeginUpdate();

			foreach (var item in modlist)
			{
				if (mods.ContainsKey(item.Key))
				{
					RSDKModInfo inf = mods[item.Key];
					modListView.Items.Add(new ListViewItem(new[] { inf.Name, inf.Author, inf.Version }) { Checked = item.Value, Tag = item.Key });
				}
				else
				{
					MessageBox.Show(this, "Mod \"" + item.Key + "\" could not be found.\n\nThis mod will be removed from the list.",
						base.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}

			foreach (KeyValuePair<string, RSDKModInfo> inf in mods.OrderBy(x => x.Value.Name))
			{
				if (!modlist.ContainsKey(inf.Key))
					modListView.Items.Add(new ListViewItem(new[] { inf.Value.Name, inf.Value.Author, inf.Value.Version }) { Tag = inf.Key });
			}

			modListView.EndUpdate();
		}

		private bool CheckForUpdates(bool force = false)
		{
			if (!force && !loaderini.UpdateCheck)
			{
				return false;
			}

			if (!force && !UpdateTimeElapsed(loaderini.UpdateUnit, loaderini.UpdateFrequency, DateTime.FromFileTimeUtc(loaderini.UpdateTime)))
			{
				return false;
			}

			checkedForUpdates = true;
			loaderini.UpdateTime = DateTime.UtcNow.ToFileTimeUtc();

			if (!File.Exists("rsdkmmver.txt"))
			{
				return false;
			}

			using (var wc = new WebClient())
			{
				try
				{
					string msg = wc.DownloadString("http://mm.reimuhakurei.net/toolchangelog.php?tool=rsdkmm&rev=" + File.ReadAllText("rsdkmmver.txt"));

					if (msg.Length > 0)
					{
						using (var dlg = new UpdateMessageDialog("RSDK", msg.Replace("\n", "\r\n")))
						{
							if (dlg.ShowDialog(this) == DialogResult.Yes)
							{
								DialogResult result = DialogResult.OK;
								do
								{
									try
									{
										if (!Directory.Exists(updatePath))
										{
											Directory.CreateDirectory(updatePath);
										}
									}
									catch (Exception ex)
									{
										result = MessageBox.Show(this, "Failed to create temporary update directory:\n" + ex.Message
																	   + "\n\nWould you like to retry?", "Directory Creation Failed", MessageBoxButtons.RetryCancel);
										if (result == DialogResult.Cancel) return false;
									}
								} while (result == DialogResult.Retry);

								using (var dlg2 = new LoaderDownloadDialog("https://mm.reimuhakurei.net/misc/RSDKModManager.7z", updatePath))
									if (dlg2.ShowDialog(this) == DialogResult.OK)
									{
										Close();
										return true;
									}
							}
						}
					}
				}
				catch
				{
					MessageBox.Show(this, "Unable to retrieve update information.", "RSDK Mod Manager");
				}
			}

			return false;
		}

		private void InitializeWorker()
		{
			if (updateChecker != null)
			{
				return;
			}

			updateChecker = new BackgroundWorker { WorkerSupportsCancellation = true };
			updateChecker.DoWork += UpdateChecker_DoWork;
			updateChecker.RunWorkerCompleted += UpdateChecker_RunWorkerCompleted;
			updateChecker.RunWorkerCompleted += UpdateChecker_EnableControls;
		}

		private void UpdateChecker_EnableControls(object sender, RunWorkerCompletedEventArgs runWorkerCompletedEventArgs)
		{
			buttonCheckForUpdates.Enabled = true;
			checkForUpdatesToolStripMenuItem.Enabled = true;
			verifyToolStripMenuItem.Enabled = true;
			forceUpdateToolStripMenuItem.Enabled = true;
			uninstallToolStripMenuItem.Enabled = true;
			developerToolStripMenuItem.Enabled = true;
		}

		private void CheckForModUpdates(bool force = false)
		{
			if (!force && !loaderini.ModUpdateCheck)
			{
				return;
			}

			InitializeWorker();

			if (!force && !UpdateTimeElapsed(loaderini.UpdateUnit, loaderini.UpdateFrequency, DateTime.FromFileTimeUtc(currentGame.ModUpdateTime)))
			{
				return;
			}

			checkedForUpdates = true;
			currentGame.ModUpdateTime = DateTime.UtcNow.ToFileTimeUtc();
			updateChecker.RunWorkerAsync(mods.Select(x => new KeyValuePair<string, ModInfo>(x.Key, x.Value)).ToList());
			buttonCheckForUpdates.Enabled = false;
		}

		private void UpdateChecker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
		{
			if (modUpdater.ForceUpdate)
			{
				updateChecker?.Dispose();
				updateChecker = null;
				modUpdater.ForceUpdate = false;
				modUpdater.Clear();
			}

			if (e.Cancelled)
			{
				return;
			}

			if (!(e.Result is Tuple<List<ModDownload>, List<string>> data))
			{
				return;
			}

			List<string> errors = data.Item2;
			if (errors.Count != 0)
			{
				MessageBox.Show(this, "The following errors occurred while checking for updates:\n\n" + string.Join("\n", errors),
					"Update Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}

			bool manual = manualModUpdate;
			manualModUpdate = false;

			List<ModDownload> updates = data.Item1;
			if (updates.Count == 0)
			{
				if (manual)
				{
					MessageBox.Show(this, "Mods are up to date.",
						"No Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
				}
				return;
			}

			using (var dialog = new ModUpdatesDialog(updates))
			{
				if (dialog.ShowDialog(this) != DialogResult.OK)
				{
					return;
				}

				updates = dialog.SelectedMods;
			}

			if (updates.Count == 0)
			{
				return;
			}

			DialogResult result;

			do
			{
				try
				{
					result = DialogResult.Cancel;
					if (!Directory.Exists(updatePath))
					{
						Directory.CreateDirectory(updatePath);
					}
				}
				catch (Exception ex)
				{
					result = MessageBox.Show(this, "Failed to create temporary update directory:\n" + ex.Message
						+ "\n\nWould you like to retry?", "Directory Creation Failed", MessageBoxButtons.RetryCancel);
				}
			} while (result == DialogResult.Retry);

			using (var progress = new ModDownloadDialog(updates, updatePath))
			{
				progress.ShowDialog(this);
			}

			do
			{
				try
				{
					result = DialogResult.Cancel;
					Directory.Delete(updatePath, true);
				}
				catch (Exception ex)
				{
					result = MessageBox.Show(this, "Failed to remove temporary update directory:\n" + ex.Message
						+ "\n\nWould you like to retry? You can remove the directory manually later.",
						"Directory Deletion Failed", MessageBoxButtons.RetryCancel);
				}
			} while (result == DialogResult.Retry);

			LoadModList();
		}

		private void UpdateChecker_DoWork(object sender, DoWorkEventArgs e)
		{
			if (!(sender is BackgroundWorker worker))
			{
				throw new Exception("what");
			}

			Invoke(new Action(() =>
			{
				buttonCheckForUpdates.Enabled = false;
				checkForUpdatesToolStripMenuItem.Enabled = false;
				verifyToolStripMenuItem.Enabled = false;
				forceUpdateToolStripMenuItem.Enabled = false;
				uninstallToolStripMenuItem.Enabled = false;
				developerToolStripMenuItem.Enabled = false;
			}));

			var updatableMods = e.Argument as List<KeyValuePair<string, ModInfo>>;
			List<ModDownload> updates = null;
			List<string> errors = null;

			var tokenSource = new CancellationTokenSource();
			CancellationToken token = tokenSource.Token;

			using (var task = new Task(() => modUpdater.GetModUpdates(updatableMods, out updates, out errors, token, currentGame.Folder), token))
			{
				task.Start();

				while (!task.IsCompleted && !task.IsCanceled)
				{
					Application.DoEvents();

					if (worker.CancellationPending)
					{
						tokenSource.Cancel();
					}
				}

				task.Wait(token);
			}

			e.Result = new Tuple<List<ModDownload>, List<string>>(updates, errors);
		}

		// TODO: merge with ^
		private void UpdateChecker_DoWorkForced(object sender, DoWorkEventArgs e)
		{
			if (!(sender is BackgroundWorker worker))
			{
				throw new Exception("what");
			}

			if (!(e.Argument is List<Tuple<string, ModInfo, List<ModManifestDiff>>> updatableMods) || updatableMods.Count == 0)
			{
				return;
			}

			var updates = new List<ModDownload>();
			var errors = new List<string>();

			using (var client = new UpdaterWebClient())
			{
				foreach (Tuple<string, ModInfo, List<ModManifestDiff>> info in updatableMods)
				{
					if (worker.CancellationPending)
					{
						e.Cancel = true;
						break;
					}

					ModInfo mod = info.Item2;
					if (!string.IsNullOrEmpty(mod.GitHubRepo))
					{
						if (string.IsNullOrEmpty(mod.GitHubAsset))
						{
							errors.Add($"[{ mod.Name }] GitHubRepo specified, but GitHubAsset is missing.");
							continue;
						}

						ModDownload d = modUpdater.GetGitHubReleases(mod, info.Item1, client, errors, currentGame.Folder);
						if (d != null)
						{
							updates.Add(d);
						}
					}
					else if (!string.IsNullOrEmpty(mod.GameBananaItemType) && mod.GameBananaItemId.HasValue)
					{
						ModDownload d = modUpdater.GetGameBananaReleases(mod, info.Item1, errors, currentGame.Folder);
						if (d != null)
						{
							updates.Add(d);
						}
					}
					else if (!string.IsNullOrEmpty(mod.UpdateUrl))
					{
						List<ModManifestEntry> localManifest = info.Item3
							.Where(x => x.State == ModManifestState.Unchanged)
							.Select(x => x.Current).ToList();

						ModDownload d = modUpdater.CheckModularVersion(mod, info.Item1, localManifest, client, errors, currentGame.Folder);
						if (d != null)
						{
							updates.Add(d);
						}
					}
				}
			}

			e.Result = new Tuple<List<ModDownload>, List<string>>(updates, errors);
		}

		private void modListView_SelectedIndexChanged(object sender, EventArgs e)
		{
			int count = modListView.SelectedIndices.Count;
			if (count == 0)
			{
				modTopButton.Enabled = modUpButton.Enabled = modDownButton.Enabled = modBottomButton.Enabled = configureModButton.Enabled = false;
				modDescription.Text = "Description: No mod selected.";
			}
			else if (count == 1)
			{
				modDescription.Text = "Description: " + mods[(string)modListView.SelectedItems[0].Tag].Description;
				modTopButton.Enabled = modListView.SelectedIndices[0] != 0;
				modUpButton.Enabled = modListView.SelectedIndices[0] > 0;
				modDownButton.Enabled = modListView.SelectedIndices[0] < modListView.Items.Count - 1;
				modBottomButton.Enabled = modListView.SelectedIndices[0] != modListView.Items.Count - 1;
				configureModButton.Enabled = File.Exists(Path.Combine("mods", (string)modListView.SelectedItems[0].Tag, "configschema.xml"));
			}
			else if (count > 1)
			{
				modDescription.Text = "Description: Multiple mods selected.";
				modTopButton.Enabled = modUpButton.Enabled = modDownButton.Enabled = modBottomButton.Enabled = true;
				configureModButton.Enabled = false;
			}
		}

		private void modTopButton_Click(object sender, EventArgs e)
		{
			if (modListView.SelectedItems.Count < 1)
				return;

			modListView.BeginUpdate();

			for (int i = 0; i < modListView.SelectedItems.Count; i++)
			{
				int index = modListView.SelectedItems[i].Index;

				if (index > 0)
				{
					ListViewItem item = modListView.SelectedItems[i];
					modListView.Items.Remove(item);
					modListView.Items.Insert(i, item);
				}
			}

			modListView.SelectedItems[0].EnsureVisible();
			modListView.EndUpdate();
		}

		private void modUpButton_Click(object sender, EventArgs e)
		{
			if (modListView.SelectedItems.Count < 1)
				return;

			modListView.BeginUpdate();

			for (int i = 0; i < modListView.SelectedItems.Count; i++)
			{
				int index = modListView.SelectedItems[i].Index;

				if (index-- > 0 && !modListView.Items[index].Selected)
				{
					ListViewItem item = modListView.SelectedItems[i];
					modListView.Items.Remove(item);
					modListView.Items.Insert(index, item);
				}
			}

			modListView.SelectedItems[0].EnsureVisible();
			modListView.EndUpdate();
		}

		private void modDownButton_Click(object sender, EventArgs e)
		{
			if (modListView.SelectedItems.Count < 1)
				return;

			modListView.BeginUpdate();

			for (int i = modListView.SelectedItems.Count - 1; i >= 0; i--)
			{
				int index = modListView.SelectedItems[i].Index + 1;

				if (index != modListView.Items.Count && !modListView.Items[index].Selected)
				{
					ListViewItem item = modListView.SelectedItems[i];
					modListView.Items.Remove(item);
					modListView.Items.Insert(index, item);
				}
			}

			modListView.SelectedItems[modListView.SelectedItems.Count - 1].EnsureVisible();
			modListView.EndUpdate();
		}

		private void modBottomButton_Click(object sender, EventArgs e)
		{
			if (modListView.SelectedItems.Count < 1)
				return;

			modListView.BeginUpdate();

			for (int i = modListView.SelectedItems.Count - 1; i >= 0; i--)
			{
				int index = modListView.SelectedItems[i].Index;

				if (index != modListView.Items.Count - 1)
				{
					ListViewItem item = modListView.SelectedItems[i];
					modListView.Items.Remove(item);
					modListView.Items.Insert(modListView.Items.Count, item);
				}
			}

			modListView.SelectedItems[modListView.SelectedItems.Count - 1].EnsureVisible();
			modListView.EndUpdate();
		}

		static readonly string moddropname = "Mod" + Process.GetCurrentProcess().Id;
		private void modListView_ItemDrag(object sender, ItemDragEventArgs e)
		{
			modListView.DoDragDrop(new DataObject(moddropname, modListView.SelectedItems.Cast<ListViewItem>().ToArray()), DragDropEffects.Move | DragDropEffects.Scroll);
		}

		private void modListView_DragEnter(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(moddropname))
				e.Effect = DragDropEffects.Move | DragDropEffects.Scroll;
			else
				e.Effect = DragDropEffects.None;
		}

		private void modListView_DragOver(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(moddropname))
				e.Effect = DragDropEffects.Move | DragDropEffects.Scroll;
			else
				e.Effect = DragDropEffects.None;
		}

		private void modListView_DragDrop(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(moddropname))
			{
				Point clientPoint = modListView.PointToClient(new Point(e.X, e.Y));
				ListViewItem[] items = (ListViewItem[])e.Data.GetData(moddropname);
				int ind = modListView.GetItemAt(clientPoint.X, clientPoint.Y).Index;
				foreach (ListViewItem item in items)
					if (ind > item.Index)
						ind++;
				modListView.BeginUpdate();
				foreach (ListViewItem item in items)
					modListView.Items.Insert(ind++, (ListViewItem)item.Clone());
				foreach (ListViewItem item in items)
					modListView.Items.Remove(item);
				modListView.EndUpdate();
			}
		}

		private void Save()
		{
			ModConfig modConfig = new ModConfig() { IsV5 = isv5 };

			foreach (ListViewItem item in modListView.Items)
			{
				modConfig.Mods.Mods.Add((string)item.Tag, item.Checked);
			}

			IniSerializer.Serialize(modConfig, modconfigpath);

			loaderini.UpdateCheck = checkUpdateStartup.Checked;
			loaderini.ModUpdateCheck = checkUpdateModsStartup.Checked;
			loaderini.UpdateUnit = (UpdateUnit)comboUpdateFrequency.SelectedIndex;
			loaderini.UpdateFrequency = (int)numericUpdateFrequency.Value;
			loaderini.AutoClose = autoCloseCheckBox.Checked;

			IniSerializer.Serialize(loaderini, loaderinipath);
		}

		private void saveAndPlayButton_Click(object sender, EventArgs e)
		{
			if (updateChecker?.IsBusy == true)
			{
				var result = MessageBox.Show(this, "Mods are still being checked for updates. Continue anyway?",
					"Busy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

				if (result == DialogResult.No)
				{
					return;
				}

				Enabled = false;

				updateChecker.CancelAsync();
				while (updateChecker.IsBusy)
				{
					Application.DoEvents();
				}

				Enabled = true;
			}

			Save();
			Process process = Process.Start(new ProcessStartInfo(Path.GetFullPath(Path.Combine(currentGame.Folder, currentGame.EXEFile))) { WorkingDirectory = Path.GetFullPath(currentGame.Folder) });
			try { process?.WaitForInputIdle(10000); }
			catch { }
			if (autoCloseCheckBox.Checked)
				Close();
		}

		private void saveButton_Click(object sender, EventArgs e)
		{
			Save();
			LoadModList();
		}

		private void buttonRefreshModList_Click(object sender, EventArgs e)
		{
			LoadModList();
		}

		private void configureModButton_Click(object sender, EventArgs e)
		{
			using (ModConfigDialog dlg = new ModConfigDialog(Path.Combine(currentGame.Folder, "mods", (string)modListView.SelectedItems[0].Tag), modListView.SelectedItems[0].Text))
				dlg.ShowDialog(this);
		}

		private void buttonNewMod_Click(object sender, EventArgs e)
		{
			using (var ModDialog = new NewModDialog())
			{
				if (ModDialog.ShowDialog() == DialogResult.OK)
					LoadModList();
			}
		}

		private void modListView_MouseClick(object sender, MouseEventArgs e)
		{
			if (e.Button != MouseButtons.Right)
			{
				return;
			}

			if (modListView.FocusedItem.Bounds.Contains(e.Location))
			{
				modContextMenu.Show(Cursor.Position);
			}
		}

		private void openFolderToolStripMenuItem_Click(object sender, EventArgs e)
		{
			foreach (ListViewItem item in modListView.SelectedItems)
			{
				Process.Start(Path.Combine(currentGame.Folder, "mods", (string)item.Tag));
			}
		}

		private void uninstallToolStripMenuItem_Click(object sender, EventArgs e)
		{
			DialogResult result = MessageBox.Show(this, "This will uninstall all selected mods."
				+ "\n\nAre you sure you wish to continue?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

			if (result != DialogResult.Yes)
			{
				return;
			}

			result = MessageBox.Show(this, "Would you like to keep mod user data where possible? (Save files, config files, etc)",
				"User Data", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

			if (result == DialogResult.Cancel)
			{
				return;
			}

			foreach (ListViewItem item in modListView.SelectedItems)
			{
				var dir = (string)item.Tag;
				var modDir = Path.Combine(currentGame.Folder, "mods", dir);
				var manpath = Path.Combine(modDir, "mod.manifest");

				try
				{
					if (result == DialogResult.Yes && File.Exists(manpath))
					{
						List<ModManifestEntry> manifest = ModManifest.FromFile(manpath);
						foreach (var entry in manifest)
						{
							var path = Path.Combine(modDir, entry.FilePath);
							if (File.Exists(path))
							{
								File.Delete(path);
							}
						}

						File.Delete(manpath);
						var version = Path.Combine(modDir, "mod.version");
						if (File.Exists(version))
						{
							File.Delete(version);
						}
					}
					else
					{
						if (result == DialogResult.Yes)
						{
							var retain = MessageBox.Show(this, $"The mod \"{ mods[dir].Name }\" (\"mods\\{ dir }\") does not have a manifest, so mod user data cannot be retained."
								+ " Do you want to uninstall it anyway?", "Cannot Retain User Data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

							if (retain == DialogResult.No)
							{
								continue;
							}
						}

						Directory.Delete(modDir, true);
					}
				}
				catch (Exception ex)
				{
					MessageBox.Show(this, $"Failed to uninstall mod \"{ mods[dir].Name }\" from \"{ dir }\": { ex.Message }", "Failed",
						MessageBoxButtons.OK, MessageBoxIcon.Error);
				}
			}

			LoadModList();
		}

		private bool displayedManifestWarning;

		private void generateManifestToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!displayedManifestWarning)
			{
				DialogResult result = MessageBox.Show(this, Properties.Resources.GenerateManifestWarning,
					"Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

				if (result != DialogResult.Yes)
				{
					return;
				}

				displayedManifestWarning = true;
			}

			foreach (ListViewItem item in modListView.SelectedItems)
			{
				var modPath = Path.Combine(currentGame.Folder, "mods", (string)item.Tag);
				var manifestPath = Path.Combine(modPath, "mod.manifest");

				List<ModManifestEntry> manifest;
				List<ModManifestDiff> diff;

				using (var progress = new ManifestDialog(modPath, $"Generating manifest: {(string)item.Tag}", true))
				{
					progress.SetTask("Generating file index...");
					if (progress.ShowDialog(this) == DialogResult.Cancel)
					{
						continue;
					}

					diff = progress.Diff;
				}

				if (diff == null)
				{
					continue;
				}

				if (diff.Count(x => x.State != ModManifestState.Unchanged) <= 0)
				{
					continue;
				}

				using (var dialog = new ManifestDiffDialog(diff))
				{
					if (dialog.ShowDialog(this) == DialogResult.Cancel)
					{
						continue;
					}

					manifest = dialog.MakeNewManifest();
				}

				ModManifest.ToFile(manifest, manifestPath);
			}
		}

		private void UpdateSelectedMods()
		{
			InitializeWorker();
			manualModUpdate = true;
			updateChecker?.RunWorkerAsync(modListView.SelectedItems.Cast<ListViewItem>()
				.Select(x => (string)x.Tag)
				.Select(x => new KeyValuePair<string, ModInfo>(x, mods[x]))
				.ToList());
		}

		private void checkForUpdatesToolStripMenuItem_Click(object sender, EventArgs e)
		{
			UpdateSelectedMods();
		}

		private void forceUpdateToolStripMenuItem_Click(object sender, EventArgs e)
		{
			var result = MessageBox.Show(this, "This will force all selected mods to be completely re-downloaded."
				+ " Are you sure you want to continue?",
				"Force Update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

			if (result != DialogResult.Yes)
			{
				return;
			}

			modUpdater.ForceUpdate = true;
			UpdateSelectedMods();
		}

		private void verifyToolStripMenuItem_Click(object sender, EventArgs e)
		{
			List<Tuple<string, ModInfo>> items = modListView.SelectedItems.Cast<ListViewItem>()
				.Select(x => (string)x.Tag)
				.Where(x => File.Exists(Path.Combine(currentGame.Folder, "mods", x, "mod.manifest")))
				.Select(x => new Tuple<string, ModInfo>(x, mods[x]))
				.ToList();

			if (items.Count < 1)
			{
				MessageBox.Show(this, "None of the selected mods have manifests, so they cannot be verified.",
					"Missing mod.manifest", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			using (var progress = new VerifyModDialog(items))
			{
				var result = progress.ShowDialog(this);
				if (result == DialogResult.Cancel)
				{
					return;
				}

				List<Tuple<string, ModInfo, List<ModManifestDiff>>> failed = progress.Failed;
				if (failed.Count < 1)
				{
					MessageBox.Show(this, "All selected mods passed verification.", "Integrity Pass",
						MessageBoxButtons.OK, MessageBoxIcon.Information);
				}
				else
				{
					result = MessageBox.Show(this, "The following mods failed verification:\n"
						+ string.Join("\n", failed.Select(x => $"{x.Item2.Name}: {x.Item3.Count(y => y.State != ModManifestState.Unchanged)} file(s)"))
						+ "\n\nWould you like to attempt repairs?",
						"Integrity Fail", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

					if (result != DialogResult.Yes)
					{
						return;
					}

					InitializeWorker();

					updateChecker.DoWork -= UpdateChecker_DoWork;
					updateChecker.DoWork += UpdateChecker_DoWorkForced;

					updateChecker.RunWorkerAsync(failed);

					modUpdater.ForceUpdate = true;
					buttonCheckForUpdates.Enabled = false;
				}
			}
		}

		private void comboUpdateFrequency_SelectedIndexChanged(object sender, EventArgs e)
		{
			numericUpdateFrequency.Enabled = comboUpdateFrequency.SelectedIndex > 0;
		}

		private void buttonCheckForUpdates_Click(object sender, EventArgs e)
		{
			buttonCheckForUpdates.Enabled = false;

			if (CheckForUpdates(true))
			{
				return;
			}

			manualModUpdate = true;
			CheckForModUpdates(true);
		}

		private void installURLHandlerButton_Click(object sender, EventArgs e)
		{
			Process.Start(new ProcessStartInfo(Application.ExecutablePath, "urlhandler " + GameInfo.GetProtocol(currentGame.Game)) { UseShellExecute = true, Verb = "runas" }).WaitForExit();
			MessageBox.Show(this, "URL handler installed!", Text);
		}

		private static Game GetDefaultGame(string exename)
		{
			Game tmpgame = 0;
			if (exename.StartsWith("rsdkv"))
				switch (exename[5])
				{
					case '3':
						tmpgame = Game.SonicCD;
						break;
					case '4':
						tmpgame = Game.Sonic1;
						break;
					case '5':
						tmpgame = Game.SonicMania;
						break;
				}
			else if (exename.StartsWith("restored"))
				tmpgame = Game.SonicCD;
			else if (exename.StartsWith("Nexus"))
				tmpgame = Game.SonicNexus;
			else if (exename.StartsWith("sonicforever"))
				tmpgame = Game.Sonic1Forever;
			else if (exename.StartsWith("sonic2absolute"))
				tmpgame = Game.Sonic2Absolute;
			return tmpgame;
		}

		private void gameSelector_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (gameSelector.SelectedIndex == -1) return;
			if (gameSelector.SelectedIndex >= loaderini.Games.Count && AddGame())
			{
				gameSelector.Items.Insert(gameSelector.Items.Count - 1, loaderini.Games.Last().Name);
				gameSelector.SelectedIndex = gameSelector.Items.Count - 2;
				removeGameButton.Enabled = loaderini.Games.Count > 1;
				return;
			}
			loaderini.LastGame = gameSelector.SelectedIndex;
			currentGame = loaderini.Games[gameSelector.SelectedIndex];
			modconfigpath = Path.Combine(currentGame.Folder, _modconfigpath);
			LoadModList();
			CheckForModUpdates();
			if (checkedForUpdates)
				IniSerializer.Serialize(loaderini, loaderinipath);
		}

		private void removeGameButton_Click(object sender, EventArgs e)
		{
			loaderini.Games.RemoveAt(gameSelector.SelectedIndex);
			gameSelector.Items.RemoveAt(gameSelector.SelectedIndex);
			gameSelector.SelectedIndex = 0;
		}
	}
}
