using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using GW2Scratch.ArcdpsLogManager.Logs;
using GW2Scratch.ArcdpsLogManager.Processing;
using Button = Eto.Forms.Button;
using Label = Eto.Forms.Label;

namespace GW2Scratch.ArcdpsLogManager.Controls
{
	public sealed class MultipleLogPanel : DynamicLayout
	{
		private LogData[] logData;

		private readonly Label countLabel = new Label {Font = Fonts.Sans(12)};
		private readonly Label dpsReportNotUploadedLabel = new Label();
		private readonly Label dpsReportUploadingLabel = new Label();
		private readonly Label dpsReportUploadedLabel = new Label();
		private readonly Label dpsReportProcessingFailedLabel = new Label();
		private readonly Label dpsReportUploadFailedLabel = new Label();
		private readonly TextArea dpsReportLinkTextArea = new TextArea {ReadOnly = true};
		private readonly Button dpsReportUploadButton = new Button();
		private readonly Button dpsReportCancelButton = new Button {Text = "Cancel"};
		private readonly Button dpsReportOpenButton = new Button {Text = "Open uploaded logs in a browser", Enabled = false};
		private readonly ProgressBar dpsReportUploadProgressBar = new ProgressBar();
		private readonly DynamicTable dpsReportUploadFailedRow;
		private readonly DynamicTable dpsReportProcessingFailedRow;

		public IEnumerable<LogData> LogData
		{
			get => logData;
			set
			{
				SuspendLayout();
				logData = value?.ToArray();

				if (logData == null || logData.Length < 2)
				{
					Visible = false;
					return;
				}

				Visible = true;

				countLabel.Text = $"{logData.Length} logs selected";

				UpdateDpsReportUploadStatus();

				ResumeLayout();
			}
		}

		private void UpdateDpsReportUploadStatus()
		{
			if (logData == null) return;

			int notUploaded = logData.Count(x => x.DpsReportEIUpload.UploadState == UploadState.NotUploaded);
			int queued = logData.Count(x => x.DpsReportEIUpload.UploadState == UploadState.Queued);
			int uploading = logData.Count(x => x.DpsReportEIUpload.UploadState == UploadState.Uploading);
			int uploaded = logData.Count(x => x.DpsReportEIUpload.UploadState == UploadState.Uploaded);
			int uploadsFailed = logData.Count(x => x.DpsReportEIUpload.UploadState == UploadState.UploadError);
			int processingFailed = logData.Count(x => x.DpsReportEIUpload.UploadState == UploadState.ProcessingError);

			int finished = uploaded + uploadsFailed + processingFailed;
			int totalRequested = queued + uploading + uploaded + uploadsFailed + processingFailed;
			dpsReportUploadProgressBar.MaxValue = totalRequested > 0 ? totalRequested : 1;
			dpsReportUploadProgressBar.Value = finished;
			dpsReportUploadButton.Enabled = notUploaded + uploadsFailed > 0;
			dpsReportCancelButton.Enabled = queued > 0;
			dpsReportUploadButton.Text = $"Upload missing logs ({notUploaded + uploadsFailed})";
			dpsReportNotUploadedLabel.Text = notUploaded.ToString();
			dpsReportUploadingLabel.Text = (uploading + queued).ToString();
			dpsReportUploadedLabel.Text = uploaded.ToString();
			dpsReportUploadFailedLabel.Text = uploadsFailed.ToString();
			dpsReportUploadFailedRow.Table.Visible = uploadsFailed > 0;
			dpsReportProcessingFailedLabel.Text = processingFailed.ToString();
			dpsReportProcessingFailedRow.Table.Visible = processingFailed > 0;
			dpsReportOpenButton.Enabled = uploaded > 0;
			dpsReportLinkTextArea.Text = string.Join(Environment.NewLine,
				logData.Where(x => x.DpsReportEIUpload.Url != null).Select(x => x.DpsReportEIUpload.Url));
		}

