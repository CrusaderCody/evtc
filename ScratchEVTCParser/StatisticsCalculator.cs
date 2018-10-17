using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ScratchEVTCParser.Events;
using ScratchEVTCParser.Model;
using ScratchEVTCParser.Model.Agents;
using ScratchEVTCParser.Model.Skills;
using ScratchEVTCParser.Statistics;
using ScratchEVTCParser.Statistics.Buffs;
using SkillSlot = ScratchEVTCParser.Model.Skills.SkillSlot;

namespace ScratchEVTCParser
{
	public class StatisticsCalculator
	{
		public BuffSimulator BuffSimulator { get; set; } = new BuffSimulator();

		/// <summary>
		/// Calculates statistics for an encounter, such as damage done...
		/// </summary>
		/// <param name="log">The processed log.</param>
		/// <param name="data">Data from the GW2 API, may be null, some statistics won't be calculated.</param>
		/// <returns></returns>
		public LogStatistics GetStatistics(Log log, GW2ApiData apiData)
		{
			var phaseStats = new List<PhaseStats>();
			foreach (var phase in log.Encounter.GetPhases())
			{
				var targetDamageData = new List<TargetSquadDamageData>();
				long phaseDuration = phase.EndTime - phase.StartTime;
				foreach (var target in phase.ImportantEnemies)
				{
					var damageData = GetDamageData(
						log.Agents,
						phase.Events
							.OfType<DamageEvent>()
							.Where(x => x.Defender == target)
							.ToArray(),
						phaseDuration);
					targetDamageData.Add(new TargetSquadDamageData(target, phaseDuration, damageData.Values));
				}

				var totalDamageData = new SquadDamageData(phaseDuration,
					GetDamageData(
						log.Agents,
						phase.Events.OfType<DamageEvent>().ToArray(),
						phaseDuration).Values
				);

				phaseStats.Add(new PhaseStats(phase.Name, phase.StartTime, phase.EndTime, targetDamageData,
					totalDamageData));
			}

			var fightTime = log.Encounter.GetPhases().Sum(x => x.EndTime - x.StartTime);
			var fullFightSquadDamageData = new SquadDamageData(fightTime,
				GetDamageData(log.Agents, log.Events.OfType<DamageEvent>().ToArray(), fightTime).Values);

			var fullFightTargetDamageData = new List<TargetSquadDamageData>();
			foreach (var target in log.Encounter.ImportantEnemies)
			{
				var damageData = GetDamageData(
					log.Agents,
					log.Events
						.OfType<DamageEvent>()
						.Where(x => x.Defender == target)
						.ToArray(),
					fightTime);
				fullFightTargetDamageData.Add(new TargetSquadDamageData(target, fightTime, damageData.Values));
			}

			var eventCounts = new Dictionary<Type, int>();
			foreach (var e in log.Events)
			{
				var type = e.GetType();
				if (!eventCounts.ContainsKey(type))
				{
					eventCounts[type] = 0;
				}

				eventCounts[type]++;
			}

			var eventCountsByName =
				eventCounts.Select(x => (x.Key.Name, x.Value)).ToDictionary(x => x.Item1, x => x.Item2);

			var logAuthor = log.Events.OfType<PointOfViewEvent>().First().RecordingAgent as Player;
			var startTime = log.Events.OfType<LogStartEvent>().First().ServerTime;

			var might = log.Skills.FirstOrDefault(x => x.Id == SkillIds.Might);
			var vulnerability = log.Skills.FirstOrDefault(x => x.Id == SkillIds.Vulnerability);
			if (might != null) BuffSimulator.TrackBuff(might, BuffSimulationType.Intensity, 25);
			if (vulnerability != null) BuffSimulator.TrackBuff(vulnerability, BuffSimulationType.Intensity, 25);

			var buffData = BuffSimulator.SimulateBuffs(log.Agents, log.Events.OfType<BuffEvent>(),
				log.Encounter.GetPhases().Last().EndTime);

			var playerData = GetPlayerData(log, apiData);

			return new LogStatistics(startTime, logAuthor, playerData, phaseStats, fullFightSquadDamageData,
				fullFightTargetDamageData, buffData, log.Encounter.GetResult(), log.Encounter.GetName(),
				log.EVTCVersion, eventCountsByName, log.Agents, log.Skills);
		}

