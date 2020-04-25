﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using GW2Scratch.ArcdpsLogManager.Analytics;
using GW2Scratch.ArcdpsLogManager.Commands;
using GW2Scratch.ArcdpsLogManager.Controls;
using GW2Scratch.ArcdpsLogManager.Dialogs;
using GW2Scratch.ArcdpsLogManager.Gw2Api;
using GW2Scratch.ArcdpsLogManager.Logs;
using GW2Scratch.ArcdpsLogManager.Logs.Filters;
using GW2Scratch.ArcdpsLogManager.Logs.Naming;
using GW2Scratch.ArcdpsLogManager.Processing;
using GW2Scratch.ArcdpsLogManager.Sections;
using GW2Scratch.ArcdpsLogManager.Timing;
using GW2Scratch.ArcdpsLogManager.Updates;
using GW2Scratch.ArcdpsLogManager.Uploads;
using GW2Scratch.EVTCAnalytics;
using GW2Scratch.EVTCAnalytics.GameData;
using GW2Scratch.EVTCAnalytics.Processing;
using Gw2Sharp;

namespace GW2Scratch.ArcdpsLogManager
{
	public sealed class ManagerForm : Form
	{
		private readonly Cooldown gridRefreshCooldown = new Cooldown(TimeSpan.FromSeconds(2));

		private ImageProvider ImageProvider { get; } = new ImageProvider();
		private LogFinder LogFinder { get; } = new LogFinder();

		private LogAnalytics LogAnalytics { get; } =
			new LogAnalytics(new EVTCParser(), new LogProcessor(), log => new LogAnalyzer(log, null));

		private ApiProcessor ApiProcessor { get; }
		private UploadProcessor UploadProcessor { get; }
		private LogDataProcessor LogDataProcessor { get; }
		private ILogNameProvider LogNameProvider { get; }
		private LogDataUpdater LogDataUpdater { get; } = new LogDataUpdater();

		private ObservableCollection<LogData> logs = new ObservableCollection<LogData>();
		private FilterCollection<LogData> logsFiltered;

		private CancellationTokenSource logLoadTaskTokenSource = null;

		public IEnumerable<LogData> LoadedLogs => logs;
		public LogCache LogCache { get; }
		public ApiData ApiData { get; }
		private LogFilters Filters { get; }

		private readonly List<FileSystemWatcher> fileSystemWatchers = new List<FileSystemWatcher>();

