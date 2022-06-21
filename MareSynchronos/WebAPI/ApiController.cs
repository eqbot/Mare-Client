﻿using Dalamud.Configuration;
using Dalamud.Logging;
using MareSynchronos.Models;
using System;
using System.Buffers.Text;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using LZ4;
using MareSynchronos.API;
using MareSynchronos.FileCacheDB;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.VisualBasic;

namespace MareSynchronos.WebAPI
{
    public class CharacterReceivedEventArgs : EventArgs
    {
        public CharacterReceivedEventArgs(string characterNameHash, CharacterCacheDto characterData)
        {
            CharacterData = characterData;
            CharacterNameHash = characterNameHash;
        }

        public CharacterCacheDto CharacterData { get; set; }
        public string CharacterNameHash { get; set; }
    }

    public class ApiController : IDisposable
    {
        public const string MainServer = "Lunae Crescere Incipientis (Central Server EU)";

        private readonly Configuration _pluginConfiguration;
        public const string MainServiceUri = "https://darkarchon.internet-box.ch:5001";
        public string UID { get; private set; } = string.Empty;
        public string SecretKey => _pluginConfiguration.ClientSecret.ContainsKey(ApiUri) ? _pluginConfiguration.ClientSecret[ApiUri] : "-";
        private string CacheFolder => _pluginConfiguration.CacheFolder;
        public ConcurrentDictionary<string, (long, long)> CurrentUploads { get; } = new();
        public ConcurrentDictionary<string, (long, long)> CurrentDownloads { get; } = new();
        public bool IsDownloading { get; private set; } = false;
        public bool IsUploading { get; private set; } = false;
        public bool UseCustomService
        {
            get => _pluginConfiguration.UseCustomService;
            set
            {
                _pluginConfiguration.UseCustomService = value;
                _pluginConfiguration.Save();
            }
        }
        private string ApiUri => UseCustomService ? _pluginConfiguration.ApiUri : MainServiceUri;

        public bool ServerAlive =>
            (_heartbeatHub?.State ?? HubConnectionState.Disconnected) == HubConnectionState.Connected;
        public bool IsConnected => !string.IsNullOrEmpty(UID);

        public event EventHandler? Connected;
        public event EventHandler? Disconnected;
        public event EventHandler<CharacterReceivedEventArgs>? CharacterReceived;
        public event EventHandler? UnpairedFromOther;
        public event EventHandler? PairedWithOther;
        public event EventHandler? PairedClientOnline;
        public event EventHandler? PairedClientOffline;

        public List<ClientPairDto> PairedClients { get; set; } = new();

        readonly CancellationTokenSource cts;
        private HubConnection? _heartbeatHub;
        private HubConnection? _fileHub;
        private HubConnection? _userHub;
        private CancellationTokenSource? uploadCancellationTokenSource;

        public ApiController(Configuration pluginConfiguration)
        {
            this._pluginConfiguration = pluginConfiguration;
            cts = new CancellationTokenSource();

            _ = Heartbeat();
        }

        public async Task Heartbeat()
        {
            while (!ServerAlive && !cts.Token.IsCancellationRequested)
            {
                try
                {
                    PluginLog.Debug("Attempting to establish heartbeat connection to " + ApiUri);
                    _heartbeatHub = new HubConnectionBuilder()
                        .WithUrl(ApiUri + "/heartbeat", options =>
                        {
                            if (!string.IsNullOrEmpty(SecretKey))
                            {
                                options.Headers.Add("Authorization", SecretKey);
                            }

#if DEBUG
                            options.HttpMessageHandlerFactory = (message) =>
                            {
                                if (message is HttpClientHandler clientHandler)
                                    clientHandler.ServerCertificateCustomValidationCallback +=
                                        (sender, certificate, chain, sslPolicyErrors) => true;
                                return message;
                            };
#endif
                        }).Build();
                    PluginLog.Debug("Heartbeat service built to: " + ApiUri);

                    await _heartbeatHub.StartAsync(cts.Token);
                    UID = await _heartbeatHub!.InvokeAsync<string>("Heartbeat");
                    PluginLog.Debug("Heartbeat started: " + ApiUri);
                    try
                    {
                        await InitializeHubConnections();
                        await LoadInitialData();
                        Connected?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error(ex, "Error during Heartbeat initialization");
                    }

                    _heartbeatHub.Closed += OnHeartbeatHubOnClosed;
                    _heartbeatHub.Reconnected += OnHeartbeatHubOnReconnected;
                    PluginLog.Debug("Heartbeat established to: " + ApiUri);
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "Creating heartbeat failure");
                }
            }
        }

