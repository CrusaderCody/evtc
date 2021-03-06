using System.Collections.Generic;
using GW2Scratch.EVTCAnalytics.GameData.Encounters;
using GW2Scratch.EVTCAnalytics.Model.Agents;
using GW2Scratch.EVTCAnalytics.Processing.Encounters.Modes;
using GW2Scratch.EVTCAnalytics.Processing.Encounters.Phases;
using GW2Scratch.EVTCAnalytics.Processing.Encounters.Results;
using GW2Scratch.EVTCAnalytics.Processing.Steps;

namespace GW2Scratch.EVTCAnalytics.Processing.Encounters
{
	public interface IEncounterData
	{
		Encounter Encounter { get; }
		PhaseSplitter PhaseSplitter { get; }
		IResultDeterminer ResultDeterminer { get; }
		IModeDeterminer ModeDeterminer { get; }
		IReadOnlyList<IPostProcessingStep> ProcessingSteps { get; }
		// TODO: Remove Agents and replace with immutable agent queries instead
		List<Agent> Targets { get; }
	}
}