		public ManagerForm(LogCache logCache, ApiData apiData)
		{
			LogCache = logCache ?? throw new ArgumentNullException(nameof(logCache));
			ApiData = apiData ?? throw new ArgumentNullException(nameof(apiData));

			// Background processors
			var dpsReportUploader = new DpsReportUploader();
			UploadProcessor = new UploadProcessor(dpsReportUploader, LogCache);
			ApiProcessor = new ApiProcessor(ApiData, new Gw2Client());
			LogDataProcessor = new LogDataProcessor(LogCache, ApiProcessor, LogAnalytics);
			LogNameProvider = new TranslatedLogNameProvider(GameLanguage.English);

			Filters = new LogFilters(LogNameProvider, new SettingsFilters());

			if (Settings.UseGW2Api)
			{
				ApiProcessor.StartBackgroundTask();
			}

			Settings.UseGW2ApiChanged += (sender, args) =>
			{
				if (Settings.UseGW2Api)
				{
					ApiProcessor.StartBackgroundTask();
				}
				else
				{
					ApiProcessor.StopBackgroundTask();
				}
			};

			Settings.DpsReportDomainChanged += (sender, args) => { dpsReportUploader.Domain = Settings.DpsReportDomain; };

			// Form layout
			Icon = Resources.GetProgramIcon();
			Title = "arcdps Log Manager";
			ClientSize = new Size(1024, 768);
			var formLayout = new DynamicLayout();
			Content = formLayout;

			Menu = ConstructMenuBar();

			var rightSide = new DynamicLayout();
			rightSide.BeginVertical(new Padding(5, 0), new Size(0, 8), yscale: true);
			{
				rightSide.Add(ConstructLogFilters());
				rightSide.Add(ConstructMainTabControl(), true);
			}
			rightSide.EndVertical();

			formLayout.BeginVertical(new Padding(5), yscale: false);
			{
				formLayout.Add(new Splitter
				{
					Orientation = Orientation.Horizontal,
					Panel1 = ConstructEncounterFilterTree(),
					Panel2 = rightSide,
					Position = 200
				}, yscale: true);
				formLayout.Add(ConstructStatusPanel());
			}
			formLayout.EndVertical();

			// Event handlers
			ApiProcessor.Processed += (sender, args) =>
			{
				bool last = args.CurrentScheduledItems == 0;

				if (last)
				{
					ApiData.SaveDataToFile();
				}
			};

			Settings.LogRootPathChanged += (sender, args) => Application.Instance.Invoke(ReloadLogs);

			Shown += (sender, args) => ReloadLogs();
			Closing += (sender, args) =>
			{
				if (LogCache?.ChangedSinceLastSave ?? false)
				{
					LogCache?.SaveToFile();
				}

				ApiData?.SaveDataToFile();
			};
			LogCollectionsRecreated += (sender, args) =>
			{
				logsFiltered.Filter = Filters.FilterLog;
				logsFiltered.Refresh();
			};
			LogSearchFinished += (sender, args) =>
			{
				var updates = LogDataUpdater.GetUpdates(logs).ToList();
				if (updates.Count > 0)
				{
					new UpdateDialog(LogDataProcessor, updates).ShowModal(this);
				}
			};

			// Collection initialization
			RecreateLogCollections(new ObservableCollection<LogData>(logs));
		}

		private DynamicLayout ConstructStatusPanel()
		{
			// Processing label
			var processingLabel = new Label();

			void UpdatingProcessingLabel(object sender, BackgroundProcessorEventArgs args)
			{
				Application.Instance.AsyncInvoke(() =>
				{
					bool finished = args.CurrentScheduledItems == 0;
					int logCount = logs.Count;
					// The log count check is to prevent a log count of 0 being shown when logs are being loaded and
					// log processing catches up with searching for logs.
					processingLabel.Text = finished && logCount > 0
						? $"{logCount} logs found."
						: $"Parsing logs {args.TotalProcessedItems}/{args.TotalScheduledItems}...";
				});
			}

			LogDataProcessor.Processed += UpdatingProcessingLabel;
			LogDataProcessor.Scheduled += UpdatingProcessingLabel;
			LogDataProcessor.Unscheduled += UpdatingProcessingLabel;
			LogCollectionsRecreated += (sender, args) => processingLabel.Text = $"{logs.Count} logs found.";
			LogSearchStarted += (sender, args) => processingLabel.Text = "Finding logs...";

			// Upload state label
			var uploadLabel = new Label();

			void UpdateUploadLabel(object sender, BackgroundProcessorEventArgs args)
			{
				Application.Instance.AsyncInvoke(() =>
				{
					bool finished = args.CurrentScheduledItems == 0;
					uploadLabel.Text = finished
						? ""
						: $"Uploading {args.TotalProcessedItems}/{args.TotalScheduledItems}...";
				});
			}

			UploadProcessor.Processed += UpdateUploadLabel;
			UploadProcessor.Scheduled += UpdateUploadLabel;
			UploadProcessor.Unscheduled += UpdateUploadLabel;

			// API state label
			var apiLabel = new Label();

			void UpdateApiLabel(object sender, BackgroundProcessorEventArgs args)
			{
				Application.Instance.AsyncInvoke(() =>
				{
					bool finished = args.CurrentScheduledItems == 0;
					apiLabel.Text = finished ? "" : $"GW2 API {args.TotalProcessedItems}/{args.TotalScheduledItems}";
				});
			}

			ApiProcessor.Processed += UpdateApiLabel;
			ApiProcessor.Scheduled += UpdateApiLabel;
			ApiProcessor.Unscheduled += UpdateApiLabel;

			// Layout of the status bar
			var layout = new DynamicLayout();
			layout.BeginHorizontal();
			{
				layout.Add(processingLabel, xscale: true);
				layout.Add(uploadLabel, xscale: true);
				layout.Add(apiLabel, xscale: true);
			}
			layout.EndHorizontal();

			return layout;
		}