        private async Task LoadInitialData()
        {
            var pairedClients = await _userHub!.InvokeAsync<List<ClientPairDto>>("GetPairedClients");
            PairedClients = pairedClients.ToList();
        }

        public void RestartHeartbeat()
        {
            PluginLog.Debug("Restarting heartbeat");

            _heartbeatHub!.Closed -= OnHeartbeatHubOnClosed;
            _heartbeatHub!.Reconnected -= OnHeartbeatHubOnReconnected;
            Task.Run(async () =>
            {
                await _heartbeatHub.StopAsync(cts.Token);
                await _heartbeatHub.DisposeAsync();
                _heartbeatHub = null!;
                _ = Heartbeat();
            });
        }

        private async Task OnHeartbeatHubOnReconnected(string? s)
        {
            PluginLog.Debug("Reconnected: " + ApiUri);
            UID = await _heartbeatHub!.InvokeAsync<string>("Heartbeat");
        }

        private Task OnHeartbeatHubOnClosed(Exception? exception)
        {
            PluginLog.Debug("Connection closed: " + ApiUri);
            Disconnected?.Invoke(null, EventArgs.Empty);
            RestartHeartbeat();
            return Task.CompletedTask;
        }

        private async Task DisposeHubConnections()
        {
            if (_fileHub != null)
            {
                PluginLog.Debug("Disposing File Hub");
                CancelUpload();
                await _fileHub!.StopAsync();
                await _fileHub!.DisposeAsync();
            }

            if (_userHub != null)
            {
                PluginLog.Debug("Disposing User Hub");
                await _userHub.StopAsync();
                await _userHub.DisposeAsync();
            }
        }

        private async Task InitializeHubConnections()
        {
            await DisposeHubConnections();

            PluginLog.Debug("Creating User Hub");
            _userHub = new HubConnectionBuilder()
                .WithUrl(ApiUri + "/user", options =>
                {
                    options.Headers.Add("Authorization", SecretKey);
#if DEBUG
                    options.HttpMessageHandlerFactory = (message) =>
                    {
                        if (message is HttpClientHandler clientHandler)
                            clientHandler.ServerCertificateCustomValidationCallback +=
                                (sender, certificate, chain, sslPolicyErrors) => true;
                        return message;
                    };
#endif
                })
                .Build();
            await _userHub.StartAsync();
            _userHub.On<ClientPairDto, string>("UpdateClientPairs", UpdateLocalClientPairs);
            _userHub.On<CharacterCacheDto, string>("ReceiveCharacterData", ReceiveCharacterData);
            _userHub.On<string>("RemoveOnlinePairedPlayer", (s) => PairedClientOffline?.Invoke(s, EventArgs.Empty));
            _userHub.On<string>("AddOnlinePairedPlayer", (s) => PairedClientOnline?.Invoke(s, EventArgs.Empty));

            PluginLog.Debug("Creating File Hub");
            _fileHub = new HubConnectionBuilder()
                .WithUrl(ApiUri + "/files", options =>
                {
                    options.Headers.Add("Authorization", SecretKey);
#if DEBUG
                    options.HttpMessageHandlerFactory = (message) =>
                    {
                        if (message is HttpClientHandler clientHandler)
                            clientHandler.ServerCertificateCustomValidationCallback +=
                                (sender, certificate, chain, sslPolicyErrors) => true;
                        return message;
                    };
#endif
                })
                .Build();
            await _fileHub.StartAsync(cts.Token);
        }

