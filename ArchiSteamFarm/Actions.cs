﻿//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// 
// Copyright 2015-2018 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Json;
using ArchiSteamFarm.Localization;
using SteamKit2;

namespace ArchiSteamFarm {
	internal sealed class Actions : IDisposable {
		private static readonly SemaphoreSlim GiftCardsSemaphore = new SemaphoreSlim(1, 1);
		private static readonly SemaphoreSlim GiftsSemaphore = new SemaphoreSlim(1, 1);

		private readonly Bot Bot;
		private readonly ConcurrentHashSet<ulong> HandledGifts = new ConcurrentHashSet<ulong>();
		private readonly SemaphoreSlim LootingSemaphore = new SemaphoreSlim(1, 1);

		private bool LootingAllowed = true;
		private bool LootingScheduled;
		private bool ProcessingGiftsScheduled;

		internal Actions(Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

		public void Dispose() => LootingSemaphore.Dispose();

		internal async Task<bool> AcceptConfirmations(bool accept, Steam.ConfirmationDetails.EType acceptedType = Steam.ConfirmationDetails.EType.Unknown, ulong acceptedSteamID = 0, IReadOnlyCollection<ulong> acceptedTradeIDs = null) {
			if (!Bot.HasMobileAuthenticator) {
				return false;
			}

			HashSet<MobileAuthenticator.Confirmation> confirmations = await Bot.BotDatabase.MobileAuthenticator.GetConfirmations(acceptedType).ConfigureAwait(false);
			if ((confirmations == null) || (confirmations.Count == 0)) {
				return true;
			}

			if ((acceptedSteamID == 0) && ((acceptedTradeIDs == null) || (acceptedTradeIDs.Count == 0))) {
				return await Bot.BotDatabase.MobileAuthenticator.HandleConfirmations(confirmations, accept).ConfigureAwait(false);
			}

			IEnumerable<Task<Steam.ConfirmationDetails>> tasks = confirmations.Select(Bot.BotDatabase.MobileAuthenticator.GetConfirmationDetails);
			ICollection<Steam.ConfirmationDetails> results;

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<Steam.ConfirmationDetails>(confirmations.Count);
					foreach (Task<Steam.ConfirmationDetails> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			foreach (MobileAuthenticator.Confirmation confirmation in results.Where(details => (details != null) && ((acceptedType != details.Type) || ((acceptedSteamID != 0) && (details.OtherSteamID64 != 0) && (acceptedSteamID != details.OtherSteamID64)) || ((acceptedTradeIDs != null) && (details.TradeOfferID != 0) && !acceptedTradeIDs.Contains(details.TradeOfferID)))).Select(details => details.Confirmation)) {
				confirmations.Remove(confirmation);
				if (confirmations.Count == 0) {
					return true;
				}
			}

			return await Bot.BotDatabase.MobileAuthenticator.HandleConfirmations(confirmations, accept).ConfigureAwait(false);
		}

		internal async Task AcceptDigitalGiftCards() {
			lock (GiftCardsSemaphore) {
				if (ProcessingGiftsScheduled) {
					return;
				}

				ProcessingGiftsScheduled = true;
			}

			await GiftCardsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				lock (GiftCardsSemaphore) {
					ProcessingGiftsScheduled = false;
				}

				HashSet<ulong> giftCardIDs = await Bot.ArchiWebHandler.GetDigitalGiftCards().ConfigureAwait(false);
				if ((giftCardIDs == null) || (giftCardIDs.Count == 0)) {
					return;
				}

				foreach (ulong giftCardID in giftCardIDs.Where(gid => !HandledGifts.Contains(gid))) {
					HandledGifts.Add(giftCardID);

					Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.BotAcceptingGift, giftCardID));
					await LimitGiftsRequestsAsync().ConfigureAwait(false);

					bool result = await Bot.ArchiWebHandler.AcceptDigitalGiftCard(giftCardID).ConfigureAwait(false);
					if (result) {
						Bot.ArchiLogger.LogGenericInfo(Strings.Success);
					} else {
						Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
					}
				}
			} finally {
				GiftCardsSemaphore.Release();
			}
		}

		internal async Task AcceptGuestPasses(IReadOnlyCollection<ulong> guestPassIDs) {
			if ((guestPassIDs == null) || (guestPassIDs.Count == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(guestPassIDs));
				return;
			}

			foreach (ulong guestPassID in guestPassIDs.Where(guestPassID => !HandledGifts.Contains(guestPassID))) {
				HandledGifts.Add(guestPassID);

				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.BotAcceptingGift, guestPassID));
				await LimitGiftsRequestsAsync().ConfigureAwait(false);

