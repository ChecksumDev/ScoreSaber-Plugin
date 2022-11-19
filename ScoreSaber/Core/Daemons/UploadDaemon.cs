﻿#if RELEASE
using Newtonsoft.Json;
using ScoreSaber.Core.AC;
using ScoreSaber.Core.Data;
using ScoreSaber.Core.Data.Internal;
using ScoreSaber.Core.Data.Models;
using ScoreSaber.Core.Services;
using ScoreSaber.Extensions;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using ScoreSaber.Core.Utils;
using static ScoreSaber.UI.Leaderboard.ScoreSaberLeaderboardViewController;

namespace ScoreSaber.Core.Daemons {

    // TODO: Actually make pretty now that we're open source
    internal class UploadDaemon : IDisposable, IUploadDaemon {

        public event Action<UploadStatus, string> UploadStatusChanged;

        public bool uploading { get; set; }

        private readonly PlayerService _playerService = null;
        private readonly ReplayService _replayService = null;
        private readonly LeaderboardService _leaderboardService = null;

        private readonly PlayerDataModel _playerDataModel = null;
        private readonly CustomLevelLoader _customLevelLoader = null;

        public UploadDaemon(PlayerService playerService, LeaderboardService leaderboardService, ReplayService replayService, PlayerDataModel playerDataModel, CustomLevelLoader customLevelLoader) {
            _playerService = playerService;
            _replayService = replayService;
            _leaderboardService = leaderboardService;
            _playerDataModel = playerDataModel;
            _customLevelLoader = customLevelLoader;

            SetupUploader();
            Plugin.Log.Debug("Upload service setup!");
        }


        private void SetupUploader() {

            StandardLevelScenesTransitionSetupDataSO transitionSetup =
 Resources.FindObjectsOfTypeAll<StandardLevelScenesTransitionSetupDataSO>().FirstOrDefault();
            MultiplayerLevelScenesTransitionSetupDataSO multiTransitionSetup =
 Resources.FindObjectsOfTypeAll<MultiplayerLevelScenesTransitionSetupDataSO>().FirstOrDefault();
            if (!Plugin.ScoreSubmission) {
                return;
            }

            transitionSetup.didFinishEvent -= UploadDaemonHelper.ThreeInstance;
            transitionSetup.didFinishEvent -= StandardSceneTransition;
            UploadDaemonHelper.ThreeInstance = StandardSceneTransition;
            transitionSetup.didFinishEvent += StandardSceneTransition;

            multiTransitionSetup.didFinishEvent -= UploadDaemonHelper.FourInstance;
            multiTransitionSetup.didFinishEvent -= MultiplayerSceneTransition;
            UploadDaemonHelper.FourInstance = MultiplayerSceneTransition;
            multiTransitionSetup.didFinishEvent += MultiplayerSceneTransition;
        }

        public void StandardSceneTransition(StandardLevelScenesTransitionSetupDataSO scene, LevelCompletionResults lcr) {
            //StandardLevelScenesTransitionSetupDataSO 
            //LevelCompletionResults
            
            ReplayHandler(scene.gameMode, scene.difficultyBeatmap, lcr, scene.practiceSettings != null);
        }

        public void MultiplayerSceneTransition(MultiplayerLevelScenesTransitionSetupDataSO scene, MultiplayerResultsData mrd) {
            // MultiplayerLevelScenesTransitionSetupDataSO
            // multiplayerResultsData
            
            if (scene.difficultyBeatmap == null) {
                return;
            }
            if (mrd.localPlayerResultData.multiplayerLevelCompletionResults.levelCompletionResults == null) {
                return;
            }
            if (mrd.localPlayerResultData.multiplayerLevelCompletionResults.playerLevelEndReason == MultiplayerLevelCompletionResults.MultiplayerPlayerLevelEndReason.HostEndedLevel) {
                return;
            }
            if (mrd.localPlayerResultData.multiplayerLevelCompletionResults.levelCompletionResults.levelEndStateType != LevelCompletionResults.LevelEndStateType.Cleared) {
                return;
            }

            ReplayHandler(scene.gameMode, scene.difficultyBeatmap, mrd.localPlayerResultData.multiplayerLevelCompletionResults.levelCompletionResults, false);
        }