        private void UpdateLocalClientPairs(ClientPairDto dto, string characterIdentifier)
        {
            var entry = PairedClients.SingleOrDefault(e => e.OtherUID == dto.OtherUID);
            if (entry == null)
            {
                UnpairedFromOther?.Invoke(characterIdentifier, EventArgs.Empty);
                return;
            }

            if ((entry.IsPausedFromOthers != dto.IsPausedFromOthers || entry.IsSynced != dto.IsSynced || entry.IsPaused != dto.IsPaused)
                && !dto.IsPaused && dto.IsSynced && !dto.IsPausedFromOthers)
            {
                PairedWithOther?.Invoke(characterIdentifier, EventArgs.Empty);
            }

            entry.IsPaused = dto.IsPaused;
            entry.IsPausedFromOthers = dto.IsPausedFromOthers;
            entry.IsSynced = dto.IsSynced;

            if (dto.IsPaused || dto.IsPausedFromOthers || !dto.IsSynced)
            {
                UnpairedFromOther?.Invoke(characterIdentifier, EventArgs.Empty);
            }
        }

        private async Task<(string, byte[])> GetCompressedFileData(string fileHash, CancellationToken uploadToken)
        {
            await using var db = new FileCacheContext();
            var fileCache = db.FileCaches.First(f => f.Hash == fileHash);
            return (fileHash, LZ4Codec.WrapHC(await File.ReadAllBytesAsync(fileCache.Filepath, uploadToken), 0,
                (int)new FileInfo(fileCache.Filepath).Length));
        }

        private async Task UploadFile(byte[] compressedFile, string fileHash, CancellationToken uploadToken)
        {
            if (uploadToken.IsCancellationRequested) return;
            var chunkSize = 1024 * 512; // 512kb
            var chunks = (int)Math.Ceiling(compressedFile.Length / (double)chunkSize);
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(chunkSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = true,
                SingleReader = true,
                SingleWriter = true
            });
            await _fileHub!.SendAsync("UploadFile", fileHash, channel.Reader, uploadToken);
            for (var i = 0; i < chunks; i++)
            {
                var uploadChunk = compressedFile.Skip(i * chunkSize).Take(chunkSize).ToArray();
                channel.Writer.TryWrite(uploadChunk);
                CurrentUploads[fileHash] = (CurrentUploads[fileHash].Item1 + uploadChunk.Length, CurrentUploads[fileHash].Item2);
                if (uploadToken.IsCancellationRequested) break;
            }

