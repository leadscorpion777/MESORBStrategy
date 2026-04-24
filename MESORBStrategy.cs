#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class MESORBStrategy : Strategy
	{
		#region Variables

		// Etat de la journee ORB
		private DateTime	currentSessionDate;
		private double		rangeHigh;
		private double		rangeLow;
		private bool		rangeLocked;
		private bool		tradedToday;
		private int			currentDirection;	// 1=long, -1=short, 0=flat

		// Indicateurs
		private VOLMA		volMa;
		private ATR			atr;
		private VWAP8		vwap;

		// Stats
		private int			statBreakoutsLong;
		private int			statBreakoutsShort;
		private int			statVolumeRejected;
		private int			statTimeStops;

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Opening Range Breakout 30min sur MES (RTH US). Entry sur breakout + filtre volume. Stop opposite side of range. Target 2x range ou trailing ATR. Flat a 15h45 NY.";
				Name						= "MESORBStrategy";
				Calculate					= Calculate.OnBarClose;
				EntriesPerDirection			= 1;
				EntryHandling				= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds	= 30;
				IsFillLimitOnTouch			= false;
				MaximumBarsLookBack			= MaximumBarsLookBack.Infinite;
				StartBehavior				= StartBehavior.WaitUntilFlat;
				IsInstantiatedOnEachOptimizationIteration = true;

				// ORB params
				RangeMinutes				= 30;
				SessionStartHour			= 9;
				SessionStartMinute			= 30;
				TimeStopHour				= 15;
				TimeStopMinute				= 45;

				// Volume filter
				EnableVolumeFilter			= true;
				VolumeMAPeriod				= 20;
				VolumeMultiplier			= 1.3;

				// Target / Stop
				TargetMode					= 1;	// 1=fixe 2x range, 2=trailing ATR 3x
				TargetMultiplier			= 2.0;
				ATRPeriod					= 14;
				ATRTrailMultiplier			= 3.0;

				// Filtre VWAP (optionnel V1)
				EnableVWAPFilter			= false;

				// Filtre jours
				TradeLundi					= true;
				TradeMardi					= true;
				TradeMercredi				= true;
				TradeJeudi					= true;
				TradeVendredi				= true;

				// Visuel
				RangeOpacity				= 25;
			}
			else if (State == State.Configure)
			{
				currentSessionDate	= DateTime.MinValue;
				rangeHigh			= double.MinValue;
				rangeLow			= double.MaxValue;
				rangeLocked			= false;
				tradedToday			= false;
				currentDirection	= 0;

				statBreakoutsLong	= 0;
				statBreakoutsShort	= 0;
				statVolumeRejected	= 0;
				statTimeStops		= 0;
			}
			else if (State == State.DataLoaded)
			{
				volMa	= VOLMA(VolumeMAPeriod);
				atr		= ATR(ATRPeriod);
				vwap	= VWAP8();
			}
			else if (State == State.Terminated)
			{
				if (SystemPerformance != null && SystemPerformance.AllTrades.Count > 0)
					PrintStats();
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < Math.Max(VolumeMAPeriod, ATRPeriod) + 5)
				return;

			// ====================================================================
			// LOGIQUE ORB A CODER ICI (V1)
			// ====================================================================
			// 1. Detecter nouveau jour de trading -> reset range, tradedToday=false
			// 2. Construire la range sur les 30 premieres minutes (9h30-10h00 NY)
			// 3. Au close de 10h00 NY -> lock range (rangeLocked=true)
			// 4. Apres lock, sur chaque close 5min :
			//    - Si close > rangeHigh + volume OK -> EnterLong
			//    - Si close < rangeLow + volume OK -> EnterShort
			// 5. Gestion Stop (opposite side) et Target (2x range ou trailing ATR)
			// 6. Time stop : flat a 15h45 NY
			// 7. 1 trade max par jour (tradedToday=true apres entry)
			// ====================================================================
		}

		#region Helpers

		private bool IsDayAllowed()
		{
			switch (Time[0].DayOfWeek)
			{
				case DayOfWeek.Monday:		return TradeLundi;
				case DayOfWeek.Tuesday:		return TradeMardi;
				case DayOfWeek.Wednesday:	return TradeMercredi;
				case DayOfWeek.Thursday:	return TradeJeudi;
				case DayOfWeek.Friday:		return TradeVendredi;
				default:					return false;
			}
		}

		private bool IsInOpeningRangeWindow()
		{
			int minutesSinceStart = (Time[0].Hour - SessionStartHour) * 60 + (Time[0].Minute - SessionStartMinute);
			return minutesSinceStart >= 0 && minutesSinceStart < RangeMinutes;
		}

		private bool IsPastTimeStop()
		{
			return Time[0].Hour > TimeStopHour || (Time[0].Hour == TimeStopHour && Time[0].Minute >= TimeStopMinute);
		}

		private bool VolumeConfirms()
		{
			if (!EnableVolumeFilter) return true;
			return Volume[0] > volMa[0] * VolumeMultiplier;
		}

		#endregion

		#region Stats

		private void PrintStats()
		{
			var all		= SystemPerformance.AllTrades;
			var longs	= SystemPerformance.LongTrades;
			var shorts	= SystemPerformance.ShortTrades;

			double totalPnL		= all.TradesPerformance.Currency.CumProfit;
			double longPnL		= longs.TradesPerformance.Currency.CumProfit;
			double shortPnL		= shorts.TradesPerformance.Currency.CumProfit;
			int totalCount		= all.Count;
			int longCount		= longs.Count;
			int shortCount		= shorts.Count;
			int longWins		= longs.WinningTrades.Count;
			int shortWins		= shorts.WinningTrades.Count;
			int totalWins		= all.WinningTrades.Count;
			double avgWin		= totalWins > 0 ? all.WinningTrades.TradesPerformance.Currency.CumProfit / totalWins : 0;
			int totalLosses		= all.LosingTrades.Count;
			double avgLoss		= totalLosses > 0 ? all.LosingTrades.TradesPerformance.Currency.CumProfit / totalLosses : 0;

			var sb = new System.Text.StringBuilder();
			Action<string> Log = (string s) => { Print(s); sb.AppendLine(s); };

			Log("========================================");
			Log("       MES ORB - STATS FINALES          ");
			Log("========================================");
			Log("");
			Log("--- COMPTEURS ---");
			Log("Total trades       : " + totalCount);
			Log("Breakouts Long     : " + statBreakoutsLong);
			Log("Breakouts Short    : " + statBreakoutsShort);
			Log("Rejets volume      : " + statVolumeRejected);
			Log("Time stops         : " + statTimeStops);
			Log("");
			Log("--- LONG ---");
			Log("Trades Long        : " + longCount);
			Log("Wins Long          : " + longWins);
			Log("Losses Long        : " + (longCount - longWins));
			Log("Winrate Long       : " + (longCount > 0 ? (100.0 * longWins / longCount).ToString("F1") : "0") + "%");
			Log("PnL Long           : " + longPnL.ToString("F2") + " $");
			Log("");
			Log("--- SHORT ---");
			Log("Trades Short       : " + shortCount);
			Log("Wins Short         : " + shortWins);
			Log("Losses Short       : " + (shortCount - shortWins));
			Log("Winrate Short      : " + (shortCount > 0 ? (100.0 * shortWins / shortCount).ToString("F1") : "0") + "%");
			Log("PnL Short          : " + shortPnL.ToString("F2") + " $");
			Log("");
			Log("--- GLOBAL ---");
			Log("PnL Total          : " + totalPnL.ToString("F2") + " $");
			Log("Winrate Global     : " + (totalCount > 0 ? (100.0 * totalWins / totalCount).ToString("F1") : "0") + "%");
			Log("Avg Win            : " + avgWin.ToString("F2") + " $");
			Log("Avg Loss           : " + avgLoss.ToString("F2") + " $");
			Log("Profit Factor      : " + (totalLosses > 0 && avgLoss != 0 ? (all.WinningTrades.TradesPerformance.Currency.CumProfit / Math.Abs(all.LosingTrades.TradesPerformance.Currency.CumProfit)).ToString("F2") : "N/A"));

			// Drawdown
			double maxDD = 0;
			double peak = 0;
			double cumPnL = 0;
			DateTime ddStart = DateTime.MinValue, ddEnd = DateTime.MinValue;
			DateTime currentDDStart = DateTime.MinValue;
			int maxUnderwaterDays = 0;
			DateTime uwStart = DateTime.MinValue;

			for (int t = 0; t < all.Count; t++)
			{
				cumPnL += all[t].ProfitCurrency;
				if (cumPnL > peak)
				{
					peak = cumPnL;
					currentDDStart = all[t].Exit.Time;
					if (uwStart != DateTime.MinValue)
					{
						int uwDays = (int)(all[t].Exit.Time - uwStart).TotalDays;
						if (uwDays > maxUnderwaterDays) { maxUnderwaterDays = uwDays; }
					}
					uwStart = DateTime.MinValue;
				}
				else
				{
					if (uwStart == DateTime.MinValue) uwStart = all[t].Exit.Time;
					double dd = peak - cumPnL;
					if (dd > maxDD)
					{
						maxDD = dd;
						ddStart = currentDDStart;
						ddEnd = all[t].Exit.Time;
					}
				}
			}
			if (uwStart != DateTime.MinValue && all.Count > 0)
			{
				int uwDays = (int)(all[all.Count - 1].Exit.Time - uwStart).TotalDays;
				if (uwDays > maxUnderwaterDays) maxUnderwaterDays = uwDays;
			}

			Log("");
			Log("--- DRAWDOWN ---");
			Log("Max Drawdown       : " + maxDD.ToString("F2") + " $");
			Log("Max Time Underwater: " + maxUnderwaterDays + " jours (" + (maxUnderwaterDays / 30.0).ToString("F1") + " mois)");
			if (ddStart != DateTime.MinValue)
				Log("  Periode          : " + ddStart.ToString("dd/MM/yyyy") + " -> " + ddEnd.ToString("dd/MM/yyyy"));

			// Mois perdants consecutifs
			var monthlyPnL = new SortedDictionary<string, double>();
			for (int t = 0; t < all.Count; t++)
			{
				string key = all[t].Exit.Time.ToString("yyyy-MM");
				if (!monthlyPnL.ContainsKey(key)) monthlyPnL[key] = 0;
				monthlyPnL[key] += all[t].ProfitCurrency;
			}

			int maxConsecLoss = 0, curConsecLoss = 0;
			string consecStart = "", consecEnd = "", curStart = "";
			foreach (var kv in monthlyPnL)
			{
				if (kv.Value < 0)
				{
					if (curConsecLoss == 0) curStart = kv.Key;
					curConsecLoss++;
					if (curConsecLoss > maxConsecLoss)
					{
						maxConsecLoss = curConsecLoss;
						consecStart = curStart;
						consecEnd = kv.Key;
					}
				}
				else
					curConsecLoss = 0;
			}
			Log("Mois perdants consec: " + maxConsecLoss + " mois");
			if (maxConsecLoss > 0)
				Log("  Periode          : " + consecStart + " -> " + consecEnd);

			// PnL mensuel
			Log("");
			Log("--- PNL MENSUEL ---");
			foreach (var kv in monthlyPnL)
			{
				string marker = kv.Value < 0 ? " ***" : "";
				Log("  " + kv.Key + " : " + kv.Value.ToString("F2") + " $" + marker);
			}

			// PnL hebdomadaire
			var weeklyPnL = new SortedDictionary<string, double>();
			var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
			for (int t = 0; t < all.Count; t++)
			{
				DateTime d = all[t].Exit.Time;
				int week = cal.GetWeekOfYear(d, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
				string key = d.Year + "-W" + week.ToString("D2");
				if (!weeklyPnL.ContainsKey(key)) weeklyPnL[key] = 0;
				weeklyPnL[key] += all[t].ProfitCurrency;
			}

			Log("");
			Log("--- PNL HEBDO ---");
			foreach (var kv in weeklyPnL)
			{
				string marker = kv.Value < 0 ? " ***" : "";
				Log("  " + kv.Key + " : " + kv.Value.ToString("F2") + " $" + marker);
			}

			Log("========================================");

			// Auto-export vers UserDataDir (partage Parallels Mac/Windows)
			Print("[LOG] UserDataDir = " + NinjaTrader.Core.Globals.UserDataDir);
			try
			{
				string logPath = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "last_backtest_MESORBStrategy.log");
				System.IO.File.WriteAllText(logPath, sb.ToString());
				Print("[LOG] Stats ecrites dans : " + logPath);
			}
			catch (Exception ex)
			{
				Print("[LOG] Echec ecriture : " + ex.Message);
			}
		}

		#endregion

		#region Properties

		// --- Groupe 1 : Opening Range ---
		[NinjaScriptProperty]
		[Range(5, 120)]
		[Display(Name = "Range Minutes", Description = "Duree de l'opening range en minutes (defaut: 30)",
			Order = 1, GroupName = "1. Opening Range")]
		public int RangeMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Session Start Hour", Description = "Heure debut RTH (defaut: 9 = 9h30 NY)",
			Order = 2, GroupName = "1. Opening Range")]
		public int SessionStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Session Start Minute", Description = "Minute debut RTH (defaut: 30)",
			Order = 3, GroupName = "1. Opening Range")]
		public int SessionStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Time Stop Hour", Description = "Heure flat forcee (defaut: 15 = 15h45 NY)",
			Order = 4, GroupName = "1. Opening Range")]
		public int TimeStopHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Time Stop Minute", Description = "Minute flat forcee (defaut: 45)",
			Order = 5, GroupName = "1. Opening Range")]
		public int TimeStopMinute { get; set; }

		// --- Groupe 2 : Volume Filter ---
		[NinjaScriptProperty]
		[Display(Name = "Enable Volume Filter", Description = "Active le filtre de confirmation volume (defaut: true)",
			Order = 1, GroupName = "2. Volume Filter")]
		public bool EnableVolumeFilter { get; set; }

		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Volume MA Period", Description = "Periode MA du volume (defaut: 20)",
			Order = 2, GroupName = "2. Volume Filter")]
		public int VolumeMAPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 10.0)]
		[Display(Name = "Volume Multiplier", Description = "Multiplicateur VolMA pour valider breakout (defaut: 1.3)",
			Order = 3, GroupName = "2. Volume Filter")]
		public double VolumeMultiplier { get; set; }

		// --- Groupe 3 : Target / Stop ---
		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name = "Target Mode", Description = "1 = target fixe (X * range), 2 = trailing ATR",
			Order = 1, GroupName = "3. Target Stop")]
		public int TargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 10.0)]
		[Display(Name = "Target Multiplier", Description = "Multiplicateur de la hauteur de range pour TP fixe (defaut: 2.0)",
			Order = 2, GroupName = "3. Target Stop")]
		public double TargetMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "ATR Period", Description = "Periode ATR pour trailing stop (defaut: 14)",
			Order = 3, GroupName = "3. Target Stop")]
		public int ATRPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 10.0)]
		[Display(Name = "ATR Trail Multiplier", Description = "Multiplicateur ATR pour trailing stop (defaut: 3.0)",
			Order = 4, GroupName = "3. Target Stop")]
		public double ATRTrailMultiplier { get; set; }

		// --- Groupe 4 : Filtres optionnels ---
		[NinjaScriptProperty]
		[Display(Name = "Enable VWAP Filter", Description = "Long only si > VWAP, short only si < VWAP (defaut: false)",
			Order = 1, GroupName = "4. Filtres optionnels")]
		public bool EnableVWAPFilter { get; set; }

		// --- Groupe 5 : Filtre Jours ---
		[NinjaScriptProperty]
		[Display(Name = "Lundi", Order = 1, GroupName = "5. Jours autorises")]
		public bool TradeLundi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mardi", Order = 2, GroupName = "5. Jours autorises")]
		public bool TradeMardi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mercredi", Order = 3, GroupName = "5. Jours autorises")]
		public bool TradeMercredi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Jeudi", Order = 4, GroupName = "5. Jours autorises")]
		public bool TradeJeudi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vendredi", Order = 5, GroupName = "5. Jours autorises")]
		public bool TradeVendredi { get; set; }

		// --- Groupe 6 : Visuel ---
		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Range Opacity", Description = "Opacite du rectangle de range (0-100)",
			Order = 1, GroupName = "6. Visuel")]
		public int RangeOpacity { get; set; }

		#endregion
	}
}