        public void ReplayHandler(string gm, IDifficultyBeatmap db, LevelCompletionResults lcr, bool practiceMode) {
            try {
                if (Plugin.ReplayState.IsPlaybackEnabled) {
                    return;
                }

                PracticeViewController practiceViewController =
                    Resources.FindObjectsOfTypeAll<PracticeViewController>().FirstOrDefault();
                if (!practiceViewController.isInViewControllerHierarchy) {
                    if (gm != "Solo" && gm != "Multiplayer") {
                        return;
                    }

                    Plugin.Log.Debug($"Starting upload process for {db.level.levelID}:{db.level.songName}");

                    if (practiceMode) {
                        // If practice write replay at this point
                        _replayService.WriteSerializedReplay().RunTask();
                        return;
                    }
                    
                    if (lcr.levelEndAction != LevelCompletionResults.LevelEndAction.None) {
                        _replayService.WriteSerializedReplay().RunTask();
                        return;
                    }
                    
                    if (lcr.levelEndStateType != LevelCompletionResults.LevelEndStateType.Cleared) {
                        _replayService.WriteSerializedReplay().RunTask();
                        return;
                    }
                    
                    PreUpload(db, lcr);
                } else {
                    // If practice write replay at this point
                    _replayService.WriteSerializedReplay().RunTask();
                }
            } catch (Exception ex) {
                UploadStatusChanged?.Invoke(UploadStatus.Error, "Failed to upload score, error written to log.");
                Plugin.Log.Error($"Failed to upload score: {ex}");
            }
        }

        //This starts the upload processs
        async void PreUpload(IDifficultyBeatmap db, LevelCompletionResults lcr) {
            if (!(db.level is CustomBeatmapLevel)) {
                return;
            }

            EnvironmentInfoSO defaultEnvironment = _customLevelLoader.LoadEnvironmentInfo(null, false);

            IReadonlyBeatmapData beatmapData =
                await db.GetBeatmapDataAsync(defaultEnvironment, _playerDataModel.playerData.playerSpecificSettings);

            if (LeaderboardUtils.ContainsV3Stuff(beatmapData)) {
                UploadStatusChanged?.Invoke(UploadStatus.Error, "New note type not supported, not uploading");
                return;
            }

            double maxScore = LeaderboardUtils.OldMaxRawScoreForNumberOfNotes(beatmapData.cuttableNotesCount);
            maxScore *= 1.12;

            if (lcr.modifiedScore > maxScore) {
                return;
            }

            try {
                UploadStatusChanged?.Invoke(UploadStatus.Packaging, "Packaging score...");
                ScoreSaberUploadData data =
                    ScoreSaberUploadData.Create(db, lcr, _playerService.localPlayerInfo, AntiCheat.GetHash());
                string scoreData = JsonConvert.SerializeObject(data);

                // TODO: Simplify now that we're open source
                
                byte[] encodedPassword =
                    new UTF8Encoding().GetBytes($"f0b4a81c9bd3ded1081b365f7628781f-{_playerService.localPlayerInfo.playerKey}-{_playerService.localPlayerInfo.playerId}-f0b4a81c9bd3ded1081b365f7628781f");
                
                byte[] keyHash = ((HashAlgorithm)CryptoConfig.CreateFromName("MD5")).ComputeHash(encodedPassword);
                
                string key = BitConverter.ToString(keyHash)
                    .Replace("-", string.Empty)
                    .ToLower();
                
                string scoreDataHex =
                    BitConverter.ToString(Swap(Encoding.UTF8.GetBytes(scoreData), Encoding.UTF8.GetBytes(key))).Replace("-", "");
                UploadScore(data, scoreDataHex, db, lcr).RunTask();
            } catch (Exception ex) {
                UploadStatusChanged?.Invoke(UploadStatus.Error, "Failed to upload score, error written to log.");
                Plugin.Log.Error($"Failed to upload score: {ex}");
            }
        }