		private LogEncounterFilterTree ConstructEncounterFilterTree()
		{
			var tree = new LogEncounterFilterTree(ImageProvider, Filters);
			tree.FiltersUpdated += (sender, args) => logsFiltered.Refresh();
			LogCollectionsRecreated += (sender, args) =>
			{
				tree.UpdateLogs(logs);
				logs.CollectionChanged += (s, a) => { tree.UpdateLogs(logs); };
			};
			LogDataProcessor.Processed += (sender, args) => { Application.Instance.AsyncInvoke(() => tree.UpdateLogs(logs)); };

			return tree;
		}

		private LogFilterPanel ConstructLogFilters()
		{
			var filterPanel = new LogFilterPanel(ImageProvider, Filters);
			filterPanel.FiltersUpdated += (sender, args) => logsFiltered.Refresh();

			return filterPanel;
		}

		private MenuBar ConstructMenuBar()
		{
			var updateMenuItem = new ButtonMenuItem {Text = "&Update"};
			updateMenuItem.Click += (sender, args) => { new UpdateDialog(LogDataProcessor, LogDataUpdater.GetUpdates(logs).ToList()).ShowModal(this); };

			var logCacheMenuItem = new ButtonMenuItem {Text = "Log &cache"};
			logCacheMenuItem.Click += (sender, args) => { new CacheDialog(this).ShowModal(this); };

			var apiDataMenuItem = new ButtonMenuItem {Text = "&API data"};
			apiDataMenuItem.Click += (sender, args) => { new ApiDialog(ApiProcessor).ShowModal(this); };

			var settingsFormMenuItem = new ButtonMenuItem {Text = "&Settings"};
			settingsFormMenuItem.Click += (sender, args) => { new SettingsForm().Show(); };

			var debugDataMenuItem = new CheckMenuItem {Text = "Show &debug data"};
			debugDataMenuItem.Checked = Settings.ShowDebugData;
			debugDataMenuItem.CheckedChanged += (sender, args) => { Settings.ShowDebugData = debugDataMenuItem.Checked; };

			var showGuildTagsMenuItem = new CheckMenuItem {Text = "Show &guild tags in log details"};
			showGuildTagsMenuItem.Checked = Settings.ShowGuildTagsInLogDetail;
			showGuildTagsMenuItem.CheckedChanged += (sender, args) => { Settings.ShowGuildTagsInLogDetail = showGuildTagsMenuItem.Checked; };

			// TODO: Implement
			/*
			var arcdpsSettingsMenuItem = new ButtonMenuItem {Text = "&arcdps settings", Enabled = false};

			var arcdpsMenuItem = new ButtonMenuItem {Text = "&arcdps", Enabled = false};
			arcdpsMenuItem.Items.Add(arcdpsSettingsMenuItem);
			*/

			var dataMenuItem = new ButtonMenuItem {Text = "&Data"};
			dataMenuItem.Items.Add(updateMenuItem);
			dataMenuItem.Items.Add(logCacheMenuItem);
			dataMenuItem.Items.Add(apiDataMenuItem);

			var settingsMenuItem = new ButtonMenuItem {Text = "&Settings"};
			settingsMenuItem.Items.Add(settingsFormMenuItem);
			settingsMenuItem.Items.Add(showGuildTagsMenuItem);
			settingsMenuItem.Items.Add(new SeparatorMenuItem());
			settingsMenuItem.Items.Add(debugDataMenuItem);

			var helpMenuItem = new ButtonMenuItem {Text = "Help"};
			helpMenuItem.Items.Add(new About());

			return new MenuBar(dataMenuItem, settingsMenuItem, helpMenuItem);
		}

