// ============================================================================
// FTM_OPENING_RANGE_BREAKOUT_MNQ_v1_8_0_RC3
//
// One self-contained MNQ opening-range breakout strategy. It requires one-minute
// MNQ bars and the CME US Index Futures ETH Trading Hours template. All trading
// decisions use New York time after converting NinjaTrader timestamps through
// UTC, so the NinjaTrader display time zone may be Eastern, UTC, Madrid, or any
// other correctly configured system time zone.
//
// The strategy builds the 09:30-09:45 ET opening range, then checks completed
// 15-minute closes from 10:00 through 15:45. A breakout must clear the range by
// one tick and pass candle-shape, close-location, and touch-count admission. A
// first-decision breakout close to completed-session VWAP takes the direct route;
// other signals pass through the prior-session, nearest-neighbor, volatility,
// and continuation refinements. At most one admitted breakout is acted on per
// cash date. Orders are submitted only after the final required bar completes.
//
// Select one sizing mode:
// - FixedDollar: fixed USD risk budget with defensive volatility/trend caps.
// - ClosedEquityPercent: closed strategy-sleeve equity risk with the same caps.
// - ConfidenceScaledPercent: causal 0/50/100 allocation score mapped to the
//   configured low/base/high percentages, without duplicate defensive caps.
//
// The direction model uses only already completed session observations and fixed
// per-contract execution costs, keeping its labels independent of order size.
// EstimatedRoundTurnCost is a sizing reserve, not a commission. Stored
// rollover offsets apply only to Merge back adjusted data through MNQ 09-26.
// Actual orders require complete strictly-prior volatility and trend context;
// model labels and eligible-session rolling context continue warming even when
// no order is submitted.
//
// For restart recovery, ImmediatelySubmit may continue only an exactly
// reconstructed strategy position with one full-quantity GTC stop and one
// full-quantity GTC target in the same OCO bracket. Any mismatch blocks new
// strategy actions and requires manual account/order reconciliation. The code
// never adopts or mutates arbitrary account orders. NinjaTrader's
// StopCancelClose handling and 30-second session-close exit remain enabled.
// ============================================================================
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class FTM_OPENING_RANGE_BREAKOUT_MNQ_v1_8_0_RC3 : Strategy
	{
		public enum RiskSizingMode
		{
			FixedDollar,
			ClosedEquityPercent,
			ConfidenceScaledPercent
		}

		private enum RestartRecoveryState
		{
			Historical,
			AuditPending,
			FlatReady,
			CurrentInstancePosition,
			RecoveredProtected,
			FailClosedExitPending,
			FlatUntilNextCashDate,
			ManualReconciliationRequired
		}

		private const string StrategyVersion = "1.8.0-rc.3";
		private const string NtAdapterVersion = "0.4.1-draft";
		private const string NativeValidationStatus = "UNCOMPILED_UNRECONCILED_DRAFT";
		private const string RequiredTradingHoursText = "CME US Index Futures ETH";

		private const int OrbStartMinuteEt = 9 * 60 + 30;
		private const int OrbEndMinuteEt = 9 * 60 + 45;
		private const int FirstBreakoutCloseMinuteEt = 10 * 60;
		private const int FlattenMinuteEt = 16 * 60;
		private const int RequiredCashCloseMinuteEt = 16 * 60;
		private const int ExpectedOrbBars = 15;

		private const double StopOrbMultiple = 1.25;
		private const double MinimumStopPoints = 2.0;
		private const double MaximumStopPoints = 100.0;
		private const double BaselineTargetR = 3.0;
		private const double HighOrbTargetR = 1.25;
		private const int ConfidenceOrbLookback = 120;
		private const double ConfidenceOrbQuantile = 0.75;
		private const int DefensiveMaxContracts = 1;
		private const double AdmissionBodyFraction = 0.15;
		private const double AdmissionCloseLocation = 0.60;
		private const int AdmissionMinimumTouches = 3;
		private const int ConfirmationTicks = 1;
		private const int MaxAdministrativeExitAttempts = 3;
		private const int ConfidenceTrendLookback = 20;
		private const int RequiredConfidenceTrendCloses = ConfidenceTrendLookback + 1;
		private const double BaselineManagedStopTriggerR = 1.25;
		private const double BaselineManagedStopLockR = 0.0;
		private const double CountertrendManagedStopTriggerR = 0.75;
		private const double CountertrendManagedStopLockR = 0.10;
		private const int ConditionalExitMinuteEt = 15 * 60 + 30;
		private const double ConditionalLossBoundaryR = 0.0;
		private const double ConditionalProfitBoundaryR = 1.0;

		private const double PriorDayContinuationThresholdBps = 300.0;
		private const int ModelFeatureCount = 14;
		private const int ModelNeighbors = 15;
		private const int ModelMinimumTrainingRows = 100;
		private const double ModelFlipProbabilityThreshold = 0.65;
		private const int ModelTrainingStartDateKey = 20210101;
		private const int ModelPredictionStartDateKey = 20230101;
		private const double WeakSignalBodyFraction = 0.20;
		private const int WeakSignalDelayBars = 1;
		private const int HighVolTouchLimit = 3;
		private const int HighVolVoteBars = 3;
		private const double HighVolVoteThresholdOrbFraction = 0.0;

		private const double IntradayContinuationThresholdBps = 25.0;
		private const double IntradayContinuationMaxSignalExtensionOrb = 0.25;
		private const int IntradayContinuationObservationBars = 1;
		private const double PriorSessionDisagreementThresholdBps = 100.0;
		private const double PriorSessionDisagreementMaxOrbBodyFraction = 0.0;
		private const int PriorSessionDisagreementObservationBars = 2;
		private const string EntryRefinementPriorityPolicy =
			"prior_session_disagreement_then_intraday_continuation";

		private const double Rc1DirectElapsedSignal15m = 1.0;
		private const double Rc1DirectMaxAlignedVwapDistanceBps = 20.0;
		private const int Rc1AlignedVwapFeatureIndex = 4;
		private const int Rc1ElapsedSignalFeatureIndex = 7;
		private const string Rc1DirectAction = "direct_first_signal_near_vwap";
		private const string Rc1PriorityPolicy =
			"direct_first_signal_near_vwap_else_integrated_refinement";

		// The online classifier learns from causal per-contract shadow outcomes
		// using fixed execution costs. Label construction stays independent of the
		// selected sizing mode and order quantity.
		private const int ModelEntrySlippageTicks = 1;
		private const int ModelStopSlippageTicks = 1;
		private const int ModelDayFlatSlippageTicks = 1;
		private const double ModelRoundTurnCost = 2.50;

		private static readonly string[] ModelFeatureNames = new string[]
		{
			"aligned_gap_bps",
			"aligned_prior_session_open_to_rth_close_bps",
			"aligned_prior_ret_5_bps",
			"aligned_prior_ret_20_bps",
			"aligned_vwap_distance_bps",
			"aligned_ret_30m_bps",
			"breakout_side",
			"signal_elapsed_15m",
			"orb_bps",
			"touch_count",
			"weekday_sin",
			"weekday_cos",
			"month_sin",
			"month_cos"
		};

		// These cash dates preserve known rollover and degraded-data boundaries.
		// They are data-quality exclusions, not discretionary market filters.
		private static readonly int[] ContractRollExclusionDateKeys = new int[]
		{
			20200311, 20200610, 20200909, 20210609,
			20210908, 20220608, 20220907, 20260611
		};

		private static readonly int[] DegradedDataExclusionDateKeys = new int[]
		{
			20200227, 20200228, 20200630, 20200701, 20200702,
			20211206, 20220103, 20240918, 20240919, 20250917,
			20250918, 20250924, 20250925, 20251128, 20260316,
			20260317, 20260410, 20260525, 20260730, 20260731
		};

		private sealed class DirectionTrainingRow
		{
			public DateTime Session;
			public double[] Features;
			public bool FlipWins;
			public int Sequence;
		}

		private sealed class NeighborMatch
		{
			public DirectionTrainingRow Row;
			public double Distance;
		}

		private sealed class QuarterDirectionModel
		{
			public List<DirectionTrainingRow> Rows;
			public double[] Means;
			public double[] Scales;
		}

		private sealed class ShadowTradeState
		{
			public int Side;
			public double Entry;
			public double ActiveStop;
			public double Target;
			public double RiskPoints;
			public double ManagedTriggerR;
			public double ManagedLockR;
			public bool PendingConditionalExit;
			public bool Complete;
			public double ExitPrice;
		}

		private sealed class ShadowPairState
		{
			public DateTime Session;
			public DateTime ExpectedParentFillOpenEt;
			public int BaselineSide;
			public double[] Features;
			public double PriorTrendBps;
			public bool PriorTrendAvailable;
			public bool Initialized;
			public bool Invalid;
			public bool LabelRecorded;
			public ShadowTradeState Breakout;
			public ShadowTradeState Fade;
		}

		private sealed class PendingEntryDecision
		{
			public DateTime ExpectedObservationOpenEt;
			public int DirectionSide;
			public int RequiredObservationBars;
			public int ObservedBars;
			public double FirstObservationOpen;
			public bool SubmitActualOrder;
			public string Branch;
			public string DirectionSource;
			public int BaselineSide;
			public double[] Features;
			public double SignalClose;
		}

		private sealed class PendingFinalEntryDecision
		{
			public DateTime ExpectedObservationOpenEt;
			public int RefinedSide;
			public int RequiredObservationBars;
			public int ObservedBars;
			public double FirstObservationOpen;
			public bool SubmitActualOrder;
			public string Branch;
			public string DirectionSource;
			public string RefinementPath;
		}

		// Captured 2026-08-19 from NinjaTrader 8 Instrument Editor > MNQ >
		// Contract months. NinjaTrader's Merge back adjusted convention applies
		// each incoming offset cumulatively to earlier contracts. Contract keys
		// are YYYYMM and rollover-date keys are YYYYMMDD in New York cash dates.
		private static readonly int[] StoredIncomingContractKeys = new int[]
		{
			202006, 202009, 202012, 202103, 202106, 202109, 202112,
			202203, 202206, 202209, 202212, 202303, 202306, 202309,
			202312, 202403, 202406, 202409, 202412, 202503, 202506,
			202509, 202512, 202603, 202606, 202609
		};

		private static readonly int[] StoredRolloverDateKeys = new int[]
		{
			20200312, 20200611, 20200910, 20201210, 20210311, 20210610,
			20210909, 20211209, 20220310, 20220609, 20220908, 20221212,
			20230313, 20230612, 20230911, 20231211, 20240311, 20240617,
			20240916, 20241216, 20250317, 20250616, 20250915, 20251215,
			20260316, 20260612
		};

		private static readonly double[] StoredRolloverOffsets = new double[]
		{
			-5.25, -11.0, -18.5, 1.75, -9.0, -15.0, -9.25,
			4.0, -2.0, 33.75, 75.0, 117.5, 122.25, 178.75,
			195.0, 210.75, 247.75, 260.5, 235.25, 287.0, 204.25,
			211.75, 237.25, 253.25, 214.25, 295.75
		};

		private TimeZoneInfo platformTimeZone;
		private TimeZoneInfo easternTimeZone;
		private SessionIterator sessionIterator;
		private bool configurationValid;
		private bool timeZoneContractValidated;
		private DateTime portfolioStartCashDate;
		private double baseRiskFraction;
		private double minRiskFraction;
		private double maxRiskFraction;

		private bool scheduleKnown;
		private DateTime actualSessionBeginPlatform;
		private DateTime actualSessionEndPlatform;
		private DateTime actualTradingDayExchange;
		private bool barClockContextAvailable;
		private DateTime lastBarOpenPlatform;
		private DateTime lastBarOpenUtc;
		private DateTime lastBarOpenEt;
		private DateTime lastBarClosePlatform;
		private DateTime lastBarCloseUtc;
		private DateTime lastBarCloseEt;

		private DateTime currentCashDate;
		private bool sessionDateEligible;
		private bool referenceSessionOpenCaptured;
		private double referenceSessionOpen;
		private bool dayBlocked;
		private bool sessionEnding;
		private bool orbFinalized;
		private bool missingOrbLogged;
		private bool breakoutConsumed;
		private bool cashExitTriggered;
		private bool cashWindowIntegrity;
		private bool orbHistoryRecorded;
		private int orbBarCount;
		private int expectedNextOrbOpenMinute;
		private double orbHigh;
		private double orbLow;
		private double orbOpen;
		private double orbClose;
		private double currentOrbBps;
		private double currentPriorOrbQ75Bps;
		private bool currentPriorOrbQ75Available;
		private List<double> eligibleOrbHistoryBps;
		private List<double> eligibleRthCloseHistory;
		private List<double> eligibleSessionReturnHistoryBps;
		private List<double> currentRthMinuteCloses;
		private double currentRthTypicalVolumeSum;
		private double currentRthVolumeSum;
		private bool rthCloseHistoryRecorded;
		private double currentPriorTrendBps;
		private bool currentPriorTrendAvailable;
		private int activeInitialStopTicks;
		private double activeManagedStopTriggerR;
		private double activeManagedStopLockR;
		private string activeManagementRegime;
		private bool managedStopActivated;
		private bool conditionalExitRequested;
		private DateTime lastCashBarCloseEt;

		private PendingEntryDecision pendingEntryDecision;
		private PendingFinalEntryDecision pendingFinalEntryDecision;
		private ShadowPairState activeShadowPair;
		private List<DirectionTrainingRow> directionTrainingRows;
		private QuarterDirectionModel activeQuarterModel;
		private int activeQuarterKey;
		private int trainingSequence;

		private int eligibleSessionCount;
		private int admittedSignalCount;
		private int geometryRejectCount;
		private int touchVetoCount;
		private int priorDayOverrideCount;
		private int knnPredictionCount;
		private int knnOverrideCount;
		private int weakDelayCount;
		private int highVolVoteCount;
		private int highVolVoteFlipCount;
		private int priorSessionConditionCount;
		private int intradayConditionCount;
		private int entryConditionOverlapCount;
		private int priorSessionReversalCount;
		private int intradayKeepCount;
		private int intradayReversalCount;
		private int modelLabelCount;
		private int sizingSkipCount;
		private int contextWarmupSkipCount;
		private int failClosedCount;
		private int rc1DirectDecisionCount;
		private int rc1ParentDecisionCount;

		private string activeEntrySignal;
		private string activeExitSignal;
		private Order entryOrder;
		private Order stopOrder;
		private Order targetOrder;
		private Order administrativeExitOrder;
		private int administrativeExitAttempts;
		private bool administrativeExitFillAwaitingExecution;
		private int administrativeExitPlatformFailureLatched;
		private bool duplicateManagedStopObserved;
		private bool duplicateManagedTargetObserved;
		private bool managedProtectionReferenceAmbiguous;
		private bool protectiveFillObserved;
		private bool accountOrderSetMismatchObserved;
		private bool liveManagedProtectionObserved;
		private bool restartProtectionDegraded;
		private int restartProtectionAuditQueued;
		private bool restartFlatConfirmationPending;
		private bool restartOpenFirstSnapshotMatched;
		private int restartOpenFirstSnapshotBar;
		private string restartOpenFirstSnapshotFingerprint;
		private Order restartOpenFirstSnapshotStopOrder;
		private Order restartOpenFirstSnapshotTargetOrder;
		private RestartRecoveryState restartRecoveryState;
		private DateTime restartRecoveryCashDate;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "FTM_OPENING_RANGE_BREAKOUT_MNQ_v1_8_0_RC3";
				Description = "MNQ one-minute opening-range breakout with direct near-VWAP entries, integrated direction refinement, three risk-sizing modes, managed protection, and guarded restart recovery.";

				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.UniqueEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				BarsRequiredToTrade = 15;
				StartBehavior = StartBehavior.ImmediatelySubmit;
				TimeInForce = TimeInForce.Gtc;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.ByStrategyPosition;
				Slippage = 1;
				DefaultQuantity = 2;
				IsInstantiatedOnEachOptimizationIteration = true;
				IncludeTradeHistoryInBacktest = true;
				TraceOrders = false;

				SizingMode = RiskSizingMode.FixedDollar;
				FixedRiskDollars = 535.0;
				FixedDollarMaxContracts = 2;
				StartingEquity = 50000.0;
				TradingStartDate = 20210101;
				BaseRiskPercent = 1.0;
				MinRiskPercent = 0.5;
				MaxRiskPercent = 2.0;
				PortfolioMaxContracts = 10;
				MaxNotionalLeverage = 4.0;
				EstimatedRoundTurnCost = 2.50;
				StopSlippageTicks = 1;
				UseStoredRolloverOffsets = true;
				MergeTargetContract = 202609;
				EnableDiagnostics = false;
			}
			else if (State == State.Configure)
			{
				platformTimeZone = null;
				easternTimeZone = null;
				try
				{
					platformTimeZone = Core.Globals.GeneralOptions.TimeZoneInfo;
					easternTimeZone = ResolveEasternTimeZone();
				}
				catch (Exception ex)
				{
					Log("FLAT MOON SOCIETY could not initialize time-zone conversion: " + ex.Message, LogLevel.Error);
				}
			}
			else if (State == State.DataLoaded)
			{
				sessionIterator = new SessionIterator(Bars);
				InitializeRuntimeState();
				configurationValid = ValidateConfiguration();
				PrintStartupDiagnostics();
			}
			else if (State == State.Realtime)
			{
				MapHistoricalOrderReferencesToRealtime();
				liveManagedProtectionObserved = IsLiveManagedProtectiveReference(stopOrder)
					|| IsLiveManagedProtectiveReference(targetOrder);
				BeginRealtimeRestartRecovery();
			}
			else if (State == State.Terminated)
			{
				PrintAnalyzerSummary();
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || CurrentBar < BarsRequiredToTrade || !configurationValid)
				return;
			if (State == State.Realtime
				&& restartRecoveryState == RestartRecoveryState.ManualReconciliationRequired)
				return;
			if (State == State.Realtime && restartProtectionDegraded)
				ProcessQueuedRestartProtectionAudit(null);
			else if (State == State.Realtime
				&& restartRecoveryState == RestartRecoveryState.AuditPending)
				AuditRealtimeRestartRecovery(true);
			if (State == State.Realtime
				&& restartRecoveryState == RestartRecoveryState.ManualReconciliationRequired)
				return;
			if (State == State.Realtime
				&& restartRecoveryState == RestartRecoveryState.AuditPending)
				return;
			if (State == State.Realtime
				&& restartRecoveryState == RestartRecoveryState.FailClosedExitPending)
			{
				if (Position.MarketPosition == MarketPosition.Flat
					&& PositionAccount != null
					&& PositionAccount.MarketPosition == MarketPosition.Flat)
					ProcessDegradedProtectionSnapshot(true);
				else if (administrativeExitFillAwaitingExecution)
					Diagnostic("ADMIN EXIT retry blocked until OnExecutionUpdate processes the reported fill.");
				else
				{
					if (administrativeExitAttempts >= MaxAdministrativeExitAttempts
						&& !IsActiveOrder(administrativeExitOrder))
						RequireManualRestartReconciliation(
							"The bounded managed administrative-exit attempts were exhausted while exposure remained open.");
					else
						RequestAdministrativeExit("RestartProtectionInvalidFollowup");
				}
				return;
			}
			if (State == State.Realtime
				&& (restartRecoveryState == RestartRecoveryState.RecoveredProtected
					|| restartRecoveryState == RestartRecoveryState.CurrentInstancePosition))
			{
				if (Position.MarketPosition == MarketPosition.Flat
					&& PositionAccount != null
					&& PositionAccount.MarketPosition == MarketPosition.Flat)
				{
					ProcessDegradedProtectionSnapshot(true);
					return;
				}
				string protectedPositionFailure;
				if (!TryValidateRecoveredPositionParity(out protectedPositionFailure))
				{
					RequireManualRestartReconciliation(protectedPositionFailure);
					return;
				}
			}

			RefreshSessionSchedule();
			if (!scheduleKnown)
				return;

			DateTime barOpenEt;
			DateTime barCloseEt;
			DateTime barOpenUtc;
			DateTime barCloseUtc;
			DateTime barOpenPlatform;
			DateTime barClosePlatform;
			try
			{
				// NinjaTrader minute bars use the close timestamp.
				// Convert the close to an absolute UTC instant before subtracting one
				// real minute. Subtracting in platform wall time is unsafe at a DST fold.
				barClosePlatform = DateTime.SpecifyKind(Time[0], DateTimeKind.Unspecified);
				barCloseUtc = ToUtc(barClosePlatform);
				barOpenUtc = barCloseUtc.AddMinutes(-1);
				barOpenPlatform = DateTime.SpecifyKind(
					TimeZoneInfo.ConvertTimeFromUtc(barOpenUtc, platformTimeZone),
					DateTimeKind.Unspecified);
				barOpenEt = FromUtcToEastern(barOpenUtc);
				barCloseEt = FromUtcToEastern(barCloseUtc);
				CaptureBarClockContext(
					barOpenPlatform, barOpenUtc, barOpenEt,
					barClosePlatform, barCloseUtc, barCloseEt);
			}
			catch (Exception ex)
			{
				configurationValid = false;
				Log("FLAT MOON SOCIETY time conversion failed; no further orders will be submitted: " + ex.Message, LogLevel.Error);
				return;
			}

			DateTime scheduleCashDate = actualTradingDayExchange.Date;
			bool beginsNewCashDate = currentCashDate == DateTime.MinValue
				|| scheduleCashDate != currentCashDate;
			if (beginsNewCashDate)
				BeginCashDate(scheduleCashDate);
			if (State == State.Realtime && beginsNewCashDate
				&& restartRecoveryState == RestartRecoveryState.FlatUntilNextCashDate)
				ReleaseRestartEntryBlockAtNewCashDate();
			CaptureReferenceSessionOpen(barOpenEt, barOpenUtc);

			int openMinuteEt = MinuteOfDay(barOpenEt);
			int closeMinuteEt = MinuteOfDay(barCloseEt);
			bool sameCashDate = barOpenEt.Date == currentCashDate && barCloseEt.Date == currentCashDate;
			bool exactOneMinuteBar = IsExactMinute(barOpenEt)
				&& IsExactMinute(barCloseEt)
				&& barCloseEt == barOpenEt.AddMinutes(1);

			if (sessionEnding)
			{
				InvalidatePendingSessionState("session-ending state");
				CancelWorkingEntry();
				RequestAdministrativeExit("SessionEnding");
				return;
			}

			if (sameCashDate && openMinuteEt >= OrbStartMinuteEt
				&& closeMinuteEt <= RequiredCashCloseMinuteEt
				&& !exactOneMinuteBar)
			{
				BlockCashDate("A New York cash bar was not aligned to one exact completed minute.");
				InvalidatePendingSessionState("misaligned one-minute cash bar");
				sessionEnding = true;
				CancelWorkingEntry();
				RequestAdministrativeExit("CashTimeAlignment");
				return;
			}

			// Track the entire required 09:30-16:00 data window. Only complete
			// eligible cash sessions enter subsequent context and sizing histories.
			if (sameCashDate && openMinuteEt >= OrbStartMinuteEt
				&& closeMinuteEt <= RequiredCashCloseMinuteEt)
			{
				if (lastCashBarCloseEt != DateTime.MinValue && barOpenEt != lastCashBarCloseEt)
				{
					BlockCashDate("A one-minute data gap was detected inside the New York cash window.");
					InvalidatePendingSessionState("cash-window data gap");
					sessionEnding = true;
					RequestAdministrativeExit("CashDataGap");
					return;
				}
				lastCashBarCloseEt = barCloseEt;

				if (sessionDateEligible && cashWindowIntegrity && !dayBlocked
					&& openMinuteEt == OrbStartMinuteEt
					&& !referenceSessionOpenCaptured)
				{
					BlockCashDate("The required 23:00 UTC reference opening bar was not observed.");
					InvalidatePendingSessionState("missing reference open");
					return;
				}

				if (!dayBlocked)
				{
					double volume = (double)Volume[0];
					if (!(volume >= 0) || double.IsNaN(volume) || double.IsInfinity(volume))
					{
						BlockCashDate("A one-minute RTH volume value is invalid.");
						InvalidatePendingSessionState("invalid RTH volume");
						return;
					}
					currentRthMinuteCloses.Add(Close[0]);
					currentRthTypicalVolumeSum += ((High[0] + Low[0] + Close[0]) / 3.0) * volume;
					currentRthVolumeSum += volume;
				}
			}

			if (sameCashDate && activeShadowPair != null)
				ProcessShadowPair(barOpenEt, barCloseEt, closeMinuteEt);
			if (sameCashDate && pendingEntryDecision != null && !dayBlocked)
				ProcessPendingEntryDecision(barOpenEt, barCloseEt);
			if (sameCashDate && pendingFinalEntryDecision != null && !dayBlocked)
				ProcessPendingFinalEntryDecision(barOpenEt, barCloseEt);

			if (sameCashDate && closeMinuteEt >= RequiredCashCloseMinuteEt)
			{
				DateTime requiredCashCloseEt = currentCashDate.Date.AddMinutes(RequiredCashCloseMinuteEt);
				if (lastCashBarCloseEt != requiredCashCloseEt)
				{
					BlockCashDate("The New York cash window is missing its final one-minute bar before 16:00 ET.");
					InvalidatePendingSessionState("cash close bar missing");
					sessionEnding = true;
					CancelWorkingEntry();
					RequestAdministrativeExit("CashDataGapAtClose");
					return;
				}
				FinalizeShadowPairAtCashClose(Close[0]);
				RecordCompletedSessionHistory(Close[0]);
				if (!cashExitTriggered)
				{
					cashExitTriggered = true;
					dayBlocked = true;
					CancelWorkingEntry();
					RequestAdministrativeExit("CashClose1600ET");
				}
				else
					RequestAdministrativeExit("CashExitFollowup");
				return;
			}

			// The native template owns an exchange early-close flatten. The session
			// schedule was already checked before the ORB; this is a second backstop.
			if (Bars.IsLastBarOfSession)
			{
				cashWindowIntegrity = false;
				InvalidatePendingSessionState("native session ended before cash completion");
				sessionEnding = true;
				dayBlocked = true;
				CancelWorkingEntry();
				RequestAdministrativeExit("NativeSessionEnd");
				return;
			}

			// The 16:00 exit is submitted in the complete-window branch above so
			// causal session history is recorded before the exit request.
			if (sameCashDate && closeMinuteEt >= FlattenMinuteEt && !cashExitTriggered)
			{
				FinalizeShadowPairAtCashClose(Close[0]);
				RecordCompletedSessionHistory(Close[0]);
				cashExitTriggered = true;
				dayBlocked = true;
				CancelWorkingEntry();
				RequestAdministrativeExit("CashClose1600ET");
				return;
			}

			if (cashExitTriggered)
			{
				RequestAdministrativeExit("CashExitFollowup");
				return;
			}

			bool isQuarterHourClose = sameCashDate
				&& closeMinuteEt >= FirstBreakoutCloseMinuteEt
				&& closeMinuteEt < FlattenMinuteEt
				&& closeMinuteEt % 15 == 0;
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				// Calculate.OnBarClose reaches this block only after NinjaTrader has
				// evaluated the completed one-minute bar against the stop/target
				// that was already working during that bar. A quarter-hour close can
				// therefore revise protection only for the following one-minute bar.
				//
				// At 15:30, apply the regime-specific managed-stop rule first. Then
				// request the selective market exit when closeR is outside [0R,+1R).
				if (isQuarterHourClose)
					ManageProtectiveStopAtQuarterHour();
				if (sameCashDate && closeMinuteEt == ConditionalExitMinuteEt)
					ManageConditionalExit1530();
				return;
			}

			if (dayBlocked || !IsWeekday(currentCashDate.DayOfWeek))
				return;

			CaptureOpeningRange(barOpenEt, barCloseEt, openMinuteEt, closeMinuteEt);
			if (dayBlocked || !orbFinalized || breakoutConsumed)
				return;
			// The opening-range bar closes at 09:45. The first later 15-minute
			// decision therefore occurs at 10:00, then every quarter hour through
			// 15:45. A 16:00 signal cannot fill before the mandated flatten.
			bool isDecisionClose = isQuarterHourClose;
			if (!isDecisionClose || Position.MarketPosition != MarketPosition.Flat)
				return;

			double confirmation = ConfirmationTicks * TickSize;
			bool longBreakout = Close[0] >= RoundPrice(orbHigh + confirmation);
			bool shortBreakout = Close[0] <= RoundPrice(orbLow - confirmation);
			if (!longBreakout && !shortBreakout)
				return;
			EvaluateAdmissionAndDirection(longBreakout ? 1 : -1, barCloseEt);
		}

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
			int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (order == null)
				return;
			if (State == State.Realtime)
				MapHistoricalOrderReferencesToRealtime();

			if (!string.IsNullOrEmpty(activeEntrySignal) && order.Name == activeEntrySignal)
				entryOrder = order;
			bool administrativeExitUpdate = !string.IsNullOrEmpty(activeExitSignal)
				&& order.Name == activeExitSignal;
			if (administrativeExitUpdate)
				administrativeExitOrder = order;
			bool managedProtection = TrackManagedProtectiveOrder(order);

			bool tracked = order.Name == activeEntrySignal
				|| order.Name == activeExitSignal
				|| managedProtection;
			if (tracked)
			{
				// NinjaTrader documents this callback value as the last order-state
				// change time, but does not document its time zone. Preserve it exactly
				// and report DateTime.Kind; do not relabel or convert it. The separately
				// labeled bar clocks come from the validated strategy clock contract.
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"ORDER {0}: callbackTimeRaw={1:o}, callbackTimeKind={2}, {3}, state={4}, qty={5}, filled={6}, avg={7:F2}, error={8}, comment={9}",
					order.Name, time, time.Kind, BarClockContext(), orderState,
					quantity, filled, averageFillPrice, error, comment));
			}

			if (tracked && (orderState == OrderState.Rejected || error != ErrorCode.NoError))
				Log(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY order failure: {0}, state={1}, error={2}, comment={3}",
					order.Name, orderState, error, comment), LogLevel.Error);

			if (State == State.Realtime && administrativeExitUpdate
				&& (filled > 0 || orderState == OrderState.PartFilled
					|| orderState == OrderState.Filled))
				administrativeExitFillAwaitingExecution = true;
			if (State == State.Realtime && administrativeExitUpdate
				&& (orderState == OrderState.Rejected || error != ErrorCode.NoError))
			{
				System.Threading.Interlocked.Exchange(
					ref administrativeExitPlatformFailureLatched, 1);
				RequireManualRestartReconciliation(
					"The managed administrative exit was rejected or reported an error. NinjaTrader's StopCancelClose path owns this outcome, so RC3 permanently blocks every further authored exit in this instance.");
				return;
			}
			if (State == State.Realtime && administrativeExitUpdate
				&& orderState == OrderState.Unknown)
			{
				RequireManualRestartReconciliation(
					"The bounded managed administrative exit entered Unknown state; RC3 cannot prove whether it is live or terminal and will not submit a duplicate exit.");
				return;
			}
			if (administrativeExitUpdate && orderState == OrderState.Cancelled
				&& error == ErrorCode.NoError)
				administrativeExitOrder = null;

			if (State == State.Realtime && managedProtection
				&& restartRecoveryState == RestartRecoveryState.AuditPending
				&& orderState == OrderState.Working)
				AuditRealtimeRestartRecovery(false);
			if (State == State.Realtime && managedProtection
				&& restartRecoveryState != RestartRecoveryState.Historical
				&& restartRecoveryState != RestartRecoveryState.FlatReady
				&& restartRecoveryState != RestartRecoveryState.ManualReconciliationRequired
				&& RequiresPromptProtectionReaudit(orderState, error))
			{
				if (filled > 0 || orderState == OrderState.PartFilled
					|| orderState == OrderState.Filled)
					protectiveFillObserved = true;
				restartProtectionDegraded = true;
				Log("FLAT MOON SOCIETY managed protection entered a degraded or terminal/error state while exposure may remain. A strategy-thread audit is being queued before any further strategy action.", LogLevel.Error);
				if (!ProtectionUpdateMayBePartOfFillSequence(order, orderState, filled))
					QueueRestartProtectionAudit();
			}
			if (State == State.Realtime && managedProtection
				&& restartRecoveryState != RestartRecoveryState.Historical
				&& restartRecoveryState != RestartRecoveryState.FlatReady
				&& restartRecoveryState != RestartRecoveryState.ManualReconciliationRequired
				&& Position.MarketPosition == MarketPosition.Flat
				&& PositionAccount != null
				&& PositionAccount.MarketPosition == MarketPosition.Flat
				&& IsPotentiallyLiveAccountOrder(order))
			{
				restartProtectionDegraded = true;
				QueueRestartProtectionAudit();
			}
			if (State == State.Realtime && administrativeExitUpdate
				&& restartRecoveryState == RestartRecoveryState.FailClosedExitPending
				&& orderState == OrderState.Cancelled
				&& error == ErrorCode.NoError)
			{
				if (filled > 0)
					protectiveFillObserved = true;
				restartProtectionDegraded = true;
				if (filled == 0)
					QueueRestartProtectionAudit();
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || quantity <= 0)
				return;

			// As with OnOrderUpdate, the official callback contract does not state a
			// time zone. Keep the execution timestamp raw and pair it with separately
			// labeled, normalized strategy-bar context for native reconciliation.
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"FILL {0}: callbackTimeRaw={1:o}, callbackTimeKind={2}, {3}, executionId={4}, orderId={5}, price={6:F2}, qty={7}, position={8}.",
				execution.Name, time, time.Kind, BarClockContext(), executionId,
				orderId, price, quantity, marketPosition));

			bool currentInstanceEntryExecution = State == State.Realtime
				&& !string.IsNullOrEmpty(activeEntrySignal)
				&& execution.Name == activeEntrySignal;
			if (currentInstanceEntryExecution
				&& restartRecoveryState == RestartRecoveryState.FlatReady)
			{
				restartRecoveryCashDate = currentCashDate;
				restartRecoveryState = RestartRecoveryState.CurrentInstancePosition;
				Diagnostic("CURRENT-INSTANCE POSITION: realtime entry execution observed; managed protection is now under lifecycle supervision.");
			}

			// A delayed/partial entry fill can race a cutoff cancellation. Establish
			// current-instance ownership first, then fail closed by requesting an exit.
			if (currentInstanceEntryExecution && (sessionEnding || cashExitTriggered))
				RequestAdministrativeExit("LateEntryFillAfterCutoff");

			bool administrativeExitExecution = !string.IsNullOrEmpty(activeExitSignal)
				&& execution.Name == activeExitSignal;
			if (State == State.Realtime && administrativeExitExecution)
				administrativeExitFillAwaitingExecution = false;

			if (State == State.Realtime
				&& restartRecoveryState != RestartRecoveryState.Historical
				&& restartRecoveryState != RestartRecoveryState.FlatReady
				&& restartRecoveryState != RestartRecoveryState.ManualReconciliationRequired
				&& TrackManagedProtectiveOrder(execution.Order))
			{
				protectiveFillObserved = true;
				restartProtectionDegraded = true;
				ProcessRestartProtectionAfterExecution();
			}
			if (State == State.Realtime
				&& restartRecoveryState != RestartRecoveryState.Historical
				&& restartRecoveryState != RestartRecoveryState.FlatReady
				&& restartRecoveryState != RestartRecoveryState.ManualReconciliationRequired
				&& administrativeExitExecution)
			{
				protectiveFillObserved = true;
				restartProtectionDegraded = true;
				ProcessRestartProtectionAfterExecution();
			}
		}

		private void MapHistoricalOrderReferencesToRealtime()
		{
			if (State != State.Realtime)
				return;
			if (entryOrder != null && entryOrder.IsBacktestOrder)
				entryOrder = GetRealtimeOrder(entryOrder);
			if (stopOrder != null && stopOrder.IsBacktestOrder)
				stopOrder = GetRealtimeOrder(stopOrder);
			if (targetOrder != null && targetOrder.IsBacktestOrder)
				targetOrder = GetRealtimeOrder(targetOrder);
			if (administrativeExitOrder != null && administrativeExitOrder.IsBacktestOrder)
				administrativeExitOrder = GetRealtimeOrder(administrativeExitOrder);
		}

		private bool TrackManagedProtectiveOrder(Order order)
		{
			if (order == null || string.IsNullOrEmpty(activeEntrySignal)
				|| !string.Equals(order.FromEntrySignal, activeEntrySignal, StringComparison.Ordinal))
				return false;

			if (string.Equals(order.Name, "Stop loss", StringComparison.Ordinal))
			{
				UpdateManagedProtectiveReference(
					ref stopOrder, order, ref duplicateManagedStopObserved);
				return true;
			}

			if (string.Equals(order.Name, "Profit target", StringComparison.Ordinal))
			{
				UpdateManagedProtectiveReference(
					ref targetOrder, order, ref duplicateManagedTargetObserved);
				return true;
			}

			return false;
		}

		private void UpdateManagedProtectiveReference(
			ref Order tracked,
			Order update,
			ref bool ambiguousLiveIdentityObserved)
		{
			if (update == null)
				return;
			if (State == State.Realtime && update.IsBacktestOrder
				&& tracked != null && !tracked.IsBacktestOrder)
				return;

			bool sameIdentity = IsSameOrderIdentity(tracked, update);
			if (tracked != null && !sameIdentity
				&& !tracked.IsBacktestOrder && !update.IsBacktestOrder)
			{
				ambiguousLiveIdentityObserved = true;
				managedProtectionReferenceAmbiguous = true;
			}

			if (tracked == null || sameIdentity || !update.IsBacktestOrder
				|| tracked.IsBacktestOrder)
				tracked = update;
			if (State == State.Realtime && !update.IsBacktestOrder)
				liveManagedProtectionObserved = true;
		}

		private bool IsSameOrderIdentity(Order first, Order second)
		{
			return first != null && second != null
				&& object.ReferenceEquals(first, second);
		}

		private bool RequiresPromptProtectionReaudit(OrderState orderState, ErrorCode error)
		{
			return error != ErrorCode.NoError
				|| orderState == OrderState.Cancelled
				|| orderState == OrderState.Rejected
				|| orderState == OrderState.PartFilled
				|| orderState == OrderState.Filled
				|| orderState == OrderState.Unknown;
		}

		private bool ProtectionUpdateMayBePartOfFillSequence(
			Order order,
			OrderState orderState,
			int filled)
		{
			if (filled > 0 || orderState == OrderState.PartFilled
				|| orderState == OrderState.Filled)
				return true;
			Order sibling = object.ReferenceEquals(order, stopOrder) ? targetOrder : stopOrder;
			return sibling != null
				&& (sibling.Filled > 0
					|| sibling.OrderState == OrderState.PartFilled
					|| sibling.OrderState == OrderState.Filled);
		}

		private void QueueRestartProtectionAudit()
		{
			if (State != State.Realtime
				|| restartRecoveryState == RestartRecoveryState.Historical
				|| restartRecoveryState == RestartRecoveryState.FlatReady
				|| restartRecoveryState == RestartRecoveryState.ManualReconciliationRequired
				|| System.Threading.Interlocked.Exchange(
					ref restartProtectionAuditQueued, 1) == 1)
				return;
			try
			{
				TriggerCustomEvent(ProcessQueuedRestartProtectionAudit, null);
			}
			catch (Exception ex)
			{
				System.Threading.Interlocked.Exchange(
					ref restartProtectionAuditQueued, 0);
				Log("FLAT MOON SOCIETY could not queue the strategy-thread protection audit: "
					+ ex.Message
					+ ". The instance remains blocked and requires immediate operator reconciliation if no further strategy event arrives.", LogLevel.Error);
			}
		}

		private void ProcessQueuedRestartProtectionAudit(object state)
		{
			System.Threading.Interlocked.Exchange(
				ref restartProtectionAuditQueued, 0);
			if (State != State.Realtime
				|| restartRecoveryState == RestartRecoveryState.Historical
				|| restartRecoveryState == RestartRecoveryState.FlatReady
				|| restartRecoveryState == RestartRecoveryState.ManualReconciliationRequired)
			{
				restartProtectionDegraded = false;
				return;
			}
			if (!restartProtectionDegraded)
				return;
			restartProtectionDegraded = false;
			// A custom event (or the bar-driven fallback that drains the same flag)
			// may establish the first exact snapshot, but it can never count as the
			// distinct realtime-bar confirmation required for recovery PASS.
			ProcessDegradedProtectionSnapshot(false);
		}

		private void ProcessRestartProtectionAfterExecution()
		{
			restartProtectionDegraded = true;
			QueueRestartProtectionAudit();
		}

		private void ProcessDegradedProtectionSnapshot(bool finalAttempt)
		{
			if (PositionAccount != null
				&& Position.MarketPosition == MarketPosition.Flat
				&& PositionAccount.MarketPosition == MarketPosition.Flat)
			{
				string flatOrderFailure;
				bool flatOrderPending;
				if (!TryValidateAccountInstrumentOrderSet(
					false, out flatOrderFailure, out flatOrderPending))
				{
					RequireManualRestartReconciliation(flatOrderFailure);
					return;
				}
				restartFlatConfirmationPending = false;
				ClearRestartOpenFirstSnapshot();
				restartRecoveryState = RestartRecoveryState.FlatUntilNextCashDate;
				Diagnostic("Recovered protective execution left both strategy and account flat; entries remain blocked until the next New York cash date.");
				return;
			}

			restartRecoveryState = RestartRecoveryState.AuditPending;
			AuditRealtimeRestartRecovery(finalAttempt);
		}

		private void BeginRealtimeRestartRecovery()
		{
			restartRecoveryCashDate = currentCashDate;
			ClearRestartOpenFirstSnapshot();
			restartRecoveryState = RestartRecoveryState.AuditPending;
			if (PositionAccount == null)
			{
				RequireManualRestartReconciliation(
					"The realtime account position is unavailable.");
				return;
			}
			bool strategyFlat = Position.MarketPosition == MarketPosition.Flat;
			bool accountFlat = PositionAccount.MarketPosition == MarketPosition.Flat;
			if (strategyFlat && accountFlat)
			{
				if (IsLifecycleActiveOrder(stopOrder) || IsLifecycleActiveOrder(targetOrder))
				{
					RequireManualRestartReconciliation(
						"Both positions are flat but a current-instance protective order is still active.");
					return;
				}
				string accountOrderFailure;
				bool accountOrderPending;
				if (!TryValidateAccountInstrumentOrderSet(
					false, out accountOrderFailure, out accountOrderPending))
				{
					RequireManualRestartReconciliation(accountOrderFailure);
					return;
				}
				restartFlatConfirmationPending = true;
				restartRecoveryState = RestartRecoveryState.AuditPending;
				Diagnostic("Entered realtime with one flat/no-order snapshot; entries remain blocked until the first realtime strategy bar confirms it.");
				return;
			}

			Print("FLAT MOON SOCIETY RESTART RECOVERY PENDING: validating the reconstructed strategy/account position and broker-confirmed managed bracket. No new entry can be submitted during this audit.");
			AuditRealtimeRestartRecovery(false);
		}

		private void AuditRealtimeRestartRecovery(bool finalAttempt)
		{
			if (restartRecoveryState != RestartRecoveryState.AuditPending)
				return;
			if (restartFlatConfirmationPending)
			{
				if (!finalAttempt)
					return;
				restartFlatConfirmationPending = false;
				if (PositionAccount != null
					&& Position.MarketPosition == MarketPosition.Flat
					&& PositionAccount.MarketPosition == MarketPosition.Flat)
				{
					string flatFailure;
					bool flatPending;
					if (TryValidateAccountInstrumentOrderSet(
						false, out flatFailure, out flatPending))
					{
						restartRecoveryState = RestartRecoveryState.FlatReady;
						Diagnostic("First realtime strategy bar confirmed flat positions and no active account order for this instrument; normal entries are enabled.");
						return;
					}
					RequireManualRestartReconciliation(flatFailure);
					return;
				}
			}

			string failure;
			bool pending;
			if (TryValidateRealtimeRestartRecovery(out failure, out pending))
			{
				string snapshotFingerprint = BuildRestartOpenSnapshotFingerprint();
				bool matchesFirstSnapshot = RestartOpenSnapshotMatchesFirst(
					snapshotFingerprint);
				if (!finalAttempt)
				{
					if (!matchesFirstSnapshot)
					{
						CaptureRestartOpenFirstSnapshot(snapshotFingerprint);
						Diagnostic("RESTART RECOVERY FIRST SNAPSHOT MATCHED: waiting for the identical position, order fields, and stop/target object identities on a distinct later realtime strategy bar.");
					}
					else
						Diagnostic("RESTART RECOVERY SNAPSHOT REMAINS IDENTICAL: the distinct later realtime strategy-bar confirmation is still required.");
					return;
				}
				if (!matchesFirstSnapshot)
				{
					CaptureRestartOpenFirstSnapshot(snapshotFingerprint);
					Diagnostic("RESTART RECOVERY NEW FIRST EXACT SNAPSHOT ARRIVED ON A REALTIME BAR: one more later realtime strategy bar must confirm identical position/order fields and stop/target object identities before recovery can pass.");
					return;
				}
				if (CurrentBar <= restartOpenFirstSnapshotBar)
				{
					Diagnostic("RESTART RECOVERY EXACT SNAPSHOT REPEATED WITHOUT A DISTINCT LATER REALTIME STRATEGY BAR: recovery remains pending.");
					return;
				}
				ClearRestartOpenFirstSnapshot();
				restartRecoveryState = RestartRecoveryState.RecoveredProtected;
				Print(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY RESTART RECOVERY PASS | strategy={0}/{1}@{2:F2} | account={3}/{4}@{5:F2} | entrySignal={6} | stopState={7} stopTif={8} stop={9:F2} | targetState={10} targetTif={11} target={12:F2} | oco={13}. Entries remain blocked until a later New York cash date after this position is flat.",
					Position.MarketPosition, Position.Quantity, Position.AveragePrice,
					PositionAccount.MarketPosition, PositionAccount.Quantity,
					PositionAccount.AveragePrice, activeEntrySignal,
					stopOrder.OrderState, stopOrder.TimeInForce, stopOrder.StopPrice,
					targetOrder.OrderState, targetOrder.TimeInForce,
					targetOrder.LimitPrice, stopOrder.Oco));
				return;
			}
			ClearRestartOpenFirstSnapshot();
			if (pending && !finalAttempt)
			{
				Diagnostic("RESTART RECOVERY STILL PENDING: " + failure);
				return;
			}
			if (pending)
				failure += " Broker-confirmed Working protection was not available by the first realtime bar.";
			HandleRestartProtectionFailure(failure);
		}

		private string BuildRestartOpenSnapshotFingerprint()
		{
			return string.Join("|", new string[]
			{
				Position.MarketPosition.ToString(),
				Position.Quantity.ToString(CultureInfo.InvariantCulture),
				Position.AveragePrice.ToString("R", CultureInfo.InvariantCulture),
				PositionAccount.MarketPosition.ToString(),
				PositionAccount.Quantity.ToString(CultureInfo.InvariantCulture),
				PositionAccount.AveragePrice.ToString("R", CultureInfo.InvariantCulture),
				activeEntrySignal,
				managedProtectionReferenceAmbiguous.ToString(),
				duplicateManagedStopObserved.ToString(),
				duplicateManagedTargetObserved.ToString(),
				stopOrder.Name,
				stopOrder.FromEntrySignal,
				stopOrder.OrderType.ToString(),
				stopOrder.OrderAction.ToString(),
				stopOrder.TimeInForce.ToString(),
				stopOrder.Quantity.ToString(CultureInfo.InvariantCulture),
				stopOrder.Filled.ToString(CultureInfo.InvariantCulture),
				stopOrder.OrderState.ToString(),
				stopOrder.Oco,
				stopOrder.StopPrice.ToString("R", CultureInfo.InvariantCulture),
				targetOrder.Name,
				targetOrder.FromEntrySignal,
				targetOrder.OrderType.ToString(),
				targetOrder.OrderAction.ToString(),
				targetOrder.TimeInForce.ToString(),
				targetOrder.Quantity.ToString(CultureInfo.InvariantCulture),
				targetOrder.Filled.ToString(CultureInfo.InvariantCulture),
				targetOrder.OrderState.ToString(),
				targetOrder.Oco,
				targetOrder.LimitPrice.ToString("R", CultureInfo.InvariantCulture)
			});
		}

		private bool RestartOpenSnapshotMatchesFirst(string snapshotFingerprint)
		{
			return restartOpenFirstSnapshotMatched
				&& object.ReferenceEquals(
					restartOpenFirstSnapshotStopOrder, stopOrder)
				&& object.ReferenceEquals(
					restartOpenFirstSnapshotTargetOrder, targetOrder)
				&& string.Equals(
					restartOpenFirstSnapshotFingerprint,
					snapshotFingerprint,
					StringComparison.Ordinal);
		}

		private void CaptureRestartOpenFirstSnapshot(string snapshotFingerprint)
		{
			restartOpenFirstSnapshotMatched = true;
			restartOpenFirstSnapshotBar = CurrentBar;
			restartOpenFirstSnapshotFingerprint = snapshotFingerprint;
			restartOpenFirstSnapshotStopOrder = stopOrder;
			restartOpenFirstSnapshotTargetOrder = targetOrder;
		}

		private void ClearRestartOpenFirstSnapshot()
		{
			restartOpenFirstSnapshotMatched = false;
			restartOpenFirstSnapshotBar = -1;
			restartOpenFirstSnapshotFingerprint = string.Empty;
			restartOpenFirstSnapshotStopOrder = null;
			restartOpenFirstSnapshotTargetOrder = null;
		}

		private bool TryValidateRealtimeRestartRecovery(out string failure, out bool pending)
		{
			failure = string.Empty;
			pending = false;
			if (PositionAccount == null)
			{
				failure = "The realtime account position is unavailable.";
				return false;
			}
			if (Position.MarketPosition == MarketPosition.Flat
				|| PositionAccount.MarketPosition == MarketPosition.Flat)
			{
				failure = "The reconstructed strategy position and account position are not both open.";
				return false;
			}
			if (Position.MarketPosition != PositionAccount.MarketPosition
				|| Position.Quantity != PositionAccount.Quantity)
			{
				failure = string.Format(CultureInfo.InvariantCulture,
					"Strategy/account position mismatch: strategy={0}/{1}, account={2}/{3}.",
					Position.MarketPosition, Position.Quantity,
					PositionAccount.MarketPosition, PositionAccount.Quantity);
				return false;
			}
			if (!IsFinitePositive(Position.AveragePrice)
				|| !IsFinitePositive(PositionAccount.AveragePrice)
				|| Math.Abs(Position.AveragePrice - PositionAccount.AveragePrice) > TickSize)
			{
				failure = string.Format(CultureInfo.InvariantCulture,
					"Strategy/account average-price mismatch exceeds one tick: strategy={0:F4}, account={1:F4}.",
					Position.AveragePrice, PositionAccount.AveragePrice);
				return false;
			}
			if (string.IsNullOrEmpty(activeEntrySignal))
			{
				failure = "The historical replay did not reconstruct an active entry signal.";
				return false;
			}
			if (managedProtectionReferenceAmbiguous)
			{
				failure = "More than one live Order object identity was observed for a managed protective slot.";
				return false;
			}
			if (duplicateManagedStopObserved || duplicateManagedTargetObserved)
			{
				failure = "More than one active managed stop or target was observed for the entry signal.";
				return false;
			}

			OrderAction expectedAction = Position.MarketPosition == MarketPosition.Long
				? OrderAction.Sell : OrderAction.BuyToCover;
			if (!ValidateRecoveredProtectiveOrder(
				stopOrder, "Stop loss", OrderType.StopMarket, expectedAction,
				Position.Quantity, out failure, out pending))
				return false;
			if (!ValidateRecoveredProtectiveOrder(
				targetOrder, "Profit target", OrderType.Limit, expectedAction,
				Position.Quantity, out failure, out pending))
				return false;
			if (string.IsNullOrEmpty(stopOrder.Oco)
				|| !string.Equals(stopOrder.Oco, targetOrder.Oco, StringComparison.Ordinal))
			{
				failure = "The managed stop and target do not share one non-empty OCO identifier.";
				return false;
			}
			if (!IsPositiveTickPrice(stopOrder.StopPrice)
				|| !IsPositiveTickPrice(targetOrder.LimitPrice))
			{
				failure = "The managed stop or target price is invalid or not aligned to the MNQ tick.";
				return false;
			}
			if ((Position.MarketPosition == MarketPosition.Long
					&& stopOrder.StopPrice >= targetOrder.LimitPrice)
				|| (Position.MarketPosition == MarketPosition.Short
					&& stopOrder.StopPrice <= targetOrder.LimitPrice))
			{
				failure = "The managed stop and target prices are not ordered around the recovered side.";
				return false;
			}
			if ((Position.MarketPosition == MarketPosition.Long
					&& targetOrder.LimitPrice <= Position.AveragePrice)
				|| (Position.MarketPosition == MarketPosition.Short
					&& targetOrder.LimitPrice >= Position.AveragePrice))
			{
				failure = "The managed target is not on the profitable side of the recovered average price.";
				return false;
			}
			bool accountOrderPending;
			if (!TryValidateAccountInstrumentOrderSet(
				true, out failure, out accountOrderPending))
			{
				pending = accountOrderPending;
				return false;
			}
			return true;
		}

		private bool ValidateRecoveredProtectiveOrder(
			Order order,
			string expectedName,
			OrderType expectedType,
			OrderAction expectedAction,
			int expectedQuantity,
			out string failure,
			out bool pending)
		{
			failure = string.Empty;
			pending = false;
			if (order == null)
			{
				failure = "Exactly one managed " + expectedName + " order was not reconstructed.";
				pending = true;
				return false;
			}
			if (order.IsBacktestOrder)
			{
				failure = "The managed " + expectedName + " is still a historical order reference.";
				pending = true;
				return false;
			}
			if (!string.Equals(order.Name, expectedName, StringComparison.Ordinal)
				|| !string.Equals(order.FromEntrySignal, activeEntrySignal, StringComparison.Ordinal))
			{
				failure = "The managed " + expectedName + " is not linked to the reconstructed entry signal.";
				return false;
			}
			if (order.OrderType != expectedType || order.OrderAction != expectedAction)
			{
				failure = "The managed " + expectedName + " has the wrong type or side.";
				return false;
			}
			if (order.TimeInForce != TimeInForce.Gtc)
			{
				failure = string.Format(CultureInfo.InvariantCulture,
					"The managed {0} is not GTC; timeInForce={1}, state={2}, type={3}, action={4}, quantity={5}, filled={6}.",
					expectedName, order.TimeInForce, order.OrderState,
					order.OrderType, order.OrderAction, order.Quantity, order.Filled);
				return false;
			}
			if (order.Quantity != expectedQuantity)
			{
				failure = "The managed " + expectedName + " quantity does not cover the full position.";
				return false;
			}
			if (order.Filled != 0)
			{
				failure = "The managed " + expectedName + " has a nonzero filled quantity.";
				return false;
			}
			if (!IsConfirmedWorkingProtection(order))
			{
				failure = "The managed " + expectedName + " is not broker-confirmed Working; state="
					+ order.OrderState.ToString() + ".";
				pending = IsLifecycleActiveOrder(order);
				return false;
			}
			return true;
		}

		private void HandleRestartProtectionFailure(string failure)
		{
			failClosedCount++;
			if (CanRequestBoundedManagedRecoveryExit())
			{
				restartRecoveryState = RestartRecoveryState.FailClosedExitPending;
				Log("FLAT MOON SOCIETY RESTART PROTECTION FAILURE: " + failure
					+ " A live current-instance managed protective reference proves ownership, so the existing bounded managed administrative exit is being requested. No account-level order API is used; verify the account is flat and has no remaining MNQ orders before restarting.", LogLevel.Error);
				RequestAdministrativeExit("RestartProtectionInvalid");
				return;
			}
			RequireManualRestartReconciliation(failure);
		}

		private bool CanRequestBoundedManagedRecoveryExit()
		{
			string accountOrderFailure;
			bool accountOrderPending;
			if (!TryValidateAccountInstrumentOrderSet(
				true, out accountOrderFailure, out accountOrderPending))
				return false;
			return liveManagedProtectionObserved
				&& !managedProtectionReferenceAmbiguous
				&& !duplicateManagedStopObserved
				&& !duplicateManagedTargetObserved
				&& !protectiveFillObserved
				&& !accountOrderSetMismatchObserved
				&& PositionAccount != null
				&& Position.MarketPosition != MarketPosition.Flat
				&& Position.MarketPosition == PositionAccount.MarketPosition
				&& Position.Quantity == PositionAccount.Quantity
				&& IsFinitePositive(Position.AveragePrice)
				&& IsFinitePositive(PositionAccount.AveragePrice)
				&& Math.Abs(Position.AveragePrice - PositionAccount.AveragePrice) <= TickSize
				&& !string.IsNullOrEmpty(activeEntrySignal)
				&& IsLiveManagedProtectiveReference(stopOrder)
				&& IsLiveManagedProtectiveReference(targetOrder)
				&& IsConfirmedWorkingProtection(stopOrder)
				&& IsConfirmedWorkingProtection(targetOrder)
				&& stopOrder.Quantity == Position.Quantity
				&& targetOrder.Quantity == Position.Quantity
				&& stopOrder.Filled == 0
				&& targetOrder.Filled == 0;
		}

		private bool TryValidateRecoveredPositionParity(out string failure)
		{
			failure = string.Empty;
			if (PositionAccount == null)
			{
				failure = "The account position became unavailable while a protected position was under lifecycle supervision.";
				return false;
			}
			if (Position.MarketPosition == MarketPosition.Flat
				|| PositionAccount.MarketPosition == MarketPosition.Flat
				|| Position.MarketPosition != PositionAccount.MarketPosition
				|| Position.Quantity != PositionAccount.Quantity)
			{
				failure = string.Format(CultureInfo.InvariantCulture,
					"Protected strategy/account position parity was lost: strategy={0}/{1}, account={2}/{3}.",
					Position.MarketPosition, Position.Quantity,
					PositionAccount.MarketPosition, PositionAccount.Quantity);
				return false;
			}
			if (!IsFinitePositive(Position.AveragePrice)
				|| !IsFinitePositive(PositionAccount.AveragePrice)
				|| Math.Abs(Position.AveragePrice - PositionAccount.AveragePrice) > TickSize)
			{
				failure = string.Format(CultureInfo.InvariantCulture,
					"Protected strategy/account average-price parity exceeded one tick: strategy={0:F4}, account={1:F4}.",
					Position.AveragePrice, PositionAccount.AveragePrice);
				return false;
			}
			return true;
		}

		private void RequireManualRestartReconciliation(string failure)
		{
			restartProtectionDegraded = false;
			System.Threading.Interlocked.Exchange(
				ref restartProtectionAuditQueued, 0);
			restartFlatConfirmationPending = false;
			ClearRestartOpenFirstSnapshot();
			if (restartRecoveryState == RestartRecoveryState.ManualReconciliationRequired)
			{
				Diagnostic("RESTART RECOVERY remains in manual reconciliation: " + failure);
				return;
			}
			restartRecoveryState = RestartRecoveryState.ManualReconciliationRequired;
			Log("FLAT MOON SOCIETY RESTART RECOVERY REQUIRES MANUAL RECONCILIATION: "
				+ failure
				+ " No account-level order API was used and no unproven order was adopted, cancelled, replaced, or flattened. RC3 will submit no further authored action from this state. NinjaTrader's StopCancelClose and session-close safety engines remain enabled and may still cancel strategy orders or submit a Close outside this recovery state machine. Leave any broker-held protection untouched, reconcile the position and working orders manually, and do not enable this instance again until the account is flat.", LogLevel.Error);
		}

		private bool TryValidateAccountInstrumentOrderSet(
			bool expectProtection,
			out string failure,
			out bool pending)
		{
			failure = string.Empty;
			pending = false;
			if (Account == null || Instrument == null)
			{
				accountOrderSetMismatchObserved = true;
				failure = "The account working-order collection is unavailable.";
				pending = expectProtection;
				return false;
			}
			var accountOrders = Account.Orders;
			if (accountOrders == null)
			{
				accountOrderSetMismatchObserved = true;
				failure = "The account working-order collection is unavailable.";
				pending = expectProtection;
				return false;
			}

			int activeInstrumentOrders = 0;
			int matchingStops = 0;
			int matchingTargets = 0;
			lock (accountOrders)
			{
				foreach (Order accountOrder in accountOrders)
				{
					if (!IsCurrentInstrumentOrder(accountOrder)
						|| !IsPotentiallyLiveAccountOrder(accountOrder))
						continue;
					activeInstrumentOrders++;
					if (IsSameOrderIdentity(accountOrder, stopOrder))
						matchingStops++;
					if (IsSameOrderIdentity(accountOrder, targetOrder))
						matchingTargets++;
				}
			}

			if (!expectProtection)
			{
				if (activeInstrumentOrders == 0)
					return true;
				accountOrderSetMismatchObserved = true;
				failure = "The strategy/account positions are flat but the account still has "
					+ activeInstrumentOrders.ToString(CultureInfo.InvariantCulture)
					+ " active order(s) for this instrument; RC3 will not classify or mutate them.";
				return false;
			}

			if (activeInstrumentOrders == 2 && matchingStops == 1 && matchingTargets == 1)
				return true;
			accountOrderSetMismatchObserved = true;
			pending = activeInstrumentOrders < 2 || matchingStops < 1 || matchingTargets < 1;
			failure = string.Format(CultureInfo.InvariantCulture,
				"The account order set does not contain exactly the two reconstructed protective orders: activeInstrumentOrders={0}, matchingStops={1}, matchingTargets={2}.",
				activeInstrumentOrders, matchingStops, matchingTargets);
			return false;
		}

		private bool IsPotentiallyLiveAccountOrder(Order order)
		{
			return order != null
				&& order.OrderState != OrderState.Cancelled
				&& order.OrderState != OrderState.Filled
				&& order.OrderState != OrderState.Rejected;
		}

		private bool IsCurrentInstrumentOrder(Order order)
		{
			return order != null && order.Instrument != null && Instrument != null
				&& string.Equals(
					order.Instrument.FullName, Instrument.FullName, StringComparison.Ordinal);
		}

		private bool IsLiveManagedProtectiveReference(Order order)
		{
			return order != null && !order.IsBacktestOrder
				&& !string.IsNullOrEmpty(activeEntrySignal)
				&& string.Equals(order.FromEntrySignal, activeEntrySignal, StringComparison.Ordinal)
				&& (string.Equals(order.Name, "Stop loss", StringComparison.Ordinal)
					|| string.Equals(order.Name, "Profit target", StringComparison.Ordinal));
		}

		private bool IsLifecycleActiveOrder(Order order)
		{
			if (order == null)
				return false;
			return order.OrderState == OrderState.Initialized
				|| order.OrderState == OrderState.TriggerPending
				|| order.OrderState == OrderState.Submitted
				|| order.OrderState == OrderState.Accepted
				|| order.OrderState == OrderState.Working
				|| order.OrderState == OrderState.PartFilled
				|| order.OrderState == OrderState.ChangePending
				|| order.OrderState == OrderState.ChangeSubmitted
				|| order.OrderState == OrderState.CancelPending
				|| order.OrderState == OrderState.CancelSubmitted
				|| order.OrderState == OrderState.Unknown;
		}

		private bool IsConfirmedWorkingProtection(Order order)
		{
			return order != null && order.OrderState == OrderState.Working;
		}

		private void ReleaseRestartEntryBlockAtNewCashDate()
		{
			if (currentCashDate <= restartRecoveryCashDate)
				return;
			if (Position.MarketPosition != MarketPosition.Flat
				|| PositionAccount == null
				|| PositionAccount.MarketPosition != MarketPosition.Flat
				|| IsLifecycleActiveOrder(stopOrder)
				|| IsLifecycleActiveOrder(targetOrder))
			{
				RequireManualRestartReconciliation(
					"The next New York cash date began before the recovered position and managed protection were conclusively flat/terminal.");
				return;
			}
			string accountOrderFailure;
			bool accountOrderPending;
			if (!TryValidateAccountInstrumentOrderSet(
				false, out accountOrderFailure, out accountOrderPending))
			{
				RequireManualRestartReconciliation(accountOrderFailure);
				return;
			}
			stopOrder = null;
			targetOrder = null;
			duplicateManagedStopObserved = false;
			duplicateManagedTargetObserved = false;
			managedProtectionReferenceAmbiguous = false;
			protectiveFillObserved = false;
			accountOrderSetMismatchObserved = false;
			liveManagedProtectionObserved = false;
			restartProtectionDegraded = false;
			System.Threading.Interlocked.Exchange(
				ref restartProtectionAuditQueued, 0);
			restartFlatConfirmationPending = false;
			restartRecoveryState = RestartRecoveryState.FlatReady;
			ClearRestartOpenFirstSnapshot();
			Print("FLAT MOON SOCIETY RESTART RECOVERY COMPLETE: the recovered exposure stayed flat through a new New York cash-date boundary; normal entry evaluation is enabled.");
		}

		private bool IsPositiveTickPrice(double price)
		{
			return price > 0 && !double.IsNaN(price) && !double.IsInfinity(price)
				&& Math.Abs(price - RoundPrice(price)) <= TickSize * 0.000001;
		}

		private void RefreshSessionSchedule()
		{
			if (!Bars.IsFirstBarOfSession && scheduleKnown)
				return;

			ClearSessionSchedule();
			try
			{
				if (!sessionIterator.GetNextSession(Time[0], true))
				{
					Log("FLAT MOON SOCIETY could not resolve a Trading Hours session for the current NinjaTrader timestamp; this session is blocked.", LogLevel.Error);
					return;
				}
				actualSessionBeginPlatform = sessionIterator.ActualSessionBegin;
				actualSessionEndPlatform = sessionIterator.ActualSessionEnd;
				actualTradingDayExchange = sessionIterator.ActualTradingDayExchange;

				DateTime barTimePlatform = DateTime.SpecifyKind(Time[0], DateTimeKind.Unspecified);
				if (platformTimeZone.IsInvalidTime(barTimePlatform)
					|| platformTimeZone.IsAmbiguousTime(barTimePlatform))
					throw new InvalidOperationException(
						"The first processed session timestamp is invalid or ambiguous in the configured NinjaTrader time zone.");
				DateTime beginPlatform = DateTime.SpecifyKind(
					actualSessionBeginPlatform, DateTimeKind.Unspecified);
				DateTime endPlatform = DateTime.SpecifyKind(
					actualSessionEndPlatform, DateTimeKind.Unspecified);
				if (endPlatform <= beginPlatform
					|| barTimePlatform < beginPlatform
					|| barTimePlatform > endPlatform)
					throw new InvalidOperationException(
						"The NinjaTrader bar timestamp is outside the SessionIterator bounds.");

				DateTime barTimeUtc = ToUtc(barTimePlatform);
				DateTime roundTripPlatform = DateTime.SpecifyKind(
					TimeZoneInfo.ConvertTimeFromUtc(barTimeUtc, platformTimeZone),
					DateTimeKind.Unspecified);
				if (roundTripPlatform != barTimePlatform)
					throw new InvalidOperationException(
						"The NinjaTrader bar timestamp failed its configured-zone UTC round trip.");
				scheduleKnown = true;

				DateTime beginEt = ToEastern(actualSessionBeginPlatform);
				DateTime endEt = ToEastern(actualSessionEndPlatform);
				DateTime barTimeEt = ToEastern(barTimePlatform);
				string tradingHoursZoneName = Bars != null && Bars.TradingHours != null
					&& Bars.TradingHours.TimeZoneInfo != null
					? Bars.TradingHours.TimeZoneInfo.Id : "(unavailable)";
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"SESSION SCHEDULE: barPlatform={0:yyyy-MM-dd HH:mm}, barUTC={1:yyyy-MM-dd HH:mm}, barET={2:yyyy-MM-dd HH:mm}, beginET={3:yyyy-MM-dd HH:mm}, endET={4:yyyy-MM-dd HH:mm}, exchangeTradingDay={5:yyyy-MM-dd}, platformZone={6}, tradingHoursZone={7}.",
					barTimePlatform, barTimeUtc, barTimeEt, beginEt, endEt,
					actualTradingDayExchange, platformTimeZone.Id,
					tradingHoursZoneName));
			}
			catch (Exception ex)
			{
				ClearSessionSchedule();
				Log("FLAT MOON SOCIETY could not resolve the Trading Hours session; this session is blocked: " + ex.Message, LogLevel.Error);
			}
		}

		private void ClearSessionSchedule()
		{
			scheduleKnown = false;
			actualSessionBeginPlatform = DateTime.MinValue;
			actualSessionEndPlatform = DateTime.MinValue;
			actualTradingDayExchange = DateTime.MinValue;
		}

		private void BeginCashDate(DateTime cashDate)
		{
			bool inheritedExposure = Position.MarketPosition != MarketPosition.Flat;
			bool inheritedWorkingEntry = IsLifecycleActiveOrder(entryOrder);
			if (activeShadowPair != null || pendingEntryDecision != null
				|| pendingFinalEntryDecision != null)
				InvalidatePendingSessionState("new trading day reached before prior state completed");

			currentCashDate = cashDate.Date;
			sessionDateEligible = true;
			referenceSessionOpenCaptured = false;
			referenceSessionOpen = 0.0;
			dayBlocked = inheritedExposure || inheritedWorkingEntry;
			sessionEnding = inheritedExposure || inheritedWorkingEntry;
			orbFinalized = false;
			missingOrbLogged = false;
			breakoutConsumed = inheritedExposure || inheritedWorkingEntry;
			cashExitTriggered = false;
			cashWindowIntegrity = false;
			orbHistoryRecorded = false;
			rthCloseHistoryRecorded = false;
			activeInitialStopTicks = 0;
			managedStopActivated = false;
			conditionalExitRequested = false;
			orbBarCount = 0;
			expectedNextOrbOpenMinute = OrbStartMinuteEt;
			orbHigh = double.MinValue;
			orbLow = double.MaxValue;
			orbOpen = 0;
			orbClose = 0;
			currentOrbBps = 0;
			currentPriorOrbQ75Bps = 0;
			currentPriorOrbQ75Available = false;
			currentPriorTrendBps = 0;
			currentPriorTrendAvailable = TryGetPriorTrendReturn(out currentPriorTrendBps);
			currentRthMinuteCloses.Clear();
			currentRthTypicalVolumeSum = 0;
			currentRthVolumeSum = 0;
			lastCashBarCloseEt = DateTime.MinValue;
			activeManagedStopTriggerR = 0;
			activeManagedStopLockR = 0;
			activeManagementRegime = string.Empty;

			if (inheritedExposure || inheritedWorkingEntry)
			{
				Log("FLAT MOON SOCIETY reached a new New York cash date with inherited exposure or a working entry. The new date is blocked and reconciliation is requested.", LogLevel.Error);
				CancelWorkingEntry();
				RequestAdministrativeExit("OvernightExposureOrEntry");
				return;
			}

			entryOrder = null;
			administrativeExitOrder = null;
			administrativeExitAttempts = 0;
			activeEntrySignal = string.Empty;
			activeExitSignal = string.Empty;

			if (!IsWeekday(currentCashDate.DayOfWeek))
			{
				sessionDateEligible = false;
				dayBlocked = true;
				return;
			}

			int dateKey = DateKey(currentCashDate);
			if (ContainsDateKey(DegradedDataExclusionDateKeys, dateKey)
				|| ContainsDateKey(ContractRollExclusionDateKeys, dateKey))
			{
				sessionDateEligible = false;
				dayBlocked = true;
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"CASH DATE {0:yyyy-MM-dd}: excluded by the embedded data-quality calendar.",
					currentCashDate));
				return;
			}

			if (!ScheduleContainsFullCashWindow(currentCashDate))
			{
				sessionDateEligible = false;
				dayBlocked = true;
				DateTime endEt = ToEastern(actualSessionEndPlatform);
				string scheduleSkip = string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY skips {0:yyyy-MM-dd}: Trading Hours session ends at {1:yyyy-MM-dd HH:mm} ET, before the required 16:00 ET cash close or outside the cash date.",
					currentCashDate, endEt);
				if (State == State.Realtime)
					Log(scheduleSkip, LogLevel.Warning);
				else
					Diagnostic(scheduleSkip);
				return;
			}

			cashWindowIntegrity = true;
			Diagnostic("CASH DATE " + currentCashDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": ready.");
		}

		private bool ScheduleContainsFullCashWindow(DateTime cashDate)
		{
			if (!scheduleKnown)
				return false;

			DateTime beginEt = ToEastern(actualSessionBeginPlatform);
			DateTime endEt = ToEastern(actualSessionEndPlatform);
			DateTime requiredOpenEt = cashDate.Date.AddMinutes(OrbStartMinuteEt);
			DateTime requiredCloseEt = cashDate.Date.AddMinutes(RequiredCashCloseMinuteEt);
			return beginEt <= requiredOpenEt && endEt >= requiredCloseEt;
		}

		private void CaptureOpeningRange(DateTime barOpenEt, DateTime barCloseEt,
			int openMinuteEt, int closeMinuteEt)
		{
			if (orbFinalized || dayBlocked)
				return;

			bool sameCashDate = barOpenEt.Date == currentCashDate && barCloseEt.Date == currentCashDate;
			bool insideOrb = sameCashDate
				&& openMinuteEt >= OrbStartMinuteEt
				&& openMinuteEt < OrbEndMinuteEt;

			if (insideOrb)
			{
				if (openMinuteEt != expectedNextOrbOpenMinute || closeMinuteEt != openMinuteEt + 1)
				{
					BlockCashDate("Opening-range one-minute bars are missing, duplicated, or misaligned.");
					return;
				}

				if (orbBarCount == 0)
					orbOpen = Open[0];
				orbHigh = orbBarCount == 0 ? High[0] : Math.Max(orbHigh, High[0]);
				orbLow = orbBarCount == 0 ? Low[0] : Math.Min(orbLow, Low[0]);
				orbClose = Close[0];
				orbBarCount++;
				expectedNextOrbOpenMinute++;

				if (closeMinuteEt == OrbEndMinuteEt)
				{
					if (orbBarCount != ExpectedOrbBars || orbHigh < orbLow)
					{
						BlockCashDate("The 09:30-09:45 ET opening range is incomplete or invalid.");
						return;
					}
					orbFinalized = true;
					double midpoint = (orbHigh + orbLow) / 2.0;
					currentOrbBps = midpoint > 0 ? (orbHigh - orbLow) / midpoint * 10000.0 : 0.0;
					currentPriorOrbQ75Available = eligibleOrbHistoryBps.Count == ConfidenceOrbLookback;
					if (currentPriorOrbQ75Available)
						currentPriorOrbQ75Bps = LinearPercentile(
							eligibleOrbHistoryBps, ConfidenceOrbQuantile);
					Diagnostic(string.Format(CultureInfo.InvariantCulture,
						"ORB {0:yyyy-MM-dd}: high={1:F2}, low={2:F2}, range={3:F2}, bps={4:F3}, bars={5}, priorHistory={6}, priorQ75={7}.",
						currentCashDate, orbHigh, orbLow, orbHigh - orbLow, currentOrbBps,
						orbBarCount, eligibleOrbHistoryBps.Count,
						currentPriorOrbQ75Available
							? currentPriorOrbQ75Bps.ToString("F3", CultureInfo.InvariantCulture)
							: "warmup"));
				}
				return;
			}

			if (sameCashDate && closeMinuteEt > OrbEndMinuteEt && !orbFinalized)
				BlockCashDate("The exact 09:30-09:45 ET opening range was not available.");
		}

		private void BlockCashDate(string reason)
		{
			cashWindowIntegrity = false;
			dayBlocked = true;
			breakoutConsumed = true;
			if (!missingOrbLogged)
			{
				missingOrbLogged = true;
				Log("FLAT MOON SOCIETY blocks "
					+ currentCashDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
					+ ": " + reason, LogLevel.Error);
			}
		}

		private void ManageProtectiveStopAtQuarterHour()
		{
			if (managedStopActivated || activeInitialStopTicks < 1
				|| string.IsNullOrEmpty(activeEntrySignal)
				|| Position.MarketPosition == MarketPosition.Flat)
				return;

			double initialRiskPoints = activeInitialStopTicks * TickSize;
			if (!(initialRiskPoints > 0) || !(Position.AveragePrice > 0))
				return;
			int side = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
			double closeR = side * (Close[0] - Position.AveragePrice) / initialRiskPoints;
			if (closeR < activeManagedStopTriggerR)
				return;

			double managedStopPrice = RoundPrice(
				Position.AveragePrice + side * initialRiskPoints * activeManagedStopLockR);

			// SetStopLoss is called only after the completed quarter-hour close.
			// The new price is protection for the next one-minute bar; it is not
			// allowed to rewrite the stop/target outcome of the trigger bar.
			SetStopLoss(activeEntrySignal, CalculationMode.Price, managedStopPrice, false);
			managedStopActivated = true;
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"MANAGED STOP: regime={0}, closeR={1:F3}, trigger={2:F2}, lock={3:F2}, fill={4:F2}, stop={5:F2}; active next one-minute bar.",
				activeManagementRegime, closeR, activeManagedStopTriggerR,
				activeManagedStopLockR, Position.AveragePrice, managedStopPrice));
		}

		private void ManageConditionalExit1530()
		{
			if (conditionalExitRequested || activeInitialStopTicks < 1
				|| string.IsNullOrEmpty(activeEntrySignal)
				|| Position.MarketPosition == MarketPosition.Flat)
				return;

			double initialRiskPoints = activeInitialStopTicks * TickSize;
			if (!(initialRiskPoints > 0) || !(Position.AveragePrice > 0))
				return;
			int side = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
			double closeR = side * (Close[0] - Position.AveragePrice) / initialRiskPoints;
			if (closeR >= ConditionalLossBoundaryR && closeR < ConditionalProfitBoundaryR)
				return;

			conditionalExitRequested = true;
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"CONDITIONAL 15:30 EXIT: closeR={0:F3}, fill={1:F2}.",
				closeR, Position.AveragePrice));
			RequestAdministrativeExit("Conditional1530ET");
		}

		private void CaptureReferenceSessionOpen(DateTime barOpenEt, DateTime barOpenUtc)
		{
			if (!sessionDateEligible || referenceSessionOpenCaptured)
				return;
			if (barOpenUtc.Hour != 23 || barOpenUtc.Minute != 0)
				return;
			// The reference contract is the fixed 23:00 UTC bar on the calendar
			// day immediately before the exchange cash date. A 23:00 bar from any
			// other date must never seed the current session.
			if (barOpenUtc.Date != currentCashDate.AddDays(-1).Date)
				return;
			if (!IsExactMinute(barOpenUtc) || !IsExactMinute(barOpenEt))
			{
				BlockCashDate("The required 23:00 UTC reference opening bar is not aligned to an exact minute.");
				return;
			}
			if (!(Open[0] > 0) || double.IsNaN(Open[0]) || double.IsInfinity(Open[0]))
			{
				BlockCashDate("The required 23:00 UTC reference open is invalid.");
				return;
			}
			referenceSessionOpen = Open[0];
			referenceSessionOpenCaptured = true;
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"REFERENCE OPEN {0:yyyy-MM-dd}: utc=23:00, et={1:yyyy-MM-dd HH:mm}, price={2:F2}.",
				currentCashDate, barOpenEt, referenceSessionOpen));
		}

		private void EvaluateAdmissionAndDirection(int baselineSide, DateTime signalCloseEt)
		{
			double signalOpen;
			double signalHigh;
			double signalLow;
			double signalClose;
			if (!TryGetExactSignalState(
				signalCloseEt,
				out signalOpen,
				out signalHigh,
				out signalLow,
				out signalClose))
			{
				breakoutConsumed = true;
				BlockCashDate("A raw breakout did not have 15 exact one-minute constituents.");
				InvalidatePendingSessionState("inexact admission constituents");
				return;
			}

			double signalRange = signalHigh - signalLow;
			double bodyFraction = signalRange > 0
				? baselineSide * (signalClose - signalOpen) / signalRange
				: 0.0;
			double closeLocation = signalRange > 0
				? (baselineSide == 1
					? (signalClose - signalLow) / signalRange
					: (signalHigh - signalClose) / signalRange)
				: 0.0;
			if (bodyFraction < AdmissionBodyFraction
				|| closeLocation < AdmissionCloseLocation)
			{
				geometryRejectCount++;
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"ADMISSION GEOMETRY REJECT {0:yyyy-MM-dd HH:mm}: side={1}, body={2:F4}, closeLocation={3:F4}; scanning continues.",
					signalCloseEt, baselineSide, bodyFraction, closeLocation));
				return;
			}

			double baselineConfirmation = baselineSide == 1
				? orbHigh + ConfirmationTicks * TickSize
				: orbLow - ConfirmationTicks * TickSize;
			int admissionTouches = CountSignalTouches(baselineSide, baselineConfirmation);
			if (admissionTouches < AdmissionMinimumTouches)
			{
				breakoutConsumed = true;
				touchVetoCount++;
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"ADMISSION TOUCH VETO {0:yyyy-MM-dd HH:mm}: side={1}, touches={2}, required={3}.",
					signalCloseEt, baselineSide, admissionTouches, AdmissionMinimumTouches));
				return;
			}

			double[] features;
			if (!TryBuildDirectionFeatures(
				baselineSide,
				admissionTouches,
				signalCloseEt,
				signalClose,
				out features))
			{
				breakoutConsumed = true;
				BlockCashDate("The admitted signal could not form the exact 14-feature direction vector.");
				InvalidatePendingSessionState("direction feature construction failed");
				return;
			}

			breakoutConsumed = true;
			admittedSignalCount++;
			double priorSessionReturnBps = eligibleSessionReturnHistoryBps.Count > 0
				? eligibleSessionReturnHistoryBps[eligibleSessionReturnHistoryBps.Count - 1]
				: 0.0;
			bool priorOverride = baselineSide * priorSessionReturnBps
				< -PriorDayContinuationThresholdBps;
			if (priorOverride)
				priorDayOverrideCount++;

			double flipProbability;
			int flipNeighbors;
			int trainingCount;
			bool predictionAvailable = TryPredictDirection(
				currentCashDate,
				features,
				out flipProbability,
				out flipNeighbors,
				out trainingCount);
			bool knnOverride = predictionAvailable
				&& flipProbability > ModelFlipProbabilityThreshold;
			if (knnOverride)
				knnOverrideCount++;

			int directionSide = priorOverride || knnOverride ? -baselineSide : baselineSide;
			string directionSource = priorOverride && knnOverride
				? "prior_session_and_quarterly_knn"
				: priorOverride
					? "prior_session"
					: knnOverride ? "quarterly_knn" : "breakout";

			StartShadowPair(
				currentCashDate,
				signalCloseEt,
				baselineSide,
				features,
				currentPriorTrendAvailable,
				currentPriorTrendBps);
			if (dayBlocked)
				return;

			bool tradingDateReached = currentCashDate >= portfolioStartCashDate;
			bool contextReadyForActualOrder = currentPriorOrbQ75Available
				&& currentPriorTrendAvailable;
			bool submitActual = tradingDateReached && contextReadyForActualOrder;
			if (tradingDateReached && !contextReadyForActualOrder)
			{
				contextWarmupSkipCount++;
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"CONTEXT WARM-UP SKIP {0:yyyy-MM-dd}: priorORB120={1}, priorTrend21={2}; shadow/model/session state continues.",
					currentCashDate, currentPriorOrbQ75Available,
					currentPriorTrendAvailable));
			}

			// A first-decision signal close to completed-session VWAP takes the
			// direct route before the remaining direction refinements. Features 4
			// and 7 hold aligned VWAP distance and the elapsed 15-minute boundary.
			double alignedVwapDistanceBps = features[Rc1AlignedVwapFeatureIndex];
			double elapsedSignal15m = features[Rc1ElapsedSignalFeatureIndex];
			bool directCondition = elapsedSignal15m == Rc1DirectElapsedSignal15m
				&& alignedVwapDistanceBps <= Rc1DirectMaxAlignedVwapDistanceBps;
			if (directCondition)
			{
				rc1DirectDecisionCount++;
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"ENTRY ROUTE {0:yyyy-MM-dd HH:mm}: action={1}, elapsed15={2:F1}, alignedVwapBps={3:F6}, baselineSide={4}; integrated refinement not evaluated.",
					signalCloseEt, Rc1DirectAction, elapsedSignal15m,
					alignedVwapDistanceBps, baselineSide));
				if (submitActual)
					SubmitEntry(
						baselineSide,
						"rc1_breakout_shadow",
						Rc1DirectAction);
				return;
			}
			rc1ParentDecisionCount++;

			double directionalBody = directionSide * (signalClose - signalOpen) / signalRange;
			bool weakSignal = directionalBody <= WeakSignalBodyFraction;
			bool highVol = currentPriorOrbQ75Available
				&& currentOrbBps > currentPriorOrbQ75Bps;
			double directionConfirmation = directionSide == 1
				? orbHigh + ConfirmationTicks * TickSize
				: orbLow - ConfirmationTicks * TickSize;
			int directionTouches = CountSignalTouches(directionSide, directionConfirmation);
			bool highVolTouchVote = highVol && directionTouches <= HighVolTouchLimit;

			if (weakSignal || highVolTouchVote)
			{
				pendingEntryDecision = new PendingEntryDecision();
				pendingEntryDecision.ExpectedObservationOpenEt = signalCloseEt;
				pendingEntryDecision.DirectionSide = directionSide;
				pendingEntryDecision.RequiredObservationBars = weakSignal
					? WeakSignalDelayBars : HighVolVoteBars;
				pendingEntryDecision.ObservedBars = 0;
				pendingEntryDecision.FirstObservationOpen = 0;
				pendingEntryDecision.SubmitActualOrder = submitActual;
				pendingEntryDecision.Branch = weakSignal ? "weak_signal_delay_keep" : "high_vol_touch_vote";
				pendingEntryDecision.DirectionSource = directionSource;
				pendingEntryDecision.BaselineSide = baselineSide;
				pendingEntryDecision.Features = (double[])features.Clone();
				pendingEntryDecision.SignalClose = signalClose;
				if (weakSignal)
					weakDelayCount++;
				else
					highVolVoteCount++;
			}
			else
			{
				ApplyFinalEntryRefinementOrSubmit(
					directionSide,
					signalCloseEt,
					directionSource,
					"condition_not_met",
					submitActual,
					baselineSide,
					features,
					signalClose);
			}

			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"DIRECTION {0:yyyy-MM-dd HH:mm}: baseline={1}, selected={2}, source={3}, priorSessionBps={4:F3}, priorOverride={5}, modelAvailable={6}, modelRows={7}, flipNeighbors={8}, flipProbability={9:F6}, knnOverride={10}, admissionTouches={11}, body={12:F4}, closeLocation={13:F4}, weak={14}, highVol={15}, directionTouches={16}, branch={17}.",
				signalCloseEt, baselineSide, directionSide, directionSource,
				priorSessionReturnBps, priorOverride, predictionAvailable, trainingCount,
				flipNeighbors, flipProbability, knnOverride, admissionTouches,
				bodyFraction, closeLocation, weakSignal, highVol, directionTouches,
				weakSignal ? "weak_signal" : highVolTouchVote ? "high_vol_touch_vote" : "none"));
		}

		private bool TryGetExactSignalState(
			DateTime signalCloseEt,
			out double signalOpen,
			out double signalHigh,
			out double signalLow,
			out double signalClose)
		{
			signalOpen = 0;
			signalHigh = double.MinValue;
			signalLow = double.MaxValue;
			signalClose = 0;
			if (CurrentBar < 14)
				return false;
			for (int barsAgo = 0; barsAgo < 15; barsAgo++)
			{
				DateTime observedCloseEt = ToEastern(Time[barsAgo]);
				DateTime expectedCloseEt = signalCloseEt.AddMinutes(-barsAgo);
				if (observedCloseEt != expectedCloseEt)
					return false;
				signalHigh = Math.Max(signalHigh, High[barsAgo]);
				signalLow = Math.Min(signalLow, Low[barsAgo]);
			}
			signalOpen = Open[14];
			signalClose = Close[0];
			return signalHigh > signalLow;
		}

		private int CountSignalTouches(int side, double confirmationLevel)
		{
			int count = 0;
			for (int barsAgo = 0; barsAgo < 15; barsAgo++)
			{
				if ((side == 1 && High[barsAgo] >= confirmationLevel)
					|| (side == -1 && Low[barsAgo] <= confirmationLevel))
					count++;
			}
			return count;
		}

		private bool TryBuildDirectionFeatures(
			int baselineSide,
			int admissionTouches,
			DateTime signalCloseEt,
			double signalClose,
			out double[] features)
		{
			features = null;
			if (!(orbOpen > 0) || !(orbClose > 0) || !(currentRthVolumeSum > 0))
				return false;
			if (currentRthMinuteCloses.Count == 0)
				return false;

			double priorClose = eligibleRthCloseHistory.Count > 0
				? eligibleRthCloseHistory[eligibleRthCloseHistory.Count - 1]
				: orbOpen;
			double priorSessionReturn = eligibleSessionReturnHistoryBps.Count > 0
				? eligibleSessionReturnHistoryBps[eligibleSessionReturnHistoryBps.Count - 1]
				: 0.0;
			double priorRet5 = LagReturnBps(eligibleRthCloseHistory, 5);
			double priorRet20 = LagReturnBps(eligibleRthCloseHistory, 20);
			double vwap = currentRthTypicalVolumeSum / currentRthVolumeSum;
			double trailingBase = currentRthMinuteCloses.Count > 30
				? currentRthMinuteCloses[currentRthMinuteCloses.Count - 31]
				: orbClose;
			double gapBps = SafeReturnBps(orbOpen, priorClose);
			double vwapDistanceBps = SafeReturnBps(signalClose, vwap);
			double trailing30Bps = SafeReturnBps(signalClose, trailingBase);
			double elapsed15 = (MinuteOfDay(signalCloseEt) - OrbEndMinuteEt) / 15.0;
			int weekday = ((int)currentCashDate.DayOfWeek + 6) % 7;
			int month = currentCashDate.Month;

			features = new double[]
			{
				baselineSide * gapBps,
				baselineSide * priorSessionReturn,
				baselineSide * priorRet5,
				baselineSide * priorRet20,
				baselineSide * vwapDistanceBps,
				baselineSide * trailing30Bps,
				baselineSide,
				elapsed15,
				currentOrbBps,
				admissionTouches,
				Math.Sin(2.0 * Math.PI * weekday / 5.0),
				Math.Cos(2.0 * Math.PI * weekday / 5.0),
				Math.Sin(2.0 * Math.PI * (month - 1) / 12.0),
				Math.Cos(2.0 * Math.PI * (month - 1) / 12.0)
			};
			if (features.Length != ModelFeatureCount)
				return false;
			for (int index = 0; index < features.Length; index++)
				if (double.IsNaN(features[index]) || double.IsInfinity(features[index]))
					return false;
			return true;
		}

		private bool TryPredictDirection(
			DateTime session,
			double[] features,
			out double flipProbability,
			out int flipNeighbors,
			out int trainingCount)
		{
			flipProbability = 0;
			flipNeighbors = 0;
			trainingCount = 0;
			if (DateKey(session) < ModelPredictionStartDateKey)
				return false;

			DateTime quarter = QuarterStart(session);
			int quarterKey = quarter.Year * 10 + ((quarter.Month - 1) / 3 + 1);
			if (activeQuarterKey != quarterKey)
			{
				activeQuarterModel = FitQuarterModel(quarter);
				activeQuarterKey = quarterKey;
			}
			if (activeQuarterModel == null)
				return false;
			trainingCount = activeQuarterModel.Rows.Count;

			List<NeighborMatch> neighbors = new List<NeighborMatch>();
			for (int rowIndex = 0; rowIndex < activeQuarterModel.Rows.Count; rowIndex++)
			{
				DirectionTrainingRow row = activeQuarterModel.Rows[rowIndex];
				double squared = 0;
				for (int featureIndex = 0; featureIndex < ModelFeatureCount; featureIndex++)
				{
					double queryValue = (features[featureIndex]
						- activeQuarterModel.Means[featureIndex])
						/ activeQuarterModel.Scales[featureIndex];
					double rowValue = (row.Features[featureIndex]
						- activeQuarterModel.Means[featureIndex])
						/ activeQuarterModel.Scales[featureIndex];
					double difference = rowValue - queryValue;
					squared += difference * difference;
				}
				NeighborMatch neighbor = new NeighborMatch();
				neighbor.Row = row;
				neighbor.Distance = Math.Sqrt(squared);
				neighbors.Add(neighbor);
			}
			neighbors.Sort(delegate(NeighborMatch left, NeighborMatch right)
			{
				int byDistance = left.Distance.CompareTo(right.Distance);
				return byDistance != 0
					? byDistance
					: left.Row.Sequence.CompareTo(right.Row.Sequence);
			});

			for (int index = 0; index < ModelNeighbors; index++)
				if (neighbors[index].Row.FlipWins)
					flipNeighbors++;
			flipProbability = (flipNeighbors + 2.0) / (ModelNeighbors + 4.0);
			knnPredictionCount++;
			return true;
		}

		private QuarterDirectionModel FitQuarterModel(DateTime quarter)
		{
			DateTime cutoff = quarter.AddDays(-1);
			List<DirectionTrainingRow> rows = new List<DirectionTrainingRow>();
			for (int index = 0; index < directionTrainingRows.Count; index++)
			{
				DirectionTrainingRow row = directionTrainingRows[index];
				if (DateKey(row.Session) >= ModelTrainingStartDateKey
					&& row.Session.Date <= cutoff)
					rows.Add(row);
			}
			if (rows.Count < Math.Max(ModelMinimumTrainingRows, ModelNeighbors))
			{
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"KNN REFIT {0}Q{1}: unavailable, cutoff={2:yyyy-MM-dd}, rows={3}, minimum={4}.",
					quarter.Year, ((quarter.Month - 1) / 3) + 1, cutoff, rows.Count,
					ModelMinimumTrainingRows));
				return null;
			}

			QuarterDirectionModel model = new QuarterDirectionModel();
			model.Rows = rows;
			model.Means = new double[ModelFeatureCount];
			model.Scales = new double[ModelFeatureCount];
			for (int featureIndex = 0; featureIndex < ModelFeatureCount; featureIndex++)
			{
				double sum = 0;
				for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
					sum += rows[rowIndex].Features[featureIndex];
				double mean = sum / rows.Count;
				double squared = 0;
				for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
				{
					double difference = rows[rowIndex].Features[featureIndex] - mean;
					squared += difference * difference;
				}
				double scale = Math.Sqrt(squared / rows.Count);
				model.Means[featureIndex] = mean;
				model.Scales[featureIndex] = scale > 0 ? scale : 1.0;
			}

			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"KNN REFIT {0}Q{1}: cutoff={2:yyyy-MM-dd}, rows={3}, neighbors={4}, scaler=population-standard-deviation.",
				quarter.Year, ((quarter.Month - 1) / 3) + 1, cutoff, rows.Count,
				ModelNeighbors));
			return model;
		}

		private DateTime QuarterStart(DateTime value)
		{
			int month = ((value.Month - 1) / 3) * 3 + 1;
			return new DateTime(value.Year, month, 1);
		}

		private double SafeReturnBps(double numerator, double denominator)
		{
			return denominator != 0 ? (numerator / denominator - 1.0) * 10000.0 : 0.0;
		}

		private double LagReturnBps(List<double> values, int periods)
		{
			if (values == null || values.Count <= periods
				|| values[values.Count - 1 - periods] == 0)
				return 0.0;
			return SafeReturnBps(
				values[values.Count - 1],
				values[values.Count - 1 - periods]);
		}

		private void StartShadowPair(
			DateTime session,
			DateTime signalCloseEt,
			int baselineSide,
			double[] features,
			bool priorTrendAvailable,
			double priorTrendBps)
		{
			if (DateKey(session) < ModelTrainingStartDateKey)
				return;
			if (activeShadowPair != null)
			{
				InvalidatePendingSessionState("a second shadow pair was requested before the first completed");
				dayBlocked = true;
				return;
			}
			activeShadowPair = new ShadowPairState();
			activeShadowPair.Session = session.Date;
			activeShadowPair.ExpectedParentFillOpenEt = signalCloseEt;
			activeShadowPair.BaselineSide = baselineSide;
			activeShadowPair.Features = (double[])features.Clone();
			activeShadowPair.PriorTrendAvailable = priorTrendAvailable;
			activeShadowPair.PriorTrendBps = priorTrendBps;
		}

		private void ProcessShadowPair(
			DateTime barOpenEt,
			DateTime barCloseEt,
			int closeMinuteEt)
		{
			if (activeShadowPair == null || activeShadowPair.Invalid
				|| activeShadowPair.LabelRecorded)
				return;
			if (!activeShadowPair.Initialized)
			{
				if (barOpenEt < activeShadowPair.ExpectedParentFillOpenEt)
					return;
				if (barOpenEt != activeShadowPair.ExpectedParentFillOpenEt)
				{
					activeShadowPair.Invalid = true;
					failClosedCount++;
					Diagnostic("MODEL LABEL SKIP: exact parent next-minute open was unavailable.");
					return;
				}
				activeShadowPair.Breakout = CreateShadowTrade(
					activeShadowPair.BaselineSide,
					Open[0],
					activeShadowPair.PriorTrendAvailable,
					activeShadowPair.PriorTrendBps);
				activeShadowPair.Fade = CreateShadowTrade(
					-activeShadowPair.BaselineSide,
					Open[0],
					activeShadowPair.PriorTrendAvailable,
					activeShadowPair.PriorTrendBps);
				activeShadowPair.Initialized = true;
			}

			ProcessShadowTradeBar(activeShadowPair.Breakout, closeMinuteEt);
			ProcessShadowTradeBar(activeShadowPair.Fade, closeMinuteEt);
			TryRecordCompletedModelLabel();
		}

		private ShadowTradeState CreateShadowTrade(
			int side,
			double parentFillOpen,
			bool priorTrendAvailable,
			double priorTrendBps)
		{
			double entry = parentFillOpen + side * ModelEntrySlippageTicks * TickSize;
			double rawStopDistance = Math.Max(MinimumStopPoints,
				Math.Min(MaximumStopPoints, (orbHigh - orbLow) * StopOrbMultiple));
			double stop = RoundPrice(entry - side * rawStopDistance);
			double riskPoints = side * (entry - stop);
			double targetR;
			double managedTriggerR;
			double managedLockR;
			string ignoredRegime;
			SelectManagementPlan(
				side,
				priorTrendAvailable,
				priorTrendBps,
				out targetR,
				out managedTriggerR,
				out managedLockR,
				out ignoredRegime);

			ShadowTradeState state = new ShadowTradeState();
			state.Side = side;
			state.Entry = entry;
			state.ActiveStop = stop;
			state.Target = RoundPrice(entry + side * rawStopDistance * targetR);
			state.RiskPoints = riskPoints;
			state.ManagedTriggerR = managedTriggerR;
			state.ManagedLockR = managedLockR;
			if (!(riskPoints > 0))
			{
				state.Complete = true;
				state.ExitPrice = entry;
			}
			return state;
		}

		private void ProcessShadowTradeBar(ShadowTradeState state, int closeMinuteEt)
		{
			if (state == null || state.Complete)
				return;
			if (state.PendingConditionalExit)
			{
				CompleteShadowTrade(
					state,
					Open[0] - state.Side * ModelDayFlatSlippageTicks * TickSize);
				return;
			}

			bool stopHit = state.Side == 1
				? Low[0] <= state.ActiveStop
				: High[0] >= state.ActiveStop;
			bool targetHit = state.Side == 1
				? High[0] >= state.Target
				: Low[0] <= state.Target;
			if (stopHit)
			{
				double basePrice = state.Side == 1
					? Math.Min(state.ActiveStop, Open[0])
					: Math.Max(state.ActiveStop, Open[0]);
				CompleteShadowTrade(
					state,
					basePrice - state.Side * ModelStopSlippageTicks * TickSize);
				return;
			}
			if (targetHit)
			{
				double price = state.Side == 1
					? Math.Max(state.Target, Open[0])
					: Math.Min(state.Target, Open[0]);
				CompleteShadowTrade(state, price);
				return;
			}

			if (closeMinuteEt < FlattenMinuteEt && closeMinuteEt % 15 == 0)
			{
				double closeR = state.Side * (Close[0] - state.Entry) / state.RiskPoints;
				if (closeR >= state.ManagedTriggerR)
				{
					double proposed = RoundPrice(
						state.Entry + state.Side * state.RiskPoints * state.ManagedLockR);
					state.ActiveStop = state.Side == 1
						? Math.Max(state.ActiveStop, proposed)
						: Math.Min(state.ActiveStop, proposed);
				}
				if (closeMinuteEt == ConditionalExitMinuteEt
					&& (closeR < ConditionalLossBoundaryR
						|| closeR >= ConditionalProfitBoundaryR))
					state.PendingConditionalExit = true;
			}
		}

		private void CompleteShadowTrade(ShadowTradeState state, double exitPrice)
		{
			state.ExitPrice = exitPrice;
			state.Complete = true;
			state.PendingConditionalExit = false;
		}

		private void FinalizeShadowPairAtCashClose(double closePrice)
		{
			if (activeShadowPair == null)
				return;
			if (activeShadowPair.Invalid || !activeShadowPair.Initialized)
			{
				activeShadowPair = null;
				return;
			}
			if (!activeShadowPair.Breakout.Complete)
				CompleteShadowTrade(
					activeShadowPair.Breakout,
					closePrice - activeShadowPair.Breakout.Side
						* ModelDayFlatSlippageTicks * TickSize);
			if (!activeShadowPair.Fade.Complete)
				CompleteShadowTrade(
					activeShadowPair.Fade,
					closePrice - activeShadowPair.Fade.Side
						* ModelDayFlatSlippageTicks * TickSize);
			TryRecordCompletedModelLabel();
		}

		private void TryRecordCompletedModelLabel()
		{
			if (activeShadowPair == null || activeShadowPair.Invalid
				|| activeShadowPair.LabelRecorded || !activeShadowPair.Initialized
				|| !activeShadowPair.Breakout.Complete || !activeShadowPair.Fade.Complete)
				return;

			double breakoutNet = activeShadowPair.Breakout.Side
				* (activeShadowPair.Breakout.ExitPrice - activeShadowPair.Breakout.Entry)
				* Instrument.MasterInstrument.PointValue - ModelRoundTurnCost;
			double fadeNet = activeShadowPair.Fade.Side
				* (activeShadowPair.Fade.ExitPrice - activeShadowPair.Fade.Entry)
				* Instrument.MasterInstrument.PointValue - ModelRoundTurnCost;
			bool breakoutProfitable = breakoutNet > 0;
			bool fadeProfitable = fadeNet > 0;
			bool exclusive = breakoutProfitable != fadeProfitable;
			if (exclusive)
			{
				DirectionTrainingRow row = new DirectionTrainingRow();
				row.Session = activeShadowPair.Session;
				row.Features = (double[])activeShadowPair.Features.Clone();
				row.FlipWins = fadeProfitable;
				row.Sequence = trainingSequence++;
				directionTrainingRows.Add(row);
				modelLabelCount++;
			}
			activeShadowPair.LabelRecorded = true;
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"MODEL LABEL {0:yyyy-MM-dd}: breakoutNet={1:F2}, fadeNet={2:F2}, exclusive={3}, included={4}, flipWins={5}.",
				activeShadowPair.Session, breakoutNet, fadeNet, exclusive, exclusive,
				exclusive && fadeProfitable));
			activeShadowPair = null;
		}

		private void ProcessPendingEntryDecision(DateTime barOpenEt, DateTime barCloseEt)
		{
			if (pendingEntryDecision == null)
				return;
			if (barOpenEt < pendingEntryDecision.ExpectedObservationOpenEt)
				return;
			if (barOpenEt != pendingEntryDecision.ExpectedObservationOpenEt
				|| barCloseEt != barOpenEt.AddMinutes(1))
			{
				// Entry refinement requires contiguous completed one-minute bars.
				// A missing observation blocks the date and prevents a late order.
				failClosedCount++;
				Log(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY blocks {0:yyyy-MM-dd}: entry-refinement branch {1} did not receive the exact next observation minute; no late substitute order is allowed.",
					currentCashDate, pendingEntryDecision.Branch), LogLevel.Error);
				pendingEntryDecision = null;
				dayBlocked = true;
				return;
			}

			if (pendingEntryDecision.ObservedBars == 0)
				pendingEntryDecision.FirstObservationOpen = Open[0];
			pendingEntryDecision.ObservedBars++;
			pendingEntryDecision.ExpectedObservationOpenEt = barCloseEt;
			if (pendingEntryDecision.ObservedBars
				< pendingEntryDecision.RequiredObservationBars)
				return;

			int finalSide = pendingEntryDecision.DirectionSide;
			string path = pendingEntryDecision.Branch;
			if (pendingEntryDecision.Branch == "high_vol_touch_vote")
			{
				double move = finalSide
					* (Close[0] - pendingEntryDecision.FirstObservationOpen);
				double fraction = (orbHigh - orbLow) > 0
					? move / (orbHigh - orbLow) : 0.0;
				if (fraction <= HighVolVoteThresholdOrbFraction)
				{
					finalSide = -finalSide;
					path = "high_vol_vote_flip";
					highVolVoteFlipCount++;
				}
				else
					path = "high_vol_vote_keep";
				Diagnostic(string.Format(CultureInfo.InvariantCulture,
					"ENTRY REFINEMENT VOTE {0:yyyy-MM-dd HH:mm}: move={1:F4}, orbFraction={2:F6}, finalSide={3}, path={4}.",
					barCloseEt, move, fraction, finalSide, path));
			}

			bool submit = pendingEntryDecision.SubmitActualOrder;
			string directionSource = pendingEntryDecision.DirectionSource;
			int baselineSide = pendingEntryDecision.BaselineSide;
			double[] features = pendingEntryDecision.Features;
			double signalClose = pendingEntryDecision.SignalClose;
			pendingEntryDecision = null;
			ApplyFinalEntryRefinementOrSubmit(
				finalSide,
				barCloseEt,
				directionSource,
				path,
				submit,
				baselineSide,
				features,
				signalClose);
		}

		private void ApplyFinalEntryRefinementOrSubmit(
			int refinedSide,
			DateTime refinementFillOpenEt,
			string directionSource,
			string refinementPath,
			bool submitActual,
			int baselineSide,
			double[] features,
			double signalClose)
		{
			double orbRange = orbHigh - orbLow;
			if (!(orbRange > 0) || features == null || features.Length != ModelFeatureCount)
			{
				failClosedCount++;
				dayBlocked = true;
				Log("FLAT MOON SOCIETY could not form the final entry conditions; the day is blocked.", LogLevel.Error);
				return;
			}

			double orientation = refinedSide * baselineSide;
			double alignedPriorSessionBps = orientation * features[1];
			double alignedRet30Bps = orientation * features[5];
			double directionalOrbBodyFraction = refinedSide
				* (orbClose - orbOpen) / orbRange;
			double edge = refinedSide == 1 ? orbHigh : orbLow;
			double signalExtensionOrb = refinedSide
				* (signalClose - edge) / orbRange;
			bool priorSessionCondition = alignedPriorSessionBps
				> PriorSessionDisagreementThresholdBps
				&& directionalOrbBodyFraction
					<= PriorSessionDisagreementMaxOrbBodyFraction;
			bool intradayCondition = alignedRet30Bps
				> IntradayContinuationThresholdBps
				&& signalExtensionOrb <= IntradayContinuationMaxSignalExtensionOrb;
			if (priorSessionCondition)
				priorSessionConditionCount++;
			if (intradayCondition)
				intradayConditionCount++;
			if (priorSessionCondition && intradayCondition)
				entryConditionOverlapCount++;

			string branch = priorSessionCondition
				? "prior_session_disagreement"
				: intradayCondition ? "intraday_continuation" : "none";
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"FINAL ENTRY CONDITION {0:yyyy-MM-dd HH:mm}: policy={1}, refinedSide={2}, alignedPrior={3:F3}, orbBody={4:F6}, priorSession={5}, alignedRet30={6:F3}, signalExtension={7:F6}, intraday={8}, overlap={9}, selected={10}.",
				refinementFillOpenEt, EntryRefinementPriorityPolicy, refinedSide,
				alignedPriorSessionBps, directionalOrbBodyFraction, priorSessionCondition,
				alignedRet30Bps, signalExtensionOrb, intradayCondition,
				priorSessionCondition && intradayCondition, branch));
			if (branch == "none")
			{
				if (submitActual)
					SubmitEntry(refinedSide, directionSource,
						refinementPath + "->entry_condition_not_met");
				return;
			}

			pendingFinalEntryDecision = new PendingFinalEntryDecision();
			pendingFinalEntryDecision.ExpectedObservationOpenEt = refinementFillOpenEt;
			pendingFinalEntryDecision.RefinedSide = refinedSide;
			pendingFinalEntryDecision.RequiredObservationBars =
				branch == "prior_session_disagreement"
					? PriorSessionDisagreementObservationBars
					: IntradayContinuationObservationBars;
			pendingFinalEntryDecision.ObservedBars = 0;
			pendingFinalEntryDecision.FirstObservationOpen = 0;
			pendingFinalEntryDecision.SubmitActualOrder = submitActual;
			pendingFinalEntryDecision.Branch = branch;
			pendingFinalEntryDecision.DirectionSource = directionSource;
			pendingFinalEntryDecision.RefinementPath = refinementPath;
		}

		private void ProcessPendingFinalEntryDecision(
			DateTime barOpenEt,
			DateTime barCloseEt)
		{
			if (pendingFinalEntryDecision == null)
				return;
			if (barOpenEt < pendingFinalEntryDecision.ExpectedObservationOpenEt)
				return;
			if (barOpenEt != pendingFinalEntryDecision.ExpectedObservationOpenEt
				|| barCloseEt != barOpenEt.AddMinutes(1))
			{
				// Final entry refinement also requires contiguous completed one-minute
				// bars. A missing observation blocks the date and prevents a late order.
				failClosedCount++;
				Log(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY blocks {0:yyyy-MM-dd}: final entry branch {1} did not receive the exact next observation minute; no late substitute order is allowed.",
					currentCashDate, pendingFinalEntryDecision.Branch), LogLevel.Error);
				pendingFinalEntryDecision = null;
				dayBlocked = true;
				return;
			}

			if (pendingFinalEntryDecision.ObservedBars == 0)
				pendingFinalEntryDecision.FirstObservationOpen = Open[0];
			pendingFinalEntryDecision.ObservedBars++;
			pendingFinalEntryDecision.ExpectedObservationOpenEt = barCloseEt;
			if (pendingFinalEntryDecision.ObservedBars
				< pendingFinalEntryDecision.RequiredObservationBars)
				return;

			int refinedSide = pendingFinalEntryDecision.RefinedSide;
			double move = refinedSide
				* (Close[0] - pendingFinalEntryDecision.FirstObservationOpen);
			int finalSide;
			string path;
			if (pendingFinalEntryDecision.Branch == "prior_session_disagreement")
			{
				finalSide = -refinedSide;
				path = "prior_session_disagreement_reverse";
				priorSessionReversalCount++;
			}
			else if (move > 0.0)
			{
				finalSide = -refinedSide;
				path = "intraday_continuation_reverse";
				intradayReversalCount++;
			}
			else
			{
				finalSide = refinedSide;
				path = "intraday_continuation_keep";
				intradayKeepCount++;
			}

			bool submit = pendingFinalEntryDecision.SubmitActualOrder;
			string source = pendingFinalEntryDecision.DirectionSource
				+ "+" + pendingFinalEntryDecision.Branch;
			string completePath = pendingFinalEntryDecision.RefinementPath + "->" + path;
			string branch = pendingFinalEntryDecision.Branch;
			pendingFinalEntryDecision = null;
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"FINAL ENTRY ACTION {0:yyyy-MM-dd HH:mm}: branch={1}, observedMove={2:F4}, refinedSide={3}, finalSide={4}, path={5}.",
				barCloseEt, branch, move, refinedSide, finalSide, completePath));
			if (submit)
				SubmitEntry(finalSide, source, completePath);
		}

		private void SelectManagementPlan(
			int side,
			bool priorTrendAvailable,
			double priorTrendBps,
			out double targetR,
			out double managedTriggerR,
			out double managedLockR,
			out string regime)
		{
			bool highVol = currentPriorOrbQ75Available
				&& currentOrbBps > currentPriorOrbQ75Bps;
			bool countertrend = priorTrendAvailable && side * priorTrendBps < 0;
			if (highVol)
			{
				targetR = HighOrbTargetR;
				managedTriggerR = BaselineManagedStopTriggerR;
				managedLockR = BaselineManagedStopLockR;
				regime = "high_orb_short_target";
				return;
			}
			if (countertrend)
			{
				targetR = BaselineTargetR;
				managedTriggerR = CountertrendManagedStopTriggerR;
				managedLockR = CountertrendManagedStopLockR;
				regime = "countertrend_early_guard";
				return;
			}
			targetR = BaselineTargetR;
			managedTriggerR = BaselineManagedStopTriggerR;
			managedLockR = BaselineManagedStopLockR;
			regime = "baseline";
		}

		private void InvalidatePendingSessionState(string reason)
		{
			bool hadPending = pendingEntryDecision != null
				|| pendingFinalEntryDecision != null || activeShadowPair != null;
			pendingEntryDecision = null;
			pendingFinalEntryDecision = null;
			activeShadowPair = null;
			if (hadPending)
			{
				failClosedCount++;
				Diagnostic("FAIL CLOSED: " + reason + ".");
			}
		}

		private void SubmitEntry(int side, string directionSource, string refinementPath)
		{
			if (restartRecoveryState != RestartRecoveryState.Historical
				&& restartRecoveryState != RestartRecoveryState.FlatReady)
			{
				failClosedCount++;
				Log("FLAT MOON SOCIETY blocked a new entry because restart recovery state is "
					+ restartRecoveryState.ToString() + ".", LogLevel.Error);
				return;
			}

			bool isLong = side == 1;
			double rawStopDistance = Math.Max(MinimumStopPoints,
				Math.Min(MaximumStopPoints, (orbHigh - orbLow) * StopOrbMultiple));
			int stopTicks = RoundPriceOffsetTicks(rawStopDistance, isLong);
			double targetR;
			double managedTriggerR;
			double managedLockR;
			string managementRegime;
			SelectManagementPlan(
				side,
				currentPriorTrendAvailable,
				currentPriorTrendBps,
				out targetR,
				out managedTriggerR,
				out managedLockR,
				out managementRegime);
			int targetTicks = RoundPriceOffsetTicks(rawStopDistance * targetR, !isLong);

			bool trendAvailable = currentPriorTrendAvailable;
			double priorTrendBps = currentPriorTrendBps;
			bool trendAligned = trendAvailable && side * priorTrendBps >= 0;
			bool countertrend = trendAvailable && !trendAligned;
			bool lowVolatility = currentPriorOrbQ75Available
				&& currentOrbBps <= currentPriorOrbQ75Bps;
			bool highVolatility = currentPriorOrbQ75Available && !lowVolatility;
			bool neutralFallback = !trendAvailable || !currentPriorOrbQ75Available;
			int confidenceScore = neutralFallback
				? 50
				: 50 * ((trendAligned ? 1 : 0) + (lowVolatility ? 1 : 0));

			double targetRiskFraction = baseRiskFraction;
			if (SizingMode == RiskSizingMode.ConfidenceScaledPercent)
			{
				targetRiskFraction = confidenceScore == 0
					? minRiskFraction
					: confidenceScore == 50 ? baseRiskFraction : maxRiskFraction;
			}
			else if (SizingMode == RiskSizingMode.FixedDollar)
				targetRiskFraction = 0.0;

			bool useDefensiveCaps = SizingMode != RiskSizingMode.ConfidenceScaledPercent;
			int contractCap = SizingMode == RiskSizingMode.FixedDollar
				? FixedDollarMaxContracts
				: PortfolioMaxContracts;
			if (useDefensiveCaps && (highVolatility || countertrend))
				contractCap = Math.Min(contractCap, DefensiveMaxContracts);

			double equityBefore;
			double perContractRisk;
			double riskBudget;
			double storedBackAdjustment;
			double rawSignalPriceProxy;
			double rawNotionalPerContract;
			int qRisk;
			int qLeverage;
			int quantity = CalculateQuantity(
				isLong,
				stopTicks,
				contractCap,
				targetRiskFraction,
				out equityBefore,
				out perContractRisk,
				out riskBudget,
				out storedBackAdjustment,
				out rawSignalPriceProxy,
				out rawNotionalPerContract,
				out qRisk,
				out qLeverage);
			activeInitialStopTicks = stopTicks;
			activeManagedStopTriggerR = managedTriggerR;
			activeManagedStopLockR = managedLockR;
			activeManagementRegime = managementRegime;
			managedStopActivated = false;
			conditionalExitRequested = false;

			if (stopTicks < 1 || targetTicks < 1)
			{
				Log("FLAT MOON SOCIETY consumed a breakout but could not form valid stop/target offsets.", LogLevel.Error);
				return;
			}
			if (quantity < 1)
			{
				sizingSkipCount++;
				Log(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY SIZING SKIP {0:yyyy-MM-dd}: mode={1}, score={2}, equity={3:F2}, budget={4:F2}, perContractRisk={5:F2}, qRisk={6}, qLeverage={7}, contractCap={8}. The breakout remains consumed.",
					currentCashDate, SizingMode, confidenceScore, equityBefore,
					riskBudget, perContractRisk, qRisk, qLeverage, contractCap),
					LogLevel.Warning);
				return;
			}

			string dateKey = currentCashDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
			activeEntrySignal = "FTMORB180RC3" + (isLong ? "L" : "S") + dateKey;
			activeExitSignal = "FTMORB180RC3X" + dateKey;
			entryOrder = null;
			stopOrder = null;
			targetOrder = null;
			administrativeExitOrder = null;
			duplicateManagedStopObserved = false;
			duplicateManagedTargetObserved = false;

			SetStopLoss(activeEntrySignal, CalculationMode.Ticks, stopTicks, false);
			SetProfitTarget(activeEntrySignal, CalculationMode.Ticks, targetTicks);

			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"SIGNAL {0}: directionSource={1}, refinement={2}, management={3}, targetR={4:F2}, managed={5:F2}/{6:F2}, mode={7}, close={8:F2}, ORB=[{9:F2},{10:F2}], rawStop={11:F4}, stopTicks={12}, targetTicks={13}, qty={14}, score={15}, targetRisk={16:P3}, neutralFallback={17}, trendAvailable={18}, prior20Bps={19:F3}, trendAligned={20}, priorQ75Available={21}, priorQ75={22:F3}, lowVol={23}, highVol={24}, countertrend={25}, equity={26:F2}, budget={27:F2}, perRisk={28:F2}, qRisk={29}, qLeverage={30}, contractCap={31}, storedAdjustment={32:F2}, rawSignalProxy={33:F2}, rawNotional={34:F2}, {35}.",
				activeEntrySignal, directionSource, refinementPath, managementRegime,
				targetR, managedTriggerR, managedLockR, SizingMode, Close[0], orbLow,
				orbHigh,
				rawStopDistance, stopTicks, targetTicks, quantity, confidenceScore,
				targetRiskFraction, neutralFallback, trendAvailable, priorTrendBps,
				trendAligned,
				currentPriorOrbQ75Available, currentPriorOrbQ75Bps, lowVolatility,
				highVolatility, countertrend, equityBefore, riskBudget, perContractRisk,
				qRisk, qLeverage, contractCap, storedBackAdjustment,
				rawSignalPriceProxy, rawNotionalPerContract, BarClockContext()));

			Order submitted = isLong
				? EnterLong(0, quantity, activeEntrySignal)
				: EnterShort(0, quantity, activeEntrySignal);
			if (entryOrder == null)
				entryOrder = submitted;
			if (entryOrder == null)
				Log("FLAT MOON SOCIETY entry submission returned no order; the day's first breakout remains consumed.", LogLevel.Error);
		}

		private int CalculateQuantity(
			bool isLong,
			int stopTicks,
			int contractCap,
			double targetRiskFraction,
			out double equityBefore,
			out double perContractRisk,
			out double riskBudget,
			out double storedBackAdjustment,
			out double rawSignalPriceProxy,
			out double rawNotionalPerContract,
			out int qRisk,
			out int qLeverage)
		{
			equityBefore = SizingMode == RiskSizingMode.FixedDollar
				? StartingEquity
				: CurrentClosedStrategyEquity();
			perContractRisk = 0;
			riskBudget = 0;
			storedBackAdjustment = 0;
			rawSignalPriceProxy = 0;
			rawNotionalPerContract = 0;
			qRisk = 0;
			qLeverage = 0;

			if (stopTicks < 1 || contractCap < 1 || TickSize <= 0
				|| Instrument == null || Instrument.MasterInstrument == null
				|| (SizingMode != RiskSizingMode.FixedDollar
					&& (!IsFinitePositive(targetRiskFraction)
						|| double.IsNaN(equityBefore) || double.IsInfinity(equityBefore))))
				return 0;

			double pointValue = Instrument.MasterInstrument.PointValue;
			double plannedEntry = Close[0];
			int side = isLong ? 1 : -1;
			double plannedStop = RoundPrice(
				plannedEntry - side * stopTicks * TickSize);
			double signedStopDistance = side * (plannedEntry - plannedStop);
			double reservedStopSlippageTicks = Math.Max(StopSlippageTicks, Slippage);
			perContractRisk = signedStopDistance * pointValue
				+ reservedStopSlippageTicks * TickSize * pointValue
				+ EstimatedRoundTurnCost;
			if (!(signedStopDistance > 0) || !(perContractRisk > 0)
				|| double.IsNaN(perContractRisk) || double.IsInfinity(perContractRisk))
				return 0;

			if (SizingMode == RiskSizingMode.FixedDollar)
			{
				riskBudget = FixedRiskDollars;
				qRisk = FloorNonNegativeToInt(riskBudget / perContractRisk);
				return Math.Min(contractCap, qRisk);
			}

			double positiveEquity = Math.Max(equityBefore, 0.0);
			riskBudget = positiveEquity * targetRiskFraction;
			storedBackAdjustment = UseStoredRolloverOffsets
				? StoredBackAdjustmentFor(currentCashDate)
				: 0.0;
			rawSignalPriceProxy = plannedEntry - storedBackAdjustment;
			if (!IsFinitePositive(rawSignalPriceProxy))
			{
				Log(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY cannot size {0:yyyy-MM-dd}: raw signal-price proxy {1:F4} is invalid (chart close {2:F4}, adjustment {3:F4}).",
					currentCashDate, rawSignalPriceProxy, Close[0], storedBackAdjustment),
					LogLevel.Error);
				return 0;
			}
			rawNotionalPerContract = rawSignalPriceProxy * pointValue;
			qRisk = FloorNonNegativeToInt(riskBudget / perContractRisk);
			qLeverage = FloorNonNegativeToInt(
				positiveEquity * MaxNotionalLeverage / rawNotionalPerContract);
			return Math.Min(contractCap, Math.Min(qRisk, qLeverage));
		}

		private double CurrentClosedStrategyEquity()
		{
			// AllTrades CumProfit contains only closed strategy trades. It does not
			// read Account cash and does not include current open P&L. Configure the
			// Analyzer commission template so this closed P&L is net of actual fees;
			// EstimatedRoundTurnCost above is only a sizing reserve.
			double cumulativeClosedNet =
				SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
			double equity = StartingEquity + cumulativeClosedNet;
			if (double.IsNaN(equity) || double.IsInfinity(equity))
			{
				Log("FLAT MOON SOCIETY closed strategy equity is not finite; the signal will be skipped.", LogLevel.Error);
				return double.NaN;
			}
			return equity;
		}

		private double StoredBackAdjustmentFor(DateTime cashDate)
		{
			int cashDateKey = cashDate.Year * 10000 + cashDate.Month * 100 + cashDate.Day;
			double adjustment = 0;
			for (int index = 0; index < StoredIncomingContractKeys.Length; index++)
			{
				if (StoredIncomingContractKeys[index] <= MergeTargetContract
					&& cashDateKey < StoredRolloverDateKeys[index])
					adjustment += StoredRolloverOffsets[index];
			}
			return adjustment;
		}

		private bool IsKnownStoredTarget(int contractKey)
		{
			// MNQH0 is the captured table's initial contract; subsequent known
			// targets are the incoming contract keys in the stored offset rows.
			if (contractKey == 202003)
				return true;
			for (int index = 0; index < StoredIncomingContractKeys.Length; index++)
				if (StoredIncomingContractKeys[index] == contractKey)
					return true;
			return false;
		}

		private bool StoredRolloverTableIsValid()
		{
			if (StoredIncomingContractKeys.Length != StoredRolloverDateKeys.Length
				|| StoredIncomingContractKeys.Length != StoredRolloverOffsets.Length
				|| StoredIncomingContractKeys.Length == 0)
				return false;
			for (int index = 0; index < StoredIncomingContractKeys.Length; index++)
			{
				if (double.IsNaN(StoredRolloverOffsets[index])
					|| double.IsInfinity(StoredRolloverOffsets[index]))
					return false;
				if (index > 0
					&& (StoredIncomingContractKeys[index] <= StoredIncomingContractKeys[index - 1]
						|| StoredRolloverDateKeys[index] <= StoredRolloverDateKeys[index - 1]))
					return false;
			}
			return true;
		}

		private int FloorNonNegativeToInt(double value)
		{
			if (!(value > 0) || double.IsNaN(value))
				return 0;
			if (double.IsPositiveInfinity(value) || value >= int.MaxValue)
				return int.MaxValue;
			return (int)Math.Floor(value);
		}

		private bool IsFinitePositive(double value)
		{
			return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
		}

		private void RecordCompletedSessionHistory(double closePrice)
		{
			if (rthCloseHistoryRecorded || !sessionDateEligible
				|| !cashWindowIntegrity || !orbFinalized
				|| !referenceSessionOpenCaptured)
				return;
			RecordCompletedOrbHistory();
			RecordCompletedRthCloseHistory(closePrice);
			eligibleSessionCount++;
		}

		private void RecordCompletedRthCloseHistory(double closePrice)
		{
			if (rthCloseHistoryRecorded || !cashWindowIntegrity || !orbFinalized
				|| !referenceSessionOpenCaptured || !(closePrice > 0)
				|| double.IsNaN(closePrice) || double.IsInfinity(closePrice))
				return;
			double sessionReturnBps = SafeReturnBps(closePrice, referenceSessionOpen);
			eligibleRthCloseHistory.Add(closePrice);
			while (eligibleRthCloseHistory.Count > RequiredConfidenceTrendCloses)
				eligibleRthCloseHistory.RemoveAt(0);
			eligibleSessionReturnHistoryBps.Add(sessionReturnBps);
			while (eligibleSessionReturnHistoryBps.Count > 1)
				eligibleSessionReturnHistoryBps.RemoveAt(0);
			rthCloseHistoryRecorded = true;
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"SESSION HISTORY {0:yyyy-MM-dd}: rthClose={1:F2}, sessionOpen={2:F2}, sessionReturnBps={3:F3}, closeRows={4}, orbRows={5}.",
				currentCashDate, closePrice, referenceSessionOpen, sessionReturnBps,
				eligibleRthCloseHistory.Count, eligibleOrbHistoryBps.Count));
		}

		private bool TryGetPriorTrendReturn(out double value)
		{
			value = 0;
			if (eligibleRthCloseHistory == null
				|| eligibleRthCloseHistory.Count < RequiredConfidenceTrendCloses)
				return false;
			double first = eligibleRthCloseHistory[0];
			double last = eligibleRthCloseHistory[eligibleRthCloseHistory.Count - 1];
			if (!(first > 0) || !(last > 0))
				return false;
			value = (last / first - 1.0) * 10000.0;
			return true;
		}

		private void RecordCompletedOrbHistory()
		{
			if (orbHistoryRecorded || !sessionDateEligible
				|| !cashWindowIntegrity || !orbFinalized)
				return;

			eligibleOrbHistoryBps.Add(currentOrbBps);
			while (eligibleOrbHistoryBps.Count > ConfidenceOrbLookback)
				eligibleOrbHistoryBps.RemoveAt(0);
			orbHistoryRecorded = true;
			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"ORB HISTORY {0:yyyy-MM-dd}: appended={1:F3}, retained={2}.",
				currentCashDate, currentOrbBps, eligibleOrbHistoryBps.Count));
		}

		private double LinearPercentile(List<double> values, double quantile)
		{
			if (values == null || values.Count == 0)
				throw new InvalidOperationException("Cannot calculate a percentile from empty ORB history.");

			double[] ordered = values.ToArray();
			Array.Sort(ordered);
			double position = (ordered.Length - 1) * quantile;
			int lower = (int)Math.Floor(position);
			int upper = (int)Math.Ceiling(position);
			if (lower == upper)
				return ordered[lower];
			double weight = position - lower;
			return ordered[lower] + (ordered[upper] - ordered[lower]) * weight;
		}

		private void CancelWorkingEntry()
		{
			if (IsActiveOrder(entryOrder))
				CancelOrder(entryOrder);
		}

		private bool IsActiveOrder(Order order)
		{
			if (order == null)
				return false;
			return order.OrderState == OrderState.Submitted
				|| order.OrderState == OrderState.Accepted
				|| order.OrderState == OrderState.Working
				|| order.OrderState == OrderState.PartFilled
				|| order.OrderState == OrderState.ChangePending
				|| order.OrderState == OrderState.ChangeSubmitted
				|| order.OrderState == OrderState.CancelPending
				|| order.OrderState == OrderState.CancelSubmitted;
		}

		private void RequestAdministrativeExit(string reason)
		{
			if (Position.MarketPosition == MarketPosition.Flat)
				return;
			if (State == State.Realtime
				&& System.Threading.Interlocked.CompareExchange(
					ref administrativeExitPlatformFailureLatched, 0, 0) != 0)
			{
				RequireManualRestartReconciliation(
					"A prior managed administrative-exit rejection/error belongs to NinjaTrader's StopCancelClose path; RC3 blocks another exit request: " + reason + ".");
				return;
			}
			if (State == State.Realtime
				&& restartRecoveryState == RestartRecoveryState.ManualReconciliationRequired)
			{
				Diagnostic("ADMIN EXIT blocked because this instance requires manual restart reconciliation: " + reason + ".");
				return;
			}
			if (State == State.Realtime && administrativeExitFillAwaitingExecution)
			{
				Diagnostic("ADMIN EXIT retry blocked until OnExecutionUpdate processes the reported fill: " + reason + ".");
				return;
			}
			if (administrativeExitOrder != null)
			{
				if (administrativeExitOrder.OrderState == OrderState.Unknown)
				{
					if (State == State.Realtime)
						RequireManualRestartReconciliation(
							"The managed administrative exit is in Unknown state; RC3 will not submit another exit while its live/terminal status is uncertain.");
					else
						Log("FLAT MOON SOCIETY administrative exit is in Unknown state; no duplicate exit will be submitted.", LogLevel.Error);
					return;
				}
				if (IsLifecycleActiveOrder(administrativeExitOrder))
					return;
			}
			if (administrativeExitAttempts >= MaxAdministrativeExitAttempts)
			{
				Log("FLAT MOON SOCIETY exhausted its bounded administrative-exit retries. Reconcile the strategy/account position manually.", LogLevel.Error);
				if (restartRecoveryState == RestartRecoveryState.FailClosedExitPending)
					RequireManualRestartReconciliation(
						"The bounded managed administrative-exit attempts were exhausted while exposure remained open.");
				return;
			}
			if (State == State.Realtime
				&& restartRecoveryState == RestartRecoveryState.FailClosedExitPending
				&& !CanRequestBoundedManagedRecoveryExit())
			{
				RequireManualRestartReconciliation(
					"The complete current-instance position, ownership, protection, and account-order guard failed before a bounded managed recovery-exit attempt or retry.");
				return;
			}

			if (State == State.Realtime
				&& System.Threading.Interlocked.CompareExchange(
					ref administrativeExitPlatformFailureLatched, 0, 0) != 0)
			{
				RequireManualRestartReconciliation(
					"An administrative rejection/error was latched immediately before another exit submission; RC3 will not race NinjaTrader's StopCancelClose action.");
				return;
			}

			administrativeExitAttempts++;
			activeExitSignal = "FTMORB180RC3X" + currentCashDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
				+ administrativeExitAttempts.ToString(CultureInfo.InvariantCulture);
			administrativeExitOrder = null;
			int quantity = Position.Quantity;
			Order submitted = Position.MarketPosition == MarketPosition.Long
				? ExitLong(0, quantity, activeExitSignal, activeEntrySignal)
				: ExitShort(0, quantity, activeExitSignal, activeEntrySignal);
			if (administrativeExitOrder == null)
				administrativeExitOrder = submitted;

			Diagnostic(string.Format(CultureInfo.InvariantCulture,
				"ADMIN EXIT {0}: reason={1}, qty={2}, attempt={3}, {4}.",
				activeExitSignal, reason, quantity, administrativeExitAttempts,
				BarClockContext()));
			if (administrativeExitOrder == null)
			{
				Log("FLAT MOON SOCIETY administrative exit returned no order. ExitOnSessionClose remains enabled; reconcile manually if exposure persists.", LogLevel.Error);
				if (restartRecoveryState == RestartRecoveryState.FailClosedExitPending)
					RequireManualRestartReconciliation(
						"The bounded managed administrative exit returned no order while exposure remained open.");
			}
		}

		private bool ValidateConfiguration()
		{
			bool valid = true;
			if (!Enum.IsDefined(typeof(RiskSizingMode), SizingMode))
				valid = ConfigurationError("Sizing Mode is invalid.");

			DateTime parsedPortfolioStart;
			if (!DateTime.TryParseExact(
				TradingStartDate.ToString("D8", CultureInfo.InvariantCulture),
				"yyyyMMdd",
				CultureInfo.InvariantCulture,
				DateTimeStyles.None,
				out parsedPortfolioStart))
				valid = ConfigurationError("Trading Start Date must be a valid YYYYMMDD date.");
			else
				portfolioStartCashDate = parsedPortfolioStart.Date;

			if (SizingMode == RiskSizingMode.FixedDollar)
			{
				if (!IsFinitePositive(FixedRiskDollars))
					valid = ConfigurationError("Fixed Risk Dollars must be finite and positive.");
				if (FixedDollarMaxContracts < 1)
					valid = ConfigurationError("Fixed-Dollar Max Contracts must be at least one.");
			}
			else
			{
				if (!IsFinitePositive(StartingEquity))
					valid = ConfigurationError("Starting Equity must be finite and positive.");
				if (!IsFinitePositive(BaseRiskPercent) || BaseRiskPercent > 10.0)
					valid = ConfigurationError("Base Risk Percent must be finite, positive, and no greater than 10.");
				if (SizingMode == RiskSizingMode.ConfidenceScaledPercent
					&& (!IsFinitePositive(MinRiskPercent)
						|| !IsFinitePositive(MaxRiskPercent)
						|| MaxRiskPercent > 10.0
						|| MinRiskPercent > BaseRiskPercent
						|| BaseRiskPercent > MaxRiskPercent))
					valid = ConfigurationError("Confidence risk percentages must be finite, positive, ordered low <= base <= high, and no greater than 10.");
				if (PortfolioMaxContracts < 1)
					valid = ConfigurationError("Portfolio Max Contracts must be at least one.");
				if (!IsFinitePositive(MaxNotionalLeverage))
					valid = ConfigurationError("Max Notional Leverage must be finite and positive.");
				if (UseStoredRolloverOffsets && !IsKnownStoredTarget(MergeTargetContract))
					valid = ConfigurationError("Merge Target Contract is not present in the captured MNQ rollover table.");
			}

			// Percent inputs are converted once during load. Inactive values are
			// retained only for display and cannot change fixed-dollar quantity.
			minRiskFraction = MinRiskPercent / 100.0;
			baseRiskFraction = BaseRiskPercent / 100.0;
			maxRiskFraction = MaxRiskPercent / 100.0;
			if (double.IsNaN(EstimatedRoundTurnCost)
				|| double.IsInfinity(EstimatedRoundTurnCost)
				|| EstimatedRoundTurnCost < 0)
				valid = ConfigurationError("Estimated Round-Turn Cost must be finite and nonnegative.");
			if (StopSlippageTicks < 0)
				valid = ConfigurationError("Stop Slippage Ticks cannot be negative.");
			if (Slippage < 0)
				valid = ConfigurationError("NinjaTrader strategy Slippage cannot be negative.");
			if (!StoredRolloverTableIsValid())
				valid = ConfigurationError("The embedded MNQ rollover-offset table is invalid.");
			if (ModelFeatureNames.Length != ModelFeatureCount)
				valid = ConfigurationError("The embedded direction feature contract is invalid.");
			if (!DateKeysStrictlyIncreasing(ContractRollExclusionDateKeys)
				|| !DateKeysStrictlyIncreasing(DegradedDataExclusionDateKeys))
				valid = ConfigurationError("The embedded data-quality exclusion dates are invalid.");
			if (Calculate != Calculate.OnBarClose)
				valid = ConfigurationError("Calculate must be OnBarClose.");
			if (StartBehavior != StartBehavior.ImmediatelySubmit)
				valid = ConfigurationError("Start Behavior must be ImmediatelySubmit without account synchronization.");
			if (TimeInForce != TimeInForce.Gtc)
				valid = ConfigurationError("Time in force must be GTC for broker-held restart protection.");
			if (RealtimeErrorHandling != RealtimeErrorHandling.StopCancelClose)
				valid = ConfigurationError("Realtime Error Handling must retain NinjaTrader's StopCancelClose safety backstop.");
			if (!IsExitOnSessionCloseStrategy)
				valid = ConfigurationError("Exit on session close must remain enabled as a platform-owned safety backstop.");
			if (ExitOnSessionCloseSeconds != 30)
				valid = ConfigurationError("Exit on session close must remain 30 seconds before the Trading Hours session end.");
			if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
				valid = ConfigurationError("Primary bars must be Minute / 1.");
			if (platformTimeZone == null || easternTimeZone == null)
				valid = ConfigurationError("Platform or New York time-zone metadata is unavailable.");
			else
			{
				timeZoneContractValidated = ValidateTimeZoneContract();
				if (!timeZoneContractValidated)
					valid = ConfigurationError("The platform-to-New-York DST conversion self-test failed.");
			}
			if (Instrument == null || Instrument.MasterInstrument == null)
				valid = ConfigurationError("Instrument metadata is unavailable.");
			else
			{
				if (!string.Equals(Instrument.MasterInstrument.Name, "MNQ", StringComparison.OrdinalIgnoreCase))
					valid = ConfigurationError("Master instrument must be MNQ.");
				if (Math.Abs(TickSize - 0.25) > 0.0000001)
					valid = ConfigurationError("MNQ tick size must be 0.25.");
				if (Math.Abs(Instrument.MasterInstrument.PointValue - 2.0) > 0.0000001)
					valid = ConfigurationError("MNQ point value must be 2.00.");
			}

			if (Bars == null || Bars.TradingHours == null
				|| Bars.TradingHours.TimeZoneInfo == null)
				valid = ConfigurationError("Trading Hours time-zone metadata is unavailable.");
			string tradingHoursName = Bars != null && Bars.TradingHours != null
				? Bars.TradingHours.Name : string.Empty;
			if (string.IsNullOrEmpty(tradingHoursName)
				|| tradingHoursName.IndexOf(RequiredTradingHoursText, StringComparison.OrdinalIgnoreCase) < 0)
				valid = ConfigurationError("Trading Hours template '" + tradingHoursName
					+ "' does not contain required text '" + RequiredTradingHoursText + "'.");
			return valid;
		}

		private bool ConfigurationError(string message)
		{
			Log("FLAT MOON SOCIETY configuration error: " + message + " No orders will be submitted.", LogLevel.Error);
			return false;
		}

		private void PrintStartupDiagnostics()
		{
			string tradingHoursName = Bars != null && Bars.TradingHours != null
				? Bars.TradingHours.Name : "(unknown)";
			string tradingHoursZoneName = Bars != null && Bars.TradingHours != null
				&& Bars.TradingHours.TimeZoneInfo != null
				? Bars.TradingHours.TimeZoneInfo.Id : "(unavailable)";
			string instrumentName = Instrument != null ? Instrument.FullName : "(unknown)";
			string platformZoneName = platformTimeZone != null ? platformTimeZone.Id : "(unavailable)";
			string easternZoneName = easternTimeZone != null ? easternTimeZone.Id : "(unavailable)";
			bool usesValidatedDefaultProfile = UsesValidatedDefaultProfile();
			Print(string.Format(CultureInfo.InvariantCulture,
				"FLAT MOON SOCIETY v{0} / NT adapter v{1} startup | valid={2} | mode={3} | instrument={4} | bars={5}/{6} | tradingHours={7} | platformZone={8} | tradingHoursZone={9} | newYorkZone={10} | slippageTicks={11} | tickSize={12:F4} | pointValue={13:F2} | timeContract={14} | fixedRisk={15:F2} | fixedCap={16} | startEquity={17:F2} | tradingStart={18} | riskPct={19:F3}/{20:F3}/{21:F3} | portfolioCap={22} | leverageCap={23:F2} | stopSlipReserve={24} | sizingCost={25:F2} | storedOffsets={26} | mergeTarget={27} | profile={28}",
				StrategyVersion, NtAdapterVersion, configurationValid, SizingMode,
				instrumentName, BarsPeriod.BarsPeriodType, BarsPeriod.Value,
				tradingHoursName, platformZoneName, tradingHoursZoneName,
				easternZoneName, Slippage,
				Instrument != null ? TickSize : 0,
				Instrument != null && Instrument.MasterInstrument != null
					? Instrument.MasterInstrument.PointValue : 0,
				timeZoneContractValidated ? "PASS" : "FAIL",
				FixedRiskDollars, FixedDollarMaxContracts, StartingEquity,
				TradingStartDate, MinRiskPercent, BaseRiskPercent, MaxRiskPercent,
				PortfolioMaxContracts, MaxNotionalLeverage, StopSlippageTicks,
				EstimatedRoundTurnCost, UseStoredRolloverOffsets, MergeTargetContract,
				usesValidatedDefaultProfile ? "VALIDATED_DEFAULTS" : "CUSTOM_UNVALIDATED"));
			if (!usesValidatedDefaultProfile)
				Log(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY CUSTOM PROFILE WARNING: mode={0}, fixedRisk={1:F2}, fixedCap={2}, startEquity={3:F2}, tradingStart={4}, riskPct={5:F3}/{6:F3}/{7:F3}, portfolioCap={8}, leverageCap={9:F2}, strategySlippageTicks={10}, stopSlipReserve={11}, sizingCost={12:F2}, storedOffsets={13}, mergeTarget={14}. This exact profile requires fresh Strategy Analyzer and simulated-account validation.",
					SizingMode, FixedRiskDollars, FixedDollarMaxContracts, StartingEquity,
					TradingStartDate, MinRiskPercent, BaseRiskPercent, MaxRiskPercent,
					PortfolioMaxContracts, MaxNotionalLeverage, Slippage,
					StopSlippageTicks, EstimatedRoundTurnCost,
					UseStoredRolloverOffsets, MergeTargetContract),
					LogLevel.Warning);
			Print("FLAT MOON SOCIETY NATIVE VALIDATION STATUS: " + NativeValidationStatus
				+ ". This exact source requires NinjaTrader compilation and row-for-row Strategy Analyzer reconciliation before release or live use.");
			Print("FLAT MOON SOCIETY reminder: actual orders require 120 prior eligible ORBs and 21 prior eligible RTH closes; load pre-start warm-up data, enable Analyzer commissions once, and use stored offsets only with Merge back adjusted data through MNQ 09-26.");
			Print("FLAT MOON SOCIETY direction model: qualifying labels from 2021 onward, quarterly expanding population scaling, 15 nearest neighbors, Laplace-smoothed flip probability above 0.65, and fixed per-contract label costs.");
			Print(string.Format(CultureInfo.InvariantCulture,
				"FLAT MOON SOCIETY entry policy: policy={0} | direct requires elapsed15={1:F1} and alignedVwap<={2:F1}bps and uses the baseline breakout entry | all other opportunities use the integrated refinement policy={3}.",
				Rc1PriorityPolicy,
				Rc1DirectElapsedSignal15m,
				Rc1DirectMaxAlignedVwapDistanceBps,
				EntryRefinementPriorityPolicy));
			Print(string.Format(CultureInfo.InvariantCulture,
				"FLAT MOON SOCIETY final entry refinement: policy={0} | prior-session disagreement requires alignedPrior>{1:F1}bps and directionalOrbBody<={2:F2}, observes {3} completed one-minute bars, then reverses | intraday continuation requires alignedRet30>{4:F1}bps and signalExtension<={5:F2} ORB, observes {6} completed one-minute bar, then reverses only when aligned move>0 | submit after the final completed observation.",
				EntryRefinementPriorityPolicy,
				PriorSessionDisagreementThresholdBps,
				PriorSessionDisagreementMaxOrbBodyFraction,
				PriorSessionDisagreementObservationBars,
				IntradayContinuationThresholdBps,
				IntradayContinuationMaxSignalExtensionOrb,
				IntradayContinuationObservationBars));
			if (SizingMode != RiskSizingMode.FixedDollar)
				Print("FLAT MOON SOCIETY percentage sizing uses Starting Equity plus this strategy instance's closed SystemPerformance P&L; it does not read brokerage-account cash or open P&L.");
		}

		private bool UsesValidatedDefaultProfile()
		{
			return SizingMode == RiskSizingMode.FixedDollar
				&& Math.Abs(FixedRiskDollars - 535.0) <= 0.000000001
				&& FixedDollarMaxContracts == 2
				&& TradingStartDate == 20210101
				&& Math.Abs(EstimatedRoundTurnCost - 2.50) <= 0.000000001
				&& Slippage == 1
				&& StopSlippageTicks == 1;
		}

		private void PrintAnalyzerSummary()
		{
			Print(string.Format(CultureInfo.InvariantCulture,
				"FLAT MOON SOCIETY v{0} ANALYZER SUMMARY | eligibleSessions={1} | admitted={2} | geometryRejects={3} | touchVetoes={4} | priorOverrides={5} | knnPredictions={6} | knnOverrides={7} | weakDelays={8} | highVolVotes={9} | voteFlips={10} | priorSessionConditions={11} | intradayConditions={12} | entryConditionOverlaps={13} | priorSessionReversals={14} | intradayKeeps={15} | intradayReversals={16} | modelLabels={17} | trainingRows={18} | sizingSkips={19} | contextWarmupSkips={20} | failClosed={21} | sizingMode={22}",
				StrategyVersion, eligibleSessionCount, admittedSignalCount,
				geometryRejectCount, touchVetoCount, priorDayOverrideCount,
				knnPredictionCount, knnOverrideCount, weakDelayCount,
				highVolVoteCount, highVolVoteFlipCount, priorSessionConditionCount,
				intradayConditionCount, entryConditionOverlapCount,
				priorSessionReversalCount, intradayKeepCount,
				intradayReversalCount, modelLabelCount,
				directionTrainingRows != null ? directionTrainingRows.Count : 0,
				sizingSkipCount, contextWarmupSkipCount, failClosedCount, SizingMode));
			Print(string.Format(CultureInfo.InvariantCulture,
				"FLAT MOON SOCIETY ENTRY ROUTE DECISIONS | direct={0} | integratedRefinement={1} | total={2} | nativeStatus={3}",
				rc1DirectDecisionCount, rc1ParentDecisionCount,
				rc1DirectDecisionCount + rc1ParentDecisionCount,
				NativeValidationStatus));
		}

		private void InitializeRuntimeState()
		{
			eligibleOrbHistoryBps = new List<double>();
			eligibleRthCloseHistory = new List<double>();
			eligibleSessionReturnHistoryBps = new List<double>();
			currentRthMinuteCloses = new List<double>();
			directionTrainingRows = new List<DirectionTrainingRow>();
			scheduleKnown = false;
			timeZoneContractValidated = false;
			barClockContextAvailable = false;
			lastBarOpenPlatform = DateTime.MinValue;
			lastBarOpenUtc = DateTime.MinValue;
			lastBarOpenEt = DateTime.MinValue;
			lastBarClosePlatform = DateTime.MinValue;
			lastBarCloseUtc = DateTime.MinValue;
			lastBarCloseEt = DateTime.MinValue;
			currentCashDate = DateTime.MinValue;
			sessionDateEligible = false;
			referenceSessionOpenCaptured = false;
			referenceSessionOpen = 0;
			dayBlocked = true;
			sessionEnding = false;
			orbFinalized = false;
			missingOrbLogged = false;
			breakoutConsumed = false;
			cashExitTriggered = false;
			cashWindowIntegrity = false;
			orbHistoryRecorded = false;
			rthCloseHistoryRecorded = false;
			activeInitialStopTicks = 0;
			managedStopActivated = false;
			conditionalExitRequested = false;
			orbBarCount = 0;
			expectedNextOrbOpenMinute = OrbStartMinuteEt;
			orbHigh = double.MinValue;
			orbLow = double.MaxValue;
			orbOpen = 0;
			orbClose = 0;
			currentOrbBps = 0;
			portfolioStartCashDate = DateTime.MinValue;
			minRiskFraction = 0;
			baseRiskFraction = 0;
			maxRiskFraction = 0;
			currentPriorOrbQ75Bps = 0;
			currentPriorOrbQ75Available = false;
			currentPriorTrendBps = 0;
			currentPriorTrendAvailable = false;
			currentRthTypicalVolumeSum = 0;
			currentRthVolumeSum = 0;
			lastCashBarCloseEt = DateTime.MinValue;
			activeManagedStopTriggerR = 0;
			activeManagedStopLockR = 0;
			activeManagementRegime = string.Empty;
			pendingEntryDecision = null;
			pendingFinalEntryDecision = null;
			activeShadowPair = null;
			activeQuarterModel = null;
			activeQuarterKey = 0;
			trainingSequence = 0;
			eligibleSessionCount = 0;
			admittedSignalCount = 0;
			geometryRejectCount = 0;
			touchVetoCount = 0;
			priorDayOverrideCount = 0;
			knnPredictionCount = 0;
			knnOverrideCount = 0;
			weakDelayCount = 0;
			highVolVoteCount = 0;
			highVolVoteFlipCount = 0;
			priorSessionConditionCount = 0;
			intradayConditionCount = 0;
			entryConditionOverlapCount = 0;
			priorSessionReversalCount = 0;
			intradayKeepCount = 0;
			intradayReversalCount = 0;
			modelLabelCount = 0;
			sizingSkipCount = 0;
			contextWarmupSkipCount = 0;
			failClosedCount = 0;
			rc1DirectDecisionCount = 0;
			rc1ParentDecisionCount = 0;
			activeEntrySignal = string.Empty;
			activeExitSignal = string.Empty;
			entryOrder = null;
			stopOrder = null;
			targetOrder = null;
			administrativeExitOrder = null;
			administrativeExitAttempts = 0;
			administrativeExitFillAwaitingExecution = false;
			administrativeExitPlatformFailureLatched = 0;
			duplicateManagedStopObserved = false;
			duplicateManagedTargetObserved = false;
			managedProtectionReferenceAmbiguous = false;
			protectiveFillObserved = false;
			accountOrderSetMismatchObserved = false;
			liveManagedProtectionObserved = false;
			restartProtectionDegraded = false;
			restartProtectionAuditQueued = 0;
			restartFlatConfirmationPending = false;
			ClearRestartOpenFirstSnapshot();
			restartRecoveryState = RestartRecoveryState.Historical;
			restartRecoveryCashDate = DateTime.MinValue;
		}

		private TimeZoneInfo ResolveEasternTimeZone()
		{
			try
			{
				return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
			}
			catch (TimeZoneNotFoundException)
			{
				return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
			}
		}

		private bool ValidateTimeZoneContract()
		{
			try
			{
				// These fixed New York wall-clock mappings cover winter, summer,
				// and both weeks where US and European DST schedules disagree.
				return ValidateTimeZoneSample(
					new DateTime(2024, 1, 16, 9, 30, 0, DateTimeKind.Unspecified),
					new DateTime(2024, 1, 16, 14, 30, 0, DateTimeKind.Utc))
					&& ValidateTimeZoneSample(
						new DateTime(2024, 3, 15, 9, 30, 0, DateTimeKind.Unspecified),
						new DateTime(2024, 3, 15, 13, 30, 0, DateTimeKind.Utc))
					&& ValidateTimeZoneSample(
						new DateTime(2024, 7, 15, 9, 30, 0, DateTimeKind.Unspecified),
						new DateTime(2024, 7, 15, 13, 30, 0, DateTimeKind.Utc))
					&& ValidateTimeZoneSample(
						new DateTime(2024, 10, 28, 9, 30, 0, DateTimeKind.Unspecified),
						new DateTime(2024, 10, 28, 13, 30, 0, DateTimeKind.Utc))
					&& ValidateTimeZoneSample(
						new DateTime(2024, 11, 15, 9, 30, 0, DateTimeKind.Unspecified),
						new DateTime(2024, 11, 15, 14, 30, 0, DateTimeKind.Utc));
			}
			catch (Exception ex)
			{
				Log("FLAT MOON SOCIETY time-zone conversion self-test failed: "
					+ ex.Message, LogLevel.Error);
				return false;
			}
		}

		private bool ValidateTimeZoneSample(DateTime expectedEastern, DateTime expectedUtc)
		{
			if (easternTimeZone.IsInvalidTime(expectedEastern)
				|| easternTimeZone.IsAmbiguousTime(expectedEastern))
				return false;

			DateTime directUtc = TimeZoneInfo.ConvertTimeToUtc(
				expectedEastern, easternTimeZone);
			DateTime platformWall = DateTime.SpecifyKind(
				TimeZoneInfo.ConvertTimeFromUtc(expectedUtc, platformTimeZone),
				DateTimeKind.Unspecified);
			if (platformTimeZone.IsInvalidTime(platformWall)
				|| platformTimeZone.IsAmbiguousTime(platformWall))
				return false;
			DateTime convertedEastern = ToEastern(platformWall);
			DateTime roundTripUtc = ToUtc(platformWall);
			bool valid = directUtc == expectedUtc
				&& convertedEastern == expectedEastern
				&& roundTripUtc == expectedUtc;
			if (!valid)
				Log(string.Format(CultureInfo.InvariantCulture,
					"FLAT MOON SOCIETY time-zone sample mismatch: expectedET={0:yyyy-MM-dd HH:mm}, expectedUTC={1:yyyy-MM-dd HH:mm}, platform={2:yyyy-MM-dd HH:mm}, observedET={3:yyyy-MM-dd HH:mm}, observedUTC={4:yyyy-MM-dd HH:mm}.",
					expectedEastern, expectedUtc, platformWall,
					convertedEastern, roundTripUtc), LogLevel.Error);
			return valid;
		}

		private DateTime ToEastern(DateTime platformTime)
		{
			return FromUtcToEastern(ToUtc(platformTime));
		}

		private DateTime FromUtcToEastern(DateTime utcTime)
		{
			DateTime utc = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
			return TimeZoneInfo.ConvertTimeFromUtc(utc, easternTimeZone);
		}

		private DateTime ToUtc(DateTime platformTime)
		{
			DateTime unspecified = DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified);
			return TimeZoneInfo.ConvertTimeToUtc(unspecified, platformTimeZone);
		}

		private int RoundPriceOffsetTicks(double points, bool priceBelowEntry)
		{
			// Absolute-price half-up rounding: with a tick-aligned entry, an exact
			// half tick rounds toward entry below it and away above it.
			double exactTicks = points / TickSize;
			return priceBelowEntry
				? (int)Math.Ceiling(exactTicks - 0.5)
				: (int)Math.Floor(exactTicks + 0.5);
		}

		private double RoundPrice(double price)
		{
			return Instrument.MasterInstrument.RoundToTickSize(price);
		}

		private int MinuteOfDay(DateTime value)
		{
			return value.Hour * 60 + value.Minute;
		}

		private bool IsExactMinute(DateTime value)
		{
			return value.Ticks % TimeSpan.TicksPerMinute == 0;
		}

		private void CaptureBarClockContext(
			DateTime barOpenPlatform,
			DateTime barOpenUtc,
			DateTime barOpenEt,
			DateTime barClosePlatform,
			DateTime barCloseUtc,
			DateTime barCloseEt)
		{
			lastBarOpenPlatform = DateTime.SpecifyKind(
				barOpenPlatform, DateTimeKind.Unspecified);
			lastBarOpenUtc = DateTime.SpecifyKind(barOpenUtc, DateTimeKind.Utc);
			lastBarOpenEt = DateTime.SpecifyKind(barOpenEt, DateTimeKind.Unspecified);
			lastBarClosePlatform = DateTime.SpecifyKind(
				barClosePlatform, DateTimeKind.Unspecified);
			lastBarCloseUtc = DateTime.SpecifyKind(barCloseUtc, DateTimeKind.Utc);
			lastBarCloseEt = DateTime.SpecifyKind(barCloseEt, DateTimeKind.Unspecified);
			barClockContextAvailable = true;
		}

		private string BarClockContext()
		{
			if (!barClockContextAvailable || platformTimeZone == null)
				return "strategyBarClock=unavailable";
			return string.Format(CultureInfo.InvariantCulture,
				"barOpenPlatform={0:yyyy-MM-dd HH:mm:ss} [{1}], barOpenUTC={2:yyyy-MM-dd HH:mm:ss}Z, barOpenET={3:yyyy-MM-dd HH:mm:ss}, barClosePlatform={4:yyyy-MM-dd HH:mm:ss} [{1}], barCloseUTC={5:yyyy-MM-dd HH:mm:ss}Z, barCloseET={6:yyyy-MM-dd HH:mm:ss}",
				lastBarOpenPlatform, platformTimeZone.Id, lastBarOpenUtc,
				lastBarOpenEt, lastBarClosePlatform, lastBarCloseUtc,
				lastBarCloseEt);
		}

		private bool IsWeekday(DayOfWeek day)
		{
			return day >= DayOfWeek.Monday && day <= DayOfWeek.Friday;
		}

		private int DateKey(DateTime value)
		{
			return value.Year * 10000 + value.Month * 100 + value.Day;
		}

		private bool ContainsDateKey(int[] values, int value)
		{
			return Array.BinarySearch(values, value) >= 0;
		}

		private bool DateKeysStrictlyIncreasing(int[] values)
		{
			if (values == null)
				return false;
			for (int index = 1; index < values.Length; index++)
				if (values[index] <= values[index - 1])
					return false;
			return true;
		}

		private void Diagnostic(string message)
		{
			if (EnableDiagnostics)
				Print("FLAT MOON SOCIETY v" + StrategyVersion + " | " + message);
		}

		#region Properties
		[NinjaScriptProperty]
		[ReadOnly(true)]
		[Display(Name = "Release Version", GroupName = "0. Release", Order = 0)]
		public string VersionLabel { get { return StrategyVersion; } set { } }

		[NinjaScriptProperty]
		[ReadOnly(true)]
		[Display(Name = "NT Adapter Version", GroupName = "0. Release", Order = 1)]
		public string AdapterVersionLabel { get { return NtAdapterVersion; } set { } }

		[NinjaScriptProperty]
		[ReadOnly(true)]
		[Display(Name = "Native Validation Status", GroupName = "0. Release", Order = 2)]
		public string NativeValidationStatusLabel { get { return NativeValidationStatus; } set { } }

		[NinjaScriptProperty]
		[Display(Name = "Sizing Mode", Description = "Select fixed USD, closed-equity percentage, or confidence-scaled percentage sizing.", GroupName = "1. Risk sizing", Order = 0)]
		public RiskSizingMode SizingMode { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, 1000000000.0)]
		[Display(Name = "Fixed Risk Per Trade ($)", Description = "Used only by FixedDollar mode.", GroupName = "1. Risk sizing", Order = 1)]
		public double FixedRiskDollars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Fixed-Dollar Max Contracts", Description = "Maximum contracts in FixedDollar mode before one-contract defensive caps.", GroupName = "1. Risk sizing", Order = 2)]
		public int FixedDollarMaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 1000000000.0)]
		[Display(Name = "Starting Equity ($)", Description = "Strategy sleeve equity used by both percentage modes; not brokerage-account cash.", GroupName = "1. Risk sizing", Order = 3)]
		public double StartingEquity { get; set; }

		[NinjaScriptProperty]
		[Range(19000101, 20991231)]
		[Display(Name = "Trading Start Date (YYYYMMDD)", Description = "Earlier loaded sessions are warm-up only.", GroupName = "1. Risk sizing", Order = 4)]
		public int TradingStartDate { get; set; }

		[NinjaScriptProperty]
		[Range(0.0001, 10.0)]
		[Display(Name = "Confidence Low Risk (%)", Description = "0-point confidence tier; used only by ConfidenceScaledPercent mode.", GroupName = "1. Risk sizing", Order = 5)]
		public double MinRiskPercent { get; set; }

		[NinjaScriptProperty]
		[Range(0.0001, 10.0)]
		[Display(Name = "Base Risk Per Trade (%)", Description = "Used by ClosedEquityPercent and as the 50-point confidence tier.", GroupName = "1. Risk sizing", Order = 6)]
		public double BaseRiskPercent { get; set; }

		[NinjaScriptProperty]
		[Range(0.0001, 10.0)]
		[Display(Name = "Confidence High Risk (%)", Description = "100-point confidence tier; used only by ConfidenceScaledPercent mode.", GroupName = "1. Risk sizing", Order = 7)]
		public double MaxRiskPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Portfolio Max Contracts", Description = "Hard cap used by both percentage modes.", GroupName = "1. Risk sizing", Order = 8)]
		public int PortfolioMaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, 100.0)]
		[Display(Name = "Max Raw-Notional Leverage", Description = "Leverage cap used by both percentage modes.", GroupName = "1. Risk sizing", Order = 9)]
		public double MaxNotionalLeverage { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 1000.0)]
		[Display(Name = "Estimated Round-Turn Cost / Contract", Description = "Sizing reserve only; does not post commissions.", GroupName = "2. Risk model", Order = 0)]
		public double EstimatedRoundTurnCost { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Stop Slippage Reserve (ticks)", GroupName = "2. Risk model", Order = 1)]
		public int StopSlippageTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Stored Rollover Offsets", Description = "Enable only for NinjaTrader Merge back adjusted data through MNQ 09-26.", GroupName = "3. Price reconstruction", Order = 0)]
		public bool UseStoredRolloverOffsets { get; set; }

		[NinjaScriptProperty]
		[Range(202003, 202609)]
		[Display(Name = "Merge Target Contract (YYYYMM)", GroupName = "3. Price reconstruction", Order = 1)]
		public int MergeTargetContract { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Detailed Output", GroupName = "4. Diagnostics", Order = 0)]
		public bool EnableDiagnostics { get; set; }
		#endregion
	}
}