            channel.Writer.Complete();
        }

        public async Task Register()
        {
            if (!ServerAlive) return;
            PluginLog.Debug("Registering at service " + ApiUri);
            var response = await _userHub!.InvokeAsync<string>("Register");
            _pluginConfiguration.ClientSecret[ApiUri] = response;
            _pluginConfiguration.Save();
            RestartHeartbeat();
        }

        public void CancelUpload()
        {
            if (uploadCancellationTokenSource != null)
            {
                PluginLog.Warning("Cancelling upload");
                uploadCancellationTokenSource?.Cancel();
                _fileHub!.InvokeAsync("AbortUpload");
            }
        }

        public async Task SendCharacterData(CharacterCacheDto character, List<string> visibleCharacterIds)
        {
            if (!IsConnected || SecretKey == "-") return;
            PluginLog.Debug("Sending Character data to service " + ApiUri);

            CancelUpload();
            uploadCancellationTokenSource = new CancellationTokenSource();
            var uploadToken = uploadCancellationTokenSource.Token;
            PluginLog.Debug("New Token Created");

            var filesToUpload = await _fileHub!.InvokeAsync<List<string>>("SendFiles", character.FileReplacements, uploadToken);

            IsUploading = true;

            PluginLog.Debug("Compressing files");
            Dictionary<string, byte[]> compressedFileData = new();
            foreach (var file in filesToUpload)
            {
                var data = await GetCompressedFileData(file, uploadToken);
                compressedFileData.Add(data.Item1, data.Item2);
                CurrentUploads[data.Item1] = (0, data.Item2.Length);
            }
            PluginLog.Debug("Files compressed, uploading files");
            foreach (var data in compressedFileData)
            {
                await UploadFile(data.Value, data.Key, uploadToken);
                if (uploadToken.IsCancellationRequested)
                {
                    PluginLog.Warning("Cancel in filesToUpload loop detected");
                    CurrentUploads.Clear();
                    break;
                }
            }
            PluginLog.Debug("Upload tasks complete, waiting for server to confirm");
            var anyUploadsOpen = await _fileHub!.InvokeAsync<bool>("IsUploadFinished", uploadToken);
            PluginLog.Debug("Uploads open: " + anyUploadsOpen);
            while (anyUploadsOpen && !uploadToken.IsCancellationRequested)
            {
                anyUploadsOpen = await _fileHub!.InvokeAsync<bool>("IsUploadFinished", uploadToken);
                await Task.Delay(TimeSpan.FromSeconds(0.5), uploadToken);
                PluginLog.Debug("Waiting for uploads to finish");
            }

            CurrentUploads.Clear();
            IsUploading = false;

            if (!uploadToken.IsCancellationRequested)
            {
                PluginLog.Debug("=== Pushing character data ===");
                await _userHub!.InvokeAsync("PushCharacterData", character, visibleCharacterIds, uploadToken);
            }
            else
            {
                PluginLog.Warning("=== Upload operation was cancelled ===");
            }

            PluginLog.Debug("== Upload complete for " + character.JobId);
            uploadCancellationTokenSource = null;
        }

        public Task ReceiveCharacterData(CharacterCacheDto character, string characterHash)
        {
            PluginLog.Debug("Received DTO for " + characterHash);
            CharacterReceived?.Invoke(null, new CharacterReceivedEventArgs(characterHash, character));
            return Task.CompletedTask;
        }

        public async Task UpdateCurrentDownloadSize(string hash)
        {
            long fileSize = await _fileHub!.InvokeAsync<long>("GetFileSize", hash);
        }

        public async Task DownloadFiles(List<FileReplacementDto> fileReplacementDto, string cacheFolder)
        {
            foreach (var file in fileReplacementDto)
            {
                var fileSize = await _fileHub!.InvokeAsync<long>("GetFileSize", file.Hash);
                CurrentDownloads[file.Hash] = (0, fileSize);
            }

            foreach (var file in fileReplacementDto.Where(f => CurrentDownloads[f.Hash].Item2 > 0))
            {
                var hash = file.Hash;
                var data = await DownloadFile(hash);
                var extractedFile = LZ4.LZ4Codec.Unwrap(data);
                var ext = file.GamePaths.First().Split(".", StringSplitOptions.None).Last();
                var filePath = Path.Combine(cacheFolder, file.Hash + "." + ext);
                await File.WriteAllBytesAsync(filePath, extractedFile);
                await using (var db = new FileCacheContext())
                {
                    db.Add(new FileCache
                    {
                        Filepath = filePath.ToLower(),
                        Hash = file.Hash,
                        LastModifiedDate = DateTime.Now.Ticks.ToString(),
                    });
                    await db.SaveChangesAsync();
                }
                PluginLog.Debug("File downloaded to " + filePath);
            }

            CurrentDownloads.Clear();
        }

        public async Task<byte[]> DownloadFile(string hash)
        {
            IsDownloading = true;
            var reader = await _fileHub!.StreamAsChannelAsync<byte[]>("DownloadFile", hash);
            List<byte> downloadedData = new();
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var data))
                {
                    CurrentDownloads[hash] = (CurrentDownloads[hash].Item1 + data.Length, CurrentDownloads[hash].Item2);
                    downloadedData.AddRange(data);
                    //await Task.Delay(25);
                }
            }

            IsDownloading = false;
            return downloadedData.ToArray();
        }

        public async Task GetCharacterData(Dictionary<string, int> hashedCharacterNames)
        {
            await _userHub!.InvokeAsync("GetCharacterData",
                hashedCharacterNames);
        }

        public async Task SendPairedClientPauseChange(string uid, bool paused)
        {
            if (!IsConnected || SecretKey == "-") return;
            await _userHub!.SendAsync("SendPairedClientPauseChange", uid, paused);
        }

        public async Task SendPairedClientAddition(string uid)
        {
            if (!IsConnected || SecretKey == "-") return;
            await _userHub!.SendAsync("SendPairedClientAddition", uid);
        }

        public async Task SendPairedClientRemoval(string uid)
        {
            if (!IsConnected || SecretKey == "-") return;
            await _userHub!.SendAsync("SendPairedClientRemoval", uid);
        }

        public void Dispose()
        {
            cts?.Cancel();
            _ = DisposeHubConnections();
        }

        public async Task<List<string>> SendCharacterName(string hashedName)
        {
            return await _userHub!.InvokeAsync<List<string>>("SendCharacterNameHash", hashedName);
        }
    }
}