		private TabControl ConstructMainTabControl()
		{
			// Main log list
			var logList = new LogList(ApiData, LogDataProcessor, UploadProcessor, ImageProvider, LogNameProvider);
			LogCollectionsRecreated += (sender, args) => logList.DataStore = logsFiltered;

			LogDataProcessor.Processed += (sender, args) =>
			{
				bool last = args.CurrentScheduledItems == 0;

				if (last || gridRefreshCooldown.TryUse(DateTime.Now))
				{
					Application.Instance.AsyncInvoke(() => { logList.ReloadData(); });
				}
			};
			// Player list
			var playerList = new PlayerList(ApiData, LogDataProcessor, UploadProcessor, ImageProvider, LogNameProvider);
			FilteredLogsUpdated += (sender, args) => playerList.UpdateDataFromLogs(logsFiltered);

			// Guild list
			var guildList = new GuildList(ApiData, LogDataProcessor, UploadProcessor, ImageProvider, LogNameProvider);
			FilteredLogsUpdated += (sender, args) => guildList.UpdateDataFromLogs(logsFiltered);

			// Statistics
			var statistics = new StatisticsSection(ImageProvider, LogNameProvider);
			FilteredLogsUpdated += (sender, args) => statistics.UpdateDataFromLogs(logsFiltered.ToList());

			// Game data collecting
			var gameDataCollecting = new GameDataCollecting(logList);
			var gameDataPage = new TabPage
			{
				Text = "Game data", Content = gameDataCollecting, Visible = Settings.ShowDebugData
			};
			Settings.ShowDebugDataChanged += (sender, args) => gameDataPage.Visible = Settings.ShowDebugData;

			// Service status
			var serviceStatus = new DynamicLayout {Spacing = new Size(10, 10), Padding = new Padding(5)};
			serviceStatus.BeginHorizontal();
			{
				serviceStatus.Add(new GroupBox
				{
					Text = "Uploads",
					Content = new BackgroundProcessorDetail {BackgroundProcessor = UploadProcessor}
				}, xscale: true);
				serviceStatus.Add(new GroupBox
				{
					Text = "Guild Wars 2 API",
					Content = new BackgroundProcessorDetail {BackgroundProcessor = ApiProcessor}
				}, xscale: true);
				serviceStatus.Add(new GroupBox
				{
					Text = "Log parsing",
					Content = new BackgroundProcessorDetail {BackgroundProcessor = LogDataProcessor}
				}, xscale: true);
			}
			serviceStatus.AddRow(null);
			var servicePage = new TabPage
			{
				Text = "Services", Content = serviceStatus, Visible = Settings.ShowDebugData
			};
			Settings.ShowDebugDataChanged += (sender, args) => servicePage.Visible = Settings.ShowDebugData;

			var tabs = new TabControl();
			tabs.Pages.Add(new TabPage {Text = "Logs", Content = logList});
			tabs.Pages.Add(new TabPage {Text = "Players", Content = playerList});
			tabs.Pages.Add(new TabPage {Text = "Guilds", Content = guildList});
			tabs.Pages.Add(new TabPage {Text = "Statistics", Content = statistics});
			tabs.Pages.Add(gameDataPage);
			tabs.Pages.Add(servicePage);

			// This is needed to avoid a Gtk platform issue where the tab is changed to the last one.
			Shown += (sender, args) => tabs.SelectedIndex = 0;

			return tabs;
		}