        public async Task UploadScore(object rawData, object data, object level, object lcr) {
            //LeaderboardUploadDataA
            //string
            //IDifficultyBeatmap
            IDifficultyBeatmap levelCasted = (IDifficultyBeatmap)level;
            LevelCompletionResults results = (LevelCompletionResults)lcr;
            try {
                UploadStatusChanged?.Invoke(UploadStatus.Packaging, "Checking leaderboard ranked status...");

                Leaderboard currentLeaderboard = await _leaderboardService.GetCurrentLeaderboard(levelCasted);

                if (currentLeaderboard != null) {
                    if (currentLeaderboard.leaderboardInfo.playerScore != null) {
                        if (results.modifiedScore < currentLeaderboard.leaderboardInfo.playerScore.modifiedScore) {
                            UploadStatusChanged?.Invoke(UploadStatus.Error, "Didn't beat score, not uploading.");
                            UploadStatusChanged?.Invoke(UploadStatus.Done, "");
                            uploading = false;
                            return;
                        }
                    }
                } else {
                    Plugin.Log.Debug("Failed to get leaderboards ranked status");
                }

                bool done = false;
                bool failed = false;
                int attempts = 1;
                UploadStatusChanged?.Invoke(UploadStatus.Packaging, "Packaging replay...");
                byte[] serializedReplay = await _replayService.WriteSerializedReplay();

                // Create http packet
                WWWForm form = new WWWForm();
                form.AddField("data", (string)data);
                if (serializedReplay != null) {
                    Plugin.Log.Debug($"Replay size: {serializedReplay.Length}");
                    form.AddBinaryData("zr", serializedReplay);
                } else {
                    UploadStatusChanged?.Invoke(UploadStatus.Error, "Failed to upload (failed to serialize replay)");
                    done = true;
                    failed = true;
                }

                // Start upload process
                while (!done) {
                    uploading = true;
                    string response = null;
                    
                    Plugin.Log.Info("Attempting score upload...");
                    UploadStatusChanged?.Invoke(UploadStatus.Uploading, "Uploading score...");
                    
                    try {
                        response = await Plugin.HttpInstance.PostAsync("/game/upload", form);
                    } catch (HttpErrorException httpException) {
                        Plugin.Log.Error(httpException.isScoreSaberError
                            ? $"Failed to upload score: {httpException.scoreSaberError.errorMessage}:{httpException}"
                            : $"Failed to upload score: {httpException.isNetworkError}:{httpException.isHttpError}:{httpException}");
                    } catch (Exception ex) {
                        Plugin.Log.Error($"Failed to upload score: {ex.ToString()}");
                    }

                    if (!string.IsNullOrEmpty(response)) {
                        if (response.Contains("uploaded")) {
                            done = true;
                        } else {
                            if (response == "banned") {
                                UploadStatusChanged?.Invoke(UploadStatus.Error, "Failed to upload (banned)");
                                done = true;
                                failed = true;
                            }
                            Plugin.Log.Error($"Raw failed response: ${response}");
                        }
                    }

                    if (done) {
                        continue;
                    }

                    if (attempts < 4) {
                        UploadStatusChanged?.Invoke(UploadStatus.Retrying, $"Failed, attempting again ({attempts} of 3 tries...)");
                        attempts++;
                        await Task.Delay(1000);
                    } else {
                        done = true;
                        failed = true;
                    }
                }

                switch (failed)
                {
                    case false:
                        SaveLocalReplay(rawData, level, serializedReplay);
                        Plugin.Log.Info("Score uploaded!");
                        UploadStatusChanged?.Invoke(UploadStatus.Success, $"Score uploaded!");
                        break;
                    case true:
                        UploadStatusChanged?.Invoke(UploadStatus.Error, $"Failed to upload score.");
                        break;
                }

                uploading = false;
                UploadStatusChanged?.Invoke(UploadStatus.Done, "");
            } catch (Exception) {
                uploading = false;
                UploadStatusChanged?.Invoke(UploadStatus.Done, "");
            }
        }

        private void SaveLocalReplay(object rawData, object level, byte[] replay) {

            try {
                if (replay != null) {
                    ScoreSaberUploadData rawDataCasted = (ScoreSaberUploadData)rawData;
                    IDifficultyBeatmap levelCasted = (IDifficultyBeatmap)level;
                    if (!Plugin.Settings.saveLocalReplays) {
                        return;
                    }

                    string replayPath =
                        $@"{Settings.replayPath}\{rawDataCasted.playerId}-{rawDataCasted.songName.ReplaceInvalidChars().Truncate(155)}-{levelCasted.difficulty.SerializedName()}-{levelCasted.parentDifficultyBeatmapSet.beatmapCharacteristic.serializedName}-{rawDataCasted.leaderboardId}.dat";
                    File.WriteAllBytes(replayPath, replay);
                } else {
                    Plugin.Log.Error("Failed to write local replay; replay is null");
                }
            } catch (Exception) {
                // ignored
            }
        }

        public void Dispose() {
            Plugin.Log.Info("Upload service successfully deconstructed");
            StandardLevelScenesTransitionSetupDataSO transitionSetup =
 Resources.FindObjectsOfTypeAll<StandardLevelScenesTransitionSetupDataSO>().FirstOrDefault();
            if (transitionSetup != null) {
                transitionSetup.didFinishEvent -= StandardSceneTransition;
            }
        }

        private byte[] Swap(byte[] panda1, byte[] panda2) {

            int N1 = 11;
            int N2 = 13;
            int NS = 257;

            for (int I = 0; I <= panda2.Length - 1; I++) {
                NS += NS % (panda2[I] + 1);
            }

            byte[] T = new byte[panda1.Length];
            for (int I = 0; I <= panda1.Length - 1; I++) {
                NS = panda2[I % panda2.Length] + NS;
                N1 = (NS + 5) * (N1 & 255) + (N1 >> 8);
                N2 = (NS + 7) * (N2 & 255) + (N2 >> 8);
                NS = ((N1 << 8) + N2) & 255;

                T[I] = (byte)(panda1[I] ^ (byte)(NS));
            }

            return T;
        }
    }
}
#endif