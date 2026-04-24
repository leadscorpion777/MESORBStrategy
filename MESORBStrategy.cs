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
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class MESORBStrategy : Strategy
	{
		#region Variables

		// Etat journalier ORB
		private DateTime	sessionStartTime;	// timestamp du 1er bar de la session en cours
		private bool		sessionInitialized;
		private double		rangeHigh;
		private double		rangeLow;
		private bool		rangeLocked;
		private bool		tradedToday;
		private string		rangeTagHigh;
		private string		rangeTagLow;
		private string		rangeTagBox;

		// Entree / trailing
		private double		entryPrice;
		private double		trailStopPrice;
		private int			lastTradeBar;

		// VWAP session (calcul manuel)
		private double		cumPriceVol;
		private double		cumVol;
		private double		sessionVwap;

		// Indicateurs
		private SMA			volMa;
		private ATR			atr;

		// Stats
		private int			statBreakoutsLong;
		private int			statBreakoutsShort;
		private int			statVolumeRejected;
		private int			statVwapRejected;
		private int			statTimeStops;
		private int			statDaysTraded;

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Opening Range Breakout (ORB) sur MES — RTH US. Range 30min, entry breakout + filtres volume/VWAP, stop opposite side, target fixe OU trailing ATR, time stop 15h45 NY, 1 trade/jour. Chaque filtre toggleable.";
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
				DefaultQuantity				= 1;

				// --- ORB ---
				RangeMinutes				= 30;
				EnableTimeStop				= true;
				TimeStopMinutesAfterOpen	= 375;	// 6h15 apres open = 15h45 NY sur RTH 9h30
				EnableOneTradePerDay		= true;
				EnableDebugPrint			= true;	// Print info sur les 1eres barres de chaque session

				// --- Volume Filter ---
				EnableVolumeFilter			= true;
				VolumeMAPeriod				= 20;
				VolumeMultiplier			= 1.3;

				// --- VWAP Filter ---
				EnableVWAPFilter			= false;

				// --- Target (fixe) ---
				EnableFixedTarget			= true;
				TargetMultiplier			= 2.0;

				// --- Stop opposite side ---
				EnableRangeStop				= true;

				// --- Trailing ATR ---
				EnableATRTrail				= false;
				ATRPeriod					= 14;
				ATRTrailMultiplier			= 3.0;

				// --- Jours autorises ---
				TradeLundi					= true;
				TradeMardi					= true;
				TradeMercredi				= true;
				TradeJeudi					= true;
				TradeVendredi				= true;

				// --- Visuel ---
				EnableDrawRange				= true;
				RangeOpacity				= 25;
			}
			else if (State == State.Configure)
			{
				sessionStartTime	= DateTime.MinValue;
				sessionInitialized	= false;
				rangeHigh			= double.MinValue;
				rangeLow			= double.MaxValue;
				rangeLocked			= false;
				tradedToday			= false;

				entryPrice			= 0;
				trailStopPrice		= 0;
				lastTradeBar		= -1;

				cumPriceVol			= 0;
				cumVol				= 0;
				sessionVwap			= 0;

				statBreakoutsLong	= 0;
				statBreakoutsShort	= 0;
				statVolumeRejected	= 0;
				statVwapRejected	= 0;
				statTimeStops		= 0;
				statDaysTraded		= 0;
			}
			else if (State == State.DataLoaded)
			{
				volMa	= SMA(Volume, VolumeMAPeriod);
				atr		= ATR(ATRPeriod);
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

			// ----- 1. Detection nouvelle session via IsFirstBarOfSession (fuseau-agnostique) -----
			if (Bars.IsFirstBarOfSession)
			{
				sessionStartTime	= Time[0];
				sessionInitialized	= true;
				rangeHigh			= double.MinValue;
				rangeLow			= double.MaxValue;
				rangeLocked			= false;
				tradedToday			= false;
				cumPriceVol			= 0;
				cumVol				= 0;
				sessionVwap			= 0;
				trailStopPrice		= 0;

				string d = Time[0].ToString("yyyyMMdd");
				rangeTagHigh	= "ORH_" + d;
				rangeTagLow		= "ORL_" + d;
				rangeTagBox		= "ORBOX_" + d;

				if (EnableDebugPrint)
					Print("[SESSION] Start @ " + Time[0].ToString("yyyy-MM-dd HH:mm"));
			}

			if (!sessionInitialized)
				return;

			// ----- 2. VWAP session (manuel) -----
			double typical	= (High[0] + Low[0] + Close[0]) / 3.0;
			cumPriceVol	+= typical * Volume[0];
			cumVol		+= Volume[0];
			sessionVwap	= cumVol > 0 ? cumPriceVol / cumVol : Close[0];

			int minSinceOpen = (int)Math.Round((Time[0] - sessionStartTime).TotalMinutes);

			// ----- 3. Construction opening range -----
			if (!rangeLocked)
			{
				// La 1ere barre de session est incluse (minSinceOpen >= 0)
				if (minSinceOpen >= 0 && minSinceOpen < RangeMinutes + BarsPeriod.Value)
				{
					if (High[0] > rangeHigh) rangeHigh = High[0];
					if (Low[0] < rangeLow)   rangeLow  = Low[0];
				}
				if (minSinceOpen + BarsPeriod.Value > RangeMinutes && rangeHigh > double.MinValue && rangeLow < double.MaxValue)
				{
					rangeLocked = true;
					if (EnableDebugPrint)
						Print("[RANGE LOCKED] @ " + Time[0].ToString("HH:mm") + " H=" + rangeHigh.ToString("F2") + " L=" + rangeLow.ToString("F2") + " (" + (rangeHigh - rangeLow).ToString("F2") + " pts)");
					if (EnableDrawRange)
						DrawRange();
				}
				return;
			}

			// ----- 4. Time Stop -----
			bool pastTimeStop = EnableTimeStop && minSinceOpen >= TimeStopMinutesAfterOpen;
			if (pastTimeStop && Position.MarketPosition != MarketPosition.Flat)
			{
				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong("TimeStop", "ORB Long");
				else
					ExitShort("TimeStop", "ORB Short");
				statTimeStops++;
				return;
			}

			// ----- 5. Trailing ATR -----
			if (EnableATRTrail && Position.MarketPosition != MarketPosition.Flat)
				UpdateTrailingStop();

			// ----- 6. Breakout entry -----
			if (!tradedToday
				&& Position.MarketPosition == MarketPosition.Flat
				&& IsDayAllowed()
				&& !pastTimeStop
				&& CurrentBar != lastTradeBar)
			{
				TryBreakoutEntry();
			}
		}

		#region ORB logic

		private void TryBreakoutEntry()
		{
			bool breakoutLong  = Close[0] > rangeHigh;
			bool breakoutShort = Close[0] < rangeLow;

			if (!breakoutLong && !breakoutShort)
				return;

			// Volume filter
			if (EnableVolumeFilter && !VolumeConfirms())
			{
				statVolumeRejected++;
				return;
			}

			// VWAP filter
			if (EnableVWAPFilter)
			{
				if (breakoutLong && Close[0] <= sessionVwap) { statVwapRejected++; return; }
				if (breakoutShort && Close[0] >= sessionVwap) { statVwapRejected++; return; }
			}

			double rangeHeight = rangeHigh - rangeLow;
			if (rangeHeight <= 0)
				return;

			if (breakoutLong)
			{
				if (EnableRangeStop)
					SetStopLoss("ORB Long", CalculationMode.Price, rangeLow, false);
				if (EnableFixedTarget)
					SetProfitTarget("ORB Long", CalculationMode.Price, Close[0] + rangeHeight * TargetMultiplier);

				EnterLong(DefaultQuantity, "ORB Long");
				entryPrice		= Close[0];
				trailStopPrice	= rangeLow;
				statBreakoutsLong++;
				if (EnableOneTradePerDay) { tradedToday = true; statDaysTraded++; }
				lastTradeBar = CurrentBar;
			}
			else if (breakoutShort)
			{
				if (EnableRangeStop)
					SetStopLoss("ORB Short", CalculationMode.Price, rangeHigh, false);
				if (EnableFixedTarget)
					SetProfitTarget("ORB Short", CalculationMode.Price, Close[0] - rangeHeight * TargetMultiplier);

				EnterShort(DefaultQuantity, "ORB Short");
				entryPrice		= Close[0];
				trailStopPrice	= rangeHigh;
				statBreakoutsShort++;
				if (EnableOneTradePerDay) { tradedToday = true; statDaysTraded++; }
				lastTradeBar = CurrentBar;
			}
		}

		private void UpdateTrailingStop()
		{
			double atrVal = atr[0] * ATRTrailMultiplier;

			if (Position.MarketPosition == MarketPosition.Long)
			{
				double candidate = Close[0] - atrVal;
				if (candidate > trailStopPrice)
				{
					trailStopPrice = candidate;
					SetStopLoss("ORB Long", CalculationMode.Price, trailStopPrice, false);
				}
			}
			else if (Position.MarketPosition == MarketPosition.Short)
			{
				double candidate = Close[0] + atrVal;
				if (candidate < trailStopPrice || trailStopPrice == 0)
				{
					trailStopPrice = candidate;
					SetStopLoss("ORB Short", CalculationMode.Price, trailStopPrice, false);
				}
			}
		}

		#endregion

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

		private bool VolumeConfirms()
		{
			return Volume[0] > volMa[0] * VolumeMultiplier;
		}

		private void DrawRange()
		{
			// Lignes HIGH et LOW du range (version simple et robuste)
			Draw.HorizontalLine(this, rangeTagHigh, rangeHigh, Brushes.LimeGreen);
			Draw.HorizontalLine(this, rangeTagLow,  rangeLow,  Brushes.OrangeRed);

			// Rectangle optionnel (si la signature Draw.Rectangle plante, commenter ce bloc)
			int barsAgo = 0;
			if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value > 0)
				barsAgo = Math.Max(1, RangeMinutes / BarsPeriod.Value);
			try
			{
				Draw.Rectangle(this, rangeTagBox, false,
					barsAgo, rangeLow,
					0, rangeHigh,
					Brushes.CornflowerBlue, Brushes.CornflowerBlue, RangeOpacity);
			}
			catch { /* ignore si signature Draw.Rectangle indisponible */ }
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
			Log("--- PARAMETRES ACTIFS ---");
			Log("RangeMinutes       : " + RangeMinutes);
			Log("VolumeFilter       : " + (EnableVolumeFilter ? "ON (x" + VolumeMultiplier.ToString("F2") + ")" : "OFF"));
			Log("VWAPFilter         : " + (EnableVWAPFilter ? "ON" : "OFF"));
			Log("FixedTarget        : " + (EnableFixedTarget ? "ON (" + TargetMultiplier.ToString("F1") + "x range)" : "OFF"));
			Log("RangeStop          : " + (EnableRangeStop ? "ON" : "OFF"));
			Log("ATRTrail           : " + (EnableATRTrail ? "ON (" + ATRTrailMultiplier.ToString("F1") + "x ATR" + ATRPeriod + ")" : "OFF"));
			Log("TimeStop           : " + (EnableTimeStop ? "ON (" + TimeStopMinutesAfterOpen + " min apres open)" : "OFF"));
			Log("OneTradePerDay     : " + (EnableOneTradePerDay ? "ON" : "OFF"));
			Log("");
			Log("--- COMPTEURS ---");
			Log("Total trades       : " + totalCount);
			Log("Breakouts Long     : " + statBreakoutsLong);
			Log("Breakouts Short    : " + statBreakoutsShort);
			Log("Rejets volume      : " + statVolumeRejected);
			Log("Rejets VWAP        : " + statVwapRejected);
			Log("Time stops         : " + statTimeStops);
			Log("Jours traded       : " + statDaysTraded);
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
			double maxDD = 0, peak = 0, cumPnL = 0;
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
						if (uwDays > maxUnderwaterDays) maxUnderwaterDays = uwDays;
					}
					uwStart = DateTime.MinValue;
				}
				else
				{
					if (uwStart == DateTime.MinValue) uwStart = all[t].Exit.Time;
					double dd = peak - cumPnL;
					if (dd > maxDD) { maxDD = dd; ddStart = currentDDStart; ddEnd = all[t].Exit.Time; }
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

			// Mois perdants consecutifs + PnL mensuel
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
				else curConsecLoss = 0;
			}
			Log("Mois perdants consec: " + maxConsecLoss + " mois");
			if (maxConsecLoss > 0)
				Log("  Periode          : " + consecStart + " -> " + consecEnd);

			Log("");
			Log("--- PNL MENSUEL ---");
			foreach (var kv in monthlyPnL)
			{
				string marker = kv.Value < 0 ? " ***" : "";
				Log("  " + kv.Key + " : " + kv.Value.ToString("F2") + " $" + marker);
			}

			// PnL hebdo
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

		// ===== Groupe 1 : Opening Range =====
		[NinjaScriptProperty]
		[Range(5, 240)]
		[Display(Name = "Range Minutes", Description = "Duree de l'opening range (defaut: 30 min). Base sur IsFirstBarOfSession — aucun pb de fuseau horaire.",
			Order = 1, GroupName = "1. Opening Range")]
		public int RangeMinutes { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug Print", Description = "Imprime debut de session + lock range (pour verifier que les sessions sont bien detectees)",
			Order = 2, GroupName = "1. Opening Range")]
		public bool EnableDebugPrint { get; set; }

		// ===== Groupe 2 : Time Stop =====
		[NinjaScriptProperty]
		[Display(Name = "Enable Time Stop", Description = "Flatter toutes positions N minutes apres l'open (defaut: ON)",
			Order = 1, GroupName = "2. Time Stop")]
		public bool EnableTimeStop { get; set; }

		[NinjaScriptProperty]
		[Range(30, 1000)]
		[Display(Name = "Time Stop Minutes After Open", Description = "Minutes apres debut session pour flat forcee (375 = 6h15 apres = 15h45 NY si open 9h30)",
			Order = 2, GroupName = "2. Time Stop")]
		public int TimeStopMinutesAfterOpen { get; set; }

		// ===== Groupe 3 : Re-Entry =====
		[NinjaScriptProperty]
		[Display(Name = "Enable One Trade Per Day", Description = "Bloque les re-entries apres 1er trade (defaut: ON)",
			Order = 1, GroupName = "3. Re-Entry")]
		public bool EnableOneTradePerDay { get; set; }

		// ===== Groupe 4 : Volume Filter =====
		[NinjaScriptProperty]
		[Display(Name = "Enable Volume Filter", Description = "Breakout valide seulement si volume > MA x multiplier (defaut: ON)",
			Order = 1, GroupName = "4. Volume Filter")]
		public bool EnableVolumeFilter { get; set; }

		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Volume MA Period", Description = "Periode de la moyenne mobile du volume (defaut: 20)",
			Order = 2, GroupName = "4. Volume Filter")]
		public int VolumeMAPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 10.0)]
		[Display(Name = "Volume Multiplier", Description = "Multiplicateur VolMA pour valider (defaut: 1.3)",
			Order = 3, GroupName = "4. Volume Filter")]
		public double VolumeMultiplier { get; set; }

		// ===== Groupe 5 : VWAP Filter =====
		[NinjaScriptProperty]
		[Display(Name = "Enable VWAP Filter", Description = "Long only si close > VWAP, short only si close < VWAP (defaut: OFF)",
			Order = 1, GroupName = "5. VWAP Filter")]
		public bool EnableVWAPFilter { get; set; }

		// ===== Groupe 6 : Target fixe =====
		[NinjaScriptProperty]
		[Display(Name = "Enable Fixed Target", Description = "Take profit a X fois la hauteur du range (defaut: ON)",
			Order = 1, GroupName = "6. Fixed Target")]
		public bool EnableFixedTarget { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 10.0)]
		[Display(Name = "Target Multiplier", Description = "Multiplicateur du range pour TP (defaut: 2.0)",
			Order = 2, GroupName = "6. Fixed Target")]
		public double TargetMultiplier { get; set; }

		// ===== Groupe 7 : Range Stop =====
		[NinjaScriptProperty]
		[Display(Name = "Enable Range Stop", Description = "Stop loss sur cote oppose du range (defaut: ON)",
			Order = 1, GroupName = "7. Range Stop")]
		public bool EnableRangeStop { get; set; }

		// ===== Groupe 8 : ATR Trailing Stop =====
		[NinjaScriptProperty]
		[Display(Name = "Enable ATR Trail", Description = "Trailing stop base sur ATR (defaut: OFF). Cumulable avec RangeStop — prend le plus proche.",
			Order = 1, GroupName = "8. ATR Trailing Stop")]
		public bool EnableATRTrail { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "ATR Period", Description = "Periode ATR (defaut: 14)",
			Order = 2, GroupName = "8. ATR Trailing Stop")]
		public int ATRPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 10.0)]
		[Display(Name = "ATR Trail Multiplier", Description = "Multiplicateur ATR pour trailing (defaut: 3.0)",
			Order = 3, GroupName = "8. ATR Trailing Stop")]
		public double ATRTrailMultiplier { get; set; }

		// ===== Groupe 9 : Jours autorises =====
		[NinjaScriptProperty]
		[Display(Name = "Lundi", Order = 1, GroupName = "9. Jours autorises")]
		public bool TradeLundi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mardi", Order = 2, GroupName = "9. Jours autorises")]
		public bool TradeMardi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mercredi", Order = 3, GroupName = "9. Jours autorises")]
		public bool TradeMercredi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Jeudi", Order = 4, GroupName = "9. Jours autorises")]
		public bool TradeJeudi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vendredi", Order = 5, GroupName = "9. Jours autorises")]
		public bool TradeVendredi { get; set; }

		// ===== Groupe 10 : Visuel =====
		[NinjaScriptProperty]
		[Display(Name = "Enable Draw Range", Description = "Dessine le rectangle de l'opening range sur le chart (defaut: ON)",
			Order = 1, GroupName = "10. Visuel")]
		public bool EnableDrawRange { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Range Opacity", Description = "Opacite du rectangle (0-100)",
			Order = 2, GroupName = "10. Visuel")]
		public int RangeOpacity { get; set; }

		#endregion
	}
}
