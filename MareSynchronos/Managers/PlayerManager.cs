﻿using Dalamud.Logging;
using MareSynchronos.Factories;
using MareSynchronos.Models;
using MareSynchronos.Utils;
using MareSynchronos.WebAPI;
using Newtonsoft.Json;
using Penumbra.PlayerWatch;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronos.Managers
{
    public class PlayerManager : IDisposable
    {
        private readonly ApiController _apiController;
        private readonly CachedPlayersManager _cachedPlayersManager;
        private readonly CharacterDataFactory _characterDataFactory;
        private readonly DalamudUtil _dalamudUtil;
        private readonly IpcManager _ipcManager;
        private readonly IPlayerWatcher _watcher;
        private string _lastSentHash = string.Empty;
        private Task? _playerChangedTask;

        public PlayerManager(ApiController apiController, IpcManager ipcManager,
            CharacterDataFactory characterDataFactory, CachedPlayersManager cachedPlayersManager, DalamudUtil dalamudUtil, IPlayerWatcher watcher)
        {
            Logger.Debug("Creating " + nameof(PlayerManager));

            _apiController = apiController;
            _ipcManager = ipcManager;
            _characterDataFactory = characterDataFactory;
            _cachedPlayersManager = cachedPlayersManager;
            _dalamudUtil = dalamudUtil;
            _watcher = watcher;

            _watcher.AddPlayerToWatch(_dalamudUtil.PlayerName);
            _apiController.Connected += ApiController_Connected;
            _apiController.Disconnected += ApiController_Disconnected;

            Logger.Debug("Watching Player, ApiController is Connected: " + _apiController.IsConnected);
            if (_apiController.IsConnected)
            {
                ApiController_Connected(null, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            Logger.Debug("Disposing " + nameof(PlayerManager));

            _ipcManager.PenumbraRedrawEvent -= IpcManager_PenumbraRedrawEvent;
            _apiController.Connected -= ApiController_Connected;
            _apiController.Disconnected -= ApiController_Disconnected;
            _watcher.PlayerChanged -= Watcher_PlayerChanged;
        }

        private void ApiController_Connected(object? sender, EventArgs args)
        {
            Logger.Debug("ApiController Connected");
            var apiTask = _apiController.SendCharacterName(_dalamudUtil.PlayerNameHashed);
            _lastSentHash = string.Empty;

            Task.WaitAll(apiTask);

            _cachedPlayersManager.AddInitialPairs(apiTask.Result);

            _ipcManager.PenumbraRedrawEvent += IpcManager_PenumbraRedrawEvent;
            _ipcManager.PenumbraRedraw(_dalamudUtil.PlayerName);
            _watcher.PlayerChanged += Watcher_PlayerChanged;
        }

        private void ApiController_Disconnected(object? sender, EventArgs args)
        {
            Logger.Debug(nameof(ApiController_Disconnected));

            _ipcManager.PenumbraRedrawEvent -= IpcManager_PenumbraRedrawEvent;
            _watcher.PlayerChanged -= Watcher_PlayerChanged;
        }

        private async Task<CharacterData> CreateFullCharacterCache()
        {
            var cache = _characterDataFactory.BuildCharacterData();

            await Task.Run(async () =>
            {
                while (!cache.IsReady)
                {
                    await Task.Delay(50);
                }

                var json = JsonConvert.SerializeObject(cache, Formatting.Indented);

                cache.CacheHash = Crypto.GetHash(json);
            });

            return cache;
        }

        private void IpcManager_PenumbraRedrawEvent(object? objectTableIndex, EventArgs e)
        {
            var player = _dalamudUtil.GetPlayerCharacterFromObjectTableIndex((int)objectTableIndex!);
            if (player != null && player.Name.ToString() != _dalamudUtil.PlayerName) return;
            Logger.Debug("Penumbra Redraw Event for " + _dalamudUtil.PlayerName);
            PlayerChanged(_dalamudUtil.PlayerName);
        }

        private void PlayerChanged(string name)
        {
            //if (sender == null) return;
            Logger.Debug("Player changed: " + name);
            if (_playerChangedTask is { IsCompleted: false })
            {
                PluginLog.Warning("PlayerChanged Task still running");
                return;
            }

            if (!_ipcManager.Initialized)
            {
                PluginLog.Warning("Penumbra not active, doing nothing.");
                return;
            }

            _playerChangedTask = Task.Run(async () =>
            {
                int attempts = 0;
                while (!_apiController.IsConnected && attempts < 10)
                {
                    Logger.Warn("No connection to the API");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    attempts++;
                }

                if (attempts == 10) return;

                Stopwatch st = Stopwatch.StartNew();
                _dalamudUtil.WaitWhileSelfIsDrawing();

                var characterCacheTask = await CreateFullCharacterCache();

                var cacheDto = characterCacheTask.ToCharacterCacheDto();

                st.Stop();
                Logger.Debug("Elapsed time PlayerChangedTask: " + st.Elapsed);
                if (cacheDto.Hash == _lastSentHash)
                {
                    Logger.Debug("Not sending data, already sent");
                    return;
                }
                _ = _apiController.SendCharacterData(cacheDto, _dalamudUtil.GetLocalPlayers().Select(d => d.Key).ToList());
                _lastSentHash = cacheDto.Hash;
            });
        }

        private void Watcher_PlayerChanged(Dalamud.Game.ClientState.Objects.Types.Character actor)
        {
            Task.Run(() =>
            {
                // fix for redraw from anamnesis
                while (!_dalamudUtil.IsPlayerPresent)
                {
                    Logger.Debug("Waiting Until Player is Present");
                    Thread.Sleep(100);
                }

                if (actor.Name.ToString() == _dalamudUtil.PlayerName)
                {
                    Logger.Debug("Watcher: PlayerChanged");
                    PlayerChanged(actor.Name.ToString());
                }
            });
        }


    }
}