		private IEnumerable<PlayerData> GetPlayerData(Log log, GW2ApiData apiData)
		{
			var players = log.Agents.OfType<Player>().ToArray();
			var deathCountDictionary = players.ToDictionary(x => x, x => 0);
			var downCountDictionary = players.ToDictionary(x => x, x => 0);
			var usedSkillDictionary = players.ToDictionary(x => x, x => new HashSet<Skill>());
			var utilitySkillDictionary =
				apiData == null ? null : players.ToDictionary(x => x, x => new List<SkillData>());
			var healingSkillDictionary =
				apiData == null ? null : players.ToDictionary(x => x, x => new List<SkillData>());
			var eliteSkillDictionary =
				apiData == null ? null : players.ToDictionary(x => x, x => new List<SkillData>());

			foreach (var deadEvent in log.Events.OfType<AgentDeadEvent>().Where(x => x.Agent is Player))
			{
				var player = (Player) deadEvent.Agent;
				deathCountDictionary[player]++;
			}

			foreach (var downEvent in log.Events.OfType<AgentDownedEvent>().Where(x => x.Agent is Player))
			{
				var player = (Player) downEvent.Agent;
				downCountDictionary[player]++;
			}

			// Buff damage events only tell us which conditions/buffs, not what skill actually applied them
			foreach (var damageEvent in log.Events.OfType<PhysicalDamageEvent>().Where(x => x.Attacker is Player))
			{
				var player = (Player) damageEvent.Attacker;
				usedSkillDictionary[player].Add(damageEvent.Skill);
			}

			foreach (var activationEvent in log.Events.OfType<SkillCastEvent>().Where(x => x.Agent is Player))
			{
				var player = (Player) activationEvent.Agent;
				usedSkillDictionary[player].Add(activationEvent.Skill);
			}

			if (apiData != null)
			{
				foreach (var player in players)
				{
					var utilitySkills = new List<SkillData>();
					var healingSkills = new List<SkillData>();
					var eliteSkills = new List<SkillData>();
					foreach (var usedSkill in usedSkillDictionary[player])
					{
						var skillData = apiData.GetSkillData(usedSkill);

						// Skills may be also registered as used if they affect other players and do damage through them
						if (skillData != null && skillData.Professions.Contains(player.Profession))
						{
							if (skillData.Slot == SkillSlot.Elite)
							{
								eliteSkills.Add(skillData);
							}
							else if (skillData.Slot == SkillSlot.Utility)
							{
								utilitySkills.Add(skillData);
							}
							else if (skillData.Slot == SkillSlot.Heal)
							{
								healingSkills.Add(skillData);
							}
						}
					}

					utilitySkillDictionary[player] = utilitySkills;
					healingSkillDictionary[player] = healingSkills;
					eliteSkillDictionary[player] = eliteSkills;
				}
			}


			return players.Select(p =>
				new PlayerData(p, downCountDictionary[p], deathCountDictionary[p], usedSkillDictionary[p],
					healingSkillDictionary?[p], utilitySkillDictionary?[p], eliteSkillDictionary?[p])).ToArray();
		}

		private Dictionary<Agent, DamageData> GetDamageData(IEnumerable<Agent> agents, ICollection<DamageEvent> events,
			long phaseDuration)
		{
			var physicalBossDamages = events.OfType<PhysicalDamageEvent>();
			var conditionBossDamages = events.OfType<BuffDamageEvent>();

			var damageDataByAttacker = new Dictionary<Agent, DamageData>();

			// Ensure all players are always in the damage data, even if they did no damage.
			foreach (var player in agents.OfType<Player>())
			{
				if (!damageDataByAttacker.ContainsKey(player))
				{
					damageDataByAttacker[player] = new DamageData(player, phaseDuration, 0, 0);
				}
			}

			foreach (var damageEvent in physicalBossDamages)
			{
				var attacker = damageEvent.Attacker;
				long damage = damageEvent.Damage;
				if (attacker == null)
				{
					continue; // TODO: Save as unknown damage
				}

				var mainMaster = attacker;
				while (mainMaster.Master != null)
				{
					mainMaster = attacker.Master;
				}

				if (!damageDataByAttacker.ContainsKey(mainMaster))
				{
					damageDataByAttacker[mainMaster] = new DamageData(mainMaster, phaseDuration, 0, 0);
				}

				damageDataByAttacker[mainMaster] += new DamageData(mainMaster, phaseDuration, damage, 0);
			}

			foreach (var damageEvent in conditionBossDamages)
			{
				var attacker = damageEvent.Attacker;
				long damage = damageEvent.Damage;
				if (attacker == null)
				{
					continue; // TODO: Save as unknown damage
				}

				var mainMaster = attacker;
				while (mainMaster.Master != null)
				{
					mainMaster = attacker.Master;
				}

				if (!damageDataByAttacker.ContainsKey(mainMaster))
				{
					damageDataByAttacker[mainMaster] = new DamageData(mainMaster, phaseDuration, 0, 0);
				}

				damageDataByAttacker[mainMaster] += new DamageData(mainMaster, phaseDuration, 0, damage);
			}

			return damageDataByAttacker;
		}
	}
}