﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GW2Scratch.EVTCAnalytics;
using GW2Scratch.EVTCAnalytics.Processing.Encounters.Results;
using GW2Scratch.EVTCAnalytics.Statistics;
using ScratchLogHTMLGenerator.Sections.General;
using ScratchLogHTMLGenerator.Sections.Phases;
using ScratchLogHTMLGenerator.Sections.ScratchData;

namespace ScratchLogHTMLGenerator
{
	public class HtmlGenerator
	{
		private readonly GW2ApiData gw2ApiData;
		private ITheme Theme { get; }

		public HtmlGenerator(GW2ApiData gw2ApiData, ITheme theme = null)
		{
			this.gw2ApiData = gw2ApiData;
			Theme = theme ?? new DefaultTheme();
		}

		public void WriteHtml(TextWriter writer, LogStatistics stats)
		{
			var summaryPage = new SummaryPage(stats, Theme);
			var playerPage = new PlayerDetailPage(stats.PlayerData, gw2ApiData, Theme);
			var rotationPage = new SquadRotationPage(stats.PlayerData, gw2ApiData, Theme);
			var defaultPage = summaryPage; // Has be a top-level page, not a subpage

			IEnumerable<Page> bossPages = stats.FullFightBossDamageData.Select(x => new BossPage(x, Theme));
			IEnumerable<Page> phasePages = stats.PhaseStats.Select(x => new PhasePage(x, Theme));

			var sections = new[]
			{
				new Section("General", summaryPage, playerPage, rotationPage),
				new Section("Bosses", bossPages.ToArray()),
				new Section("Phases", phasePages.ToArray()),
				new Section("Scratch data",
					//new BuffDataPage(stats.BuffData, Theme),
					new EventDataPage(stats.EventCounts, Theme),
					//new AgentListPage(stats.Agents, Theme),
					new SkillListPage(stats.Skills, Theme)),
			};

			string result = (stats.EncounterResult == EncounterResult.Success ? "Success"
				: stats.EncounterResult == EncounterResult.Failure ? "Failure" : "Result unknown");

			writer.WriteLine($@"<!DOCTYPE html>
<html>
<head>
    <title>{stats.EncounterName}</title>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/bulma/0.7.2/css/bulma.min.css'>
    <!-- <link rel='stylesheet' href='https://jenil.github.io/bulmaswatch/darkly/bulmaswatch.min.css'> -->
    <script defer src='https://use.fontawesome.com/releases/v5.1.0/js/all.js'></script>
    <script defer src='https://cdn.plot.ly/plotly-latest.min.js'></script>
	<script>
		function openTab(tabLink) {{
			var tabs = document.getElementsByClassName('scratch-tab');
			for (var i = 0; i < tabs.length; i++) {{
				tabs[i].classList.add('is-hidden');
			}}
			var tabLinks = document.getElementsByClassName('scratch-tablink');
			for (var i = 0; i < tabLinks.length; i++) {{
				tabLinks[i].classList.remove('is-active');
			}}
			tabLink.classList.add('is-active');
			var tabName = 'tab-' + tabLink.id.substring('tablink-'.length);
			document.getElementById(tabName).classList.remove('is-hidden');
		}}
	</script>");

			foreach (var section in sections)
			{
				foreach (var page in section.Pages)
				{
					WritePageHead(writer, page);
				}
			}

			writer.WriteLine($@"
	<style>");

			foreach (var section in sections)
			{
				foreach (var page in section.Pages)
				{
					WritePageStyle(writer, page);
				}
			}

			writer.WriteLine($@"
	</style>
</head>
<body>
<section class='section'>
<div class='container'>
    <h1 class='title'>{stats.EncounterName}</h1>
    <div class='subtitle'>{result} in {MillisecondsToReadableFormat(stats.FightTimeMs)}</div>
    <div class='columns'>");

			var pageNames = AssignPageNames(sections);
			WriteMenu(writer, pageNames, sections, defaultPage);

			foreach (var section in sections)
			{
				foreach (var page in section.Pages)
				{
					WritePage(writer, pageNames, page, page == defaultPage);
				}
			}

			writer.WriteLine($@"
    </div>
</div>
</section>
<footer class='footer'>
<div class='container'>
<div class='content has-text-centered'>
<p>
    Generated by the Scratch EVTC Parser.
</p>
<p>
	Time of recording {stats.FightStart.ToUniversalTime():yyyy-MM-dd HH:mm UTC}
    <br>
	Recorded by {stats.LogAuthor.Name}
    <br>
	EVTC version {stats.LogVersion}
</p>
</div>
</div>
</footer>
</body>
</html>");
		}

		private void WritePage(TextWriter writer, IReadOnlyDictionary<Page, string> pageNames, Page page,
			bool visible = false)
		{
			var hiddenClass = visible ? "" : "is-hidden";
			writer.WriteLine($"<div id='tab-{pageNames[page]}' class='column scratch-tab {hiddenClass}'>");

			page.WriteHtml(writer);

			writer.WriteLine("</div>");

			foreach (var subpage in page.Subpages)
			{
				WritePage(writer, pageNames, subpage);
			}
		}

		private void WritePageStyle(TextWriter writer, Page page)
		{
			page.WriteStyleHtml(writer);

			foreach (var subpage in page.Subpages)
			{
				WritePageStyle(writer, subpage);
			}
		}

		private void WritePageHead(TextWriter writer, Page page)
		{
			page.WriteHeadHtml(writer);

			foreach (var subpage in page.Subpages)
			{
				WritePageHead(writer, subpage);
			}
		}

		private void WriteMenu(TextWriter writer, IReadOnlyDictionary<Page, string> pageNames,
			IEnumerable<Section> sections, Page defaultPage)
		{
			writer.WriteLine("<aside class='menu column is-3'>");

			foreach (var section in sections)
			{
				writer.WriteLine($"<p class='menu-label'>{section.Name}</p><ul class='menu-list'>");

				foreach (var page in section.Pages)
				{
					WriteMenuPage(writer, pageNames, page, page == defaultPage);
				}

				writer.WriteLine("</ul>");
			}

			writer.WriteLine("</aside>");
		}

		private void WriteMenuPage(TextWriter writer, IReadOnlyDictionary<Page, string> pageNames, Page page,
			bool isActive = false)
		{
			var isActiveClass = isActive ? "is-active" : "";
			writer.WriteLine("<li>");
            writer.WriteLine($"<a id='tablink-{pageNames[page]}' onclick='openTab(this)' class='scratch-tablink {isActiveClass}'>{page.MenuName}</a>");

			if (page.Subpages.Any())
			{
				writer.WriteLine("<ul>");
				foreach (var subpage in page.Subpages)
				{
					WriteMenuPage(writer, pageNames, subpage);
				}

				writer.WriteLine("</ul>");
			}

			writer.WriteLine("</li>");
		}

		private Dictionary<Page, string> AssignPageNames(IEnumerable<Section> sections)
		{
			var names = new Dictionary<Page, string>();

			int sectionIndex = 0;
			foreach (var section in sections)
			{
				int pageIndex = 0;
				foreach (var page in section.Pages)
				{
					AssignPageName(names, page, $"{sectionIndex}-{pageIndex}");
					pageIndex++;
				}

				sectionIndex++;
			}

			return names;
		}

		private void AssignPageName(Dictionary<Page, string> names, Page page, string pageName)
		{
			names[page] = pageName;
			int subpageIndex = 0;
			foreach (var subpage in page.Subpages)
			{
				AssignPageName(names, subpage, $"{pageName}-{subpageIndex}");
				subpageIndex++;
			}
		}


		private string MillisecondsToReadableFormat(long milliseconds)
		{
			return $"{milliseconds / 1000 / 60}m {milliseconds / 1000 % 60}s {milliseconds % 1000}ms";
		}
	}
}