				ArchiHandler.RedeemGuestPassResponseCallback response = await Bot.ArchiHandler.RedeemGuestPass(guestPassID).ConfigureAwait(false);
				if (response != null) {
					if (response.Result == EResult.OK) {
						Bot.ArchiLogger.LogGenericInfo(Strings.Success);
					} else {
						Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, response.Result));
					}
				} else {
					Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
				}
			}
		}

		internal static (bool Success, string Output) Exit() {
			// Schedule the task after some time so user can receive response
			Utilities.InBackground(
				async () => {
					await Task.Delay(1000).ConfigureAwait(false);
					await Program.Exit().ConfigureAwait(false);
				}
			);

			return (true, Strings.Done);
		}

		internal async Task<(bool Success, string Output)> Loot(uint appID = Steam.Asset.SteamAppID, byte contextID = Steam.Asset.SteamCommunityContextID, ulong targetSteamID = 0, IReadOnlyCollection<Steam.Asset.EType> wantedTypes = null, IReadOnlyCollection<uint> wantedRealAppIDs = null) {
			if ((appID == 0) || (contextID == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(appID) + " || " + nameof(contextID));
				return (false, null);
			}

			if (!Bot.IsConnectedAndLoggedOn) {
				return (false, Strings.BotNotConnected);
			}

			if (!LootingAllowed) {
				return (false, Strings.BotLootingTemporarilyDisabled);
			}

			if (Bot.BotConfig.LootableTypes.Count == 0) {
				return (false, Strings.BotLootingNoLootableTypes);
			}

			if (targetSteamID == 0) {
				targetSteamID = GetFirstSteamMasterID();

				if (targetSteamID == 0) {
					return (false, Strings.BotLootingMasterNotDefined);
				}
			}

			if (targetSteamID == Bot.CachedSteamID) {
				return (false, Strings.BotSendingTradeToYourself);
			}

			lock (LootingSemaphore) {
				if (LootingScheduled) {
					return (false, Strings.BotLootingTemporarilyDisabled);
				}

				LootingScheduled = true;
			}

			await LootingSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				lock (LootingSemaphore) {
					LootingScheduled = false;
				}

				HashSet<Steam.Asset> inventory = await Bot.ArchiWebHandler.GetInventory(Bot.CachedSteamID, appID, contextID, true, wantedTypes, wantedRealAppIDs).ConfigureAwait(false);
				if ((inventory == null) || (inventory.Count == 0)) {
					return (false, string.Format(Strings.ErrorIsEmpty, nameof(inventory)));
				}

				if (!await Bot.ArchiWebHandler.MarkSentTrades().ConfigureAwait(false) || !await Bot.ArchiWebHandler.SendTradeOffer(targetSteamID, inventory, Bot.BotConfig.SteamTradeToken).ConfigureAwait(false)) {
					return (false, Strings.BotLootingFailed);
				}

				if (Bot.HasMobileAuthenticator) {
					// Give Steam network some time to generate confirmations
					await Task.Delay(3000).ConfigureAwait(false);
					if (!await AcceptConfirmations(true, Steam.ConfirmationDetails.EType.Trade, targetSteamID).ConfigureAwait(false)) {
						return (false, Strings.BotLootingFailed);
					}
				}
			} finally {
				LootingSemaphore.Release();
			}

			return (true, Strings.BotLootingSuccess);
		}

		internal void OnDisconnected() => HandledGifts.Clear();

		internal async Task<ArchiHandler.PurchaseResponseCallback> RedeemKey(string key) {
			await LimitGiftsRequestsAsync().ConfigureAwait(false);

			return await Bot.ArchiHandler.RedeemKey(key).ConfigureAwait(false);
		}

		internal static (bool Success, string Output) Restart() {
			// Schedule the task after some time so user can receive response
			Utilities.InBackground(
				async () => {
					await Task.Delay(1000).ConfigureAwait(false);
					await Program.Restart().ConfigureAwait(false);
				}
			);

			return (true, Strings.Done);
		}

		internal bool SwitchLootingAllowed() => LootingAllowed = !LootingAllowed;

		internal static async Task<(bool Success, Version Version)> Update() {
			Version version = await ASF.Update(true).ConfigureAwait(false);
			if ((version == null) || (version <= SharedInfo.Version)) {
				return (false, version);
			}

			Utilities.InBackground(ASF.RestartOrExit);
			return (true, version);
		}

		private ulong GetFirstSteamMasterID() => Bot.BotConfig.SteamUserPermissions.Where(kv => (kv.Key != 0) && (kv.Value == BotConfig.EPermission.Master)).Select(kv => kv.Key).OrderByDescending(steamID => steamID != Bot.CachedSteamID).ThenBy(steamID => steamID).FirstOrDefault();

		private static async Task LimitGiftsRequestsAsync() {
			if (Program.GlobalConfig.GiftsLimiterDelay == 0) {
				return;
			}

			await GiftsSemaphore.WaitAsync().ConfigureAwait(false);
			Utilities.InBackground(
				async () => {
					await Task.Delay(Program.GlobalConfig.GiftsLimiterDelay * 1000).ConfigureAwait(false);
					GiftsSemaphore.Release();
				}
			);
		}
	}
}