		public MultipleLogPanel(LogDataProcessor logProcessor, UploadProcessor uploadProcessor)
		{
			Padding = new Padding(10);
			Width = 350;
			Visible = false;

			var reparseButton = new Button {Text = "Reparse all"};
			reparseButton.Click += (sender, args) =>
			{
				foreach (var log in logData)
				{
					logProcessor.Schedule(log);
				}
			};

			dpsReportUploadButton.Click += (sender, args) =>
			{
				foreach (var log in logData)
				{
					var state = log.DpsReportEIUpload.UploadState;
					if (state == UploadState.NotUploaded || state == UploadState.UploadError)
					{
						uploadProcessor.ScheduleDpsReportEIUpload(log);
					}
				}
			};

			dpsReportOpenButton.Click += (sender, args) =>
			{
				var uploadedLogs = logData.Where(x => x.DpsReportEIUpload.UploadState == UploadState.Uploaded).ToList();
				if (uploadedLogs.Count > 5)
				{
					var result = MessageBox.Show(
						$"Are you sure you want to open {uploadedLogs.Count} logs at once in a browser?",
						"Open uploaded logs in a browser",
						MessageBoxButtons.YesNo,
						MessageBoxType.Question);

					if (result != DialogResult.Yes)
					{
						return;
					}
				}

				foreach (var log in logData)
				{
					var state = log.DpsReportEIUpload.UploadState;
					if (state == UploadState.Uploaded)
					{
						Process.Start(log.DpsReportEIUpload.Url);
					}
				}
			};

			dpsReportCancelButton.Click += (sender, args) => { uploadProcessor.CancelDpsReportEIUpload(logData); };

			DynamicTable debugSection;

			BeginVertical(spacing: new Size(0, 30));
			{
				BeginVertical();
				{
					//Add(new Label {Text = "Batch log management", Font = Fonts.Sans(16, FontStyle.Bold)});
					Add(countLabel);
				}
				EndVertical();
				debugSection = BeginVertical();
				{
					Add(reparseButton);
				}
				EndVertical();

				BeginVertical(spacing: new Size(0, 5));
				{
					BeginVertical(spacing: new Size(5, 5));
					{
						AddRow(new Label {Text = "Upload to dps.report (EI)", Font = Fonts.Sans(12)});
						AddRow("Not uploaded:", dpsReportNotUploadedLabel);
						AddRow("Uploading:", dpsReportUploadingLabel);
						AddRow("Uploaded:", dpsReportUploadedLabel);
						dpsReportUploadFailedRow = BeginVertical(spacing: new Size(5, 5));
						{
							AddRow("Upload failed:", dpsReportUploadFailedLabel);
						}
						EndVertical();
						dpsReportProcessingFailedRow = BeginVertical(spacing: new Size(5, 5));
						{
							AddRow("Processing failed:", dpsReportProcessingFailedLabel);
						}
						EndVertical();
					}
					EndVertical();
					BeginVertical();
					{
						BeginHorizontal();
						{
							Add(dpsReportUploadButton, true);
							Add(dpsReportCancelButton);
						}
						EndHorizontal();
					}
					EndVertical();
					BeginVertical();
					{
						AddRow(dpsReportUploadProgressBar);
						BeginHorizontal(yscale: true);
						{
							Add(dpsReportLinkTextArea);
						}
						EndHorizontal();
						BeginHorizontal(yscale: false);
						{
							AddRow(dpsReportOpenButton);
						}
						EndHorizontal();
					}
					EndVertical();
				}
				EndVertical();
			}
			EndVertical();

			Settings.ShowDebugDataChanged += (sender, args) => { debugSection.Visible = Settings.ShowDebugData; };
			Shown += (sender, args) =>
			{
				// Assigning visibility in the constructor does not work
				debugSection.Visible = Settings.ShowDebugData;
			};

			uploadProcessor.Processed += OnUploadProcessorUpdate;
			uploadProcessor.Unscheduled += OnUploadProcessorUpdate;
			uploadProcessor.Scheduled += OnUploadProcessorUpdate;
		}

		private void OnUploadProcessorUpdate(object sender, EventArgs e)
		{
			Application.Instance.Invoke(UpdateDpsReportUploadStatus);
		}
	}
}