		public void ReloadLogs()
		{
			logLoadTaskTokenSource?.Cancel();
			logLoadTaskTokenSource = new CancellationTokenSource();

			logs.Clear();
			LogSearchStarted?.Invoke(this, EventArgs.Empty);
			Task.Run(() => FindLogs(logLoadTaskTokenSource.Token))
				.ContinueWith(t => Application.Instance.Invoke(() => LogSearchFinished?.Invoke(null, EventArgs.Empty)));

			SetupFileSystemWatchers();
		}


		private void SetupFileSystemWatchers()
		{
			foreach (var watcher in fileSystemWatchers)
			{
				watcher.Dispose();
			}

			fileSystemWatchers.Clear();
			foreach (var directory in Settings.LogRootPaths)
			{
				try
				{
					var watcher = new FileSystemWatcher(directory);
					watcher.IncludeSubdirectories = true;
					watcher.Filter = "*evtc"; // Matches both .evtc and .zevtc files.
					watcher.Created += (sender, args) =>
						Application.Instance.AsyncInvoke(() => AddNewLog(args.FullPath));
					watcher.Renamed += (sender, args) =>
						Application.Instance.AsyncInvoke(() => AddNewLog(args.FullPath));
					watcher.EnableRaisingEvents = true;

					fileSystemWatchers.Add(watcher);
				}
				catch (Exception e)
				{
					// TODO: Replace with proper logging
					Console.Error.WriteLine($"Failed to set up FileSystemWatcher: {e.Message}");
				}
			}
		}

		private void AddNewLog(string fullName)
		{
			if (logs.Any(x => x.FileInfo.FullName == fullName))
			{
				return;
			}

			if (!LogCache.TryGetLogData(fullName, out var log))
			{
				log = new LogData(new FileInfo(fullName));
			}

			if (log.ParsingStatus != ParsingStatus.Parsed)
			{
				LogDataProcessor.Schedule(log);
			}

			logs.Add(log);
		}

		/// <summary>
		/// Discover logs and process them.
		/// </summary>
		private void FindLogs(CancellationToken cancellationToken)
		{
			LogDataProcessor.UnscheduleAll();

			// TODO: Fix the counters being off if a log is currently being processed
			LogDataProcessor.ResetTotalCounters();
			try
			{
				var newLogs = new ObservableCollection<LogData>(logs);

				//foreach (var log in LogFinder.GetTesting())
				foreach (var log in Settings.LogRootPaths.SelectMany(x => LogFinder.GetFromDirectory(x, LogCache)))
				{
					newLogs.Add(log);

					if (log.ParsingStatus == ParsingStatus.Parsed)
					{
						ApiProcessor.RegisterLog(log);
					}
					else
					{
						LogDataProcessor.Schedule(log);
					}

					cancellationToken.ThrowIfCancellationRequested();
				}

				Application.Instance.Invoke(() => { RecreateLogCollections(newLogs); });
			}
			catch (Exception e) when (!(e is OperationCanceledException))
			{
				Application.Instance.Invoke(() =>
				{
					MessageBox.Show(this, $"Logs could not be found.\nReason: {e.Message}", "Log Discovery Error",
						MessageBoxType.Error);
				});
			}

			if (LogCache.ChangedSinceLastSave)
			{
				LogCache.SaveToFile();
			}
		}

		/// <summary>
		/// Recreate log collections, this is significantly faster than updating the old one.
		/// </summary>
		private void RecreateLogCollections(ObservableCollection<LogData> newLogCollection)
		{
			logs = newLogCollection;
			logsFiltered = new FilterCollection<LogData>(logs);
			logsFiltered.CollectionChanged += (sender, args) => FilteredLogsUpdated?.Invoke(this, EventArgs.Empty);
			LogCollectionsRecreated?.Invoke(this, EventArgs.Empty);
		}

		private event EventHandler LogSearchStarted;
		private event EventHandler LogSearchFinished;
		private event EventHandler LogCollectionsRecreated;
		private event EventHandler FilteredLogsUpdated;
	}
}