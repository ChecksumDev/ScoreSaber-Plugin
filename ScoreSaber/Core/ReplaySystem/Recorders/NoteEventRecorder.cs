﻿using ScoreSaber.Extensions;
using System;
using System.Collections.Generic;
using Zenject;
using ScoreSaber.Core.ReplaySystem.Data;
using SiraUtil.Affinity;
using System.Reflection;

namespace ScoreSaber.Core.ReplaySystem.Recorders
{
    internal class NoteEventRecorder : TimeSynchronizer, IInitializable, IDisposable, IAffinity
    {
        private readonly List<NoteEvent> _noteKeyframes;
        private readonly ScoreController _scoreController;
        private readonly Dictionary<NoteData, NoteCutInfo> _collectedBadCutInfos = new Dictionary<NoteData, NoteCutInfo>();
        private readonly Dictionary<GoodCutScoringElement, float> _scoringStartInfo = new Dictionary<GoodCutScoringElement, float>();

        public NoteEventRecorder(ScoreController scoreController) {

            _scoreController = scoreController;
            _noteKeyframes = new List<NoteEvent>();
        }

        public void Initialize() {

            _scoreController.scoringForNoteStartedEvent += ScoreController_scoringForNoteStartedEvent;
            _scoreController.scoringForNoteFinishedEvent += ScoreController_scoringForNoteFinishedEvent;
        }

        private void ScoreController_scoringForNoteStartedEvent(ScoringElement element) {
            
            if (element is GoodCutScoringElement goodCut) {

                _scoringStartInfo.Add(goodCut, audioTimeSyncController.songTime);
            }
        }

        private void ScoreController_scoringForNoteFinishedEvent(ScoringElement element) {

            var noteData = element.noteData;
            NoteID noteID = new NoteID() { 
                Time = noteData.time,
                LineIndex = noteData.lineIndex,
                LineLayer = (int)noteData.noteLineLayer,
                ColorType = (int)noteData.colorType,
                CutDirection = (int)noteData.cutDirection,
                GameplayType = (int)noteData.gameplayType,
                ScoringType = (int)noteData.scoringType,
                CutDirectionAngleOffset = noteData.cutDirectionAngleOffset,
            };

            if (element is GoodCutScoringElement goodCut) {

                var cutTime = _scoringStartInfo[goodCut];
                var noteCutInfo = goodCut.cutScoreBuffer.noteCutInfo;
                _scoringStartInfo.Remove(goodCut);

                _noteKeyframes.Add(new NoteEvent() {
                    
                    NoteID = noteID,
                    EventType = NoteEventType.GoodCut,
                    CutPoint = noteCutInfo.cutPoint.Convert(),
                    CutNormal = noteCutInfo.cutNormal.Convert(),
                    SaberDirection = noteCutInfo.saberDir.Convert(),
                    SaberType = (int)noteCutInfo.saberType,
                    DirectionOK = noteCutInfo.directionOK,
                    CutDirectionDeviation = noteCutInfo.cutDirDeviation,
                    SaberSpeed = noteCutInfo.saberSpeed,
                    CutAngle = noteCutInfo.cutAngle,
                    CutDistanceToCenter = noteCutInfo.cutDistanceToCenter,
                    BeforeCutRating = goodCut.cutScoreBuffer.beforeCutSwingRating,
                    AfterCutRating = goodCut.cutScoreBuffer.afterCutSwingRating,
                    Time = cutTime,
                    UnityTimescale = UnityEngine.Time.timeScale,
                    TimeSyncTimescale = audioTimeSyncController.timeScale,

                    TimeDeviation = noteCutInfo.timeDeviation,
                    WorldRotation = noteCutInfo.worldRotation.Convert(),
                    InverseWorldRotation = noteCutInfo.inverseWorldRotation.Convert(),
                    NoteRotation = noteCutInfo.noteRotation.Convert(),
                    NotePosition = noteCutInfo.notePosition.Convert(),
                });


            } else if (element is BadCutScoringElement badCut) {

                var badCutEventType = noteData.colorType == ColorType.None ? NoteEventType.Bomb : NoteEventType.BadCut;
                var noteCutInfo = _collectedBadCutInfos[badCut.noteData];
                _collectedBadCutInfos.Remove(badCut.noteData);
                _noteKeyframes.Add(new NoteEvent() {
                    
                    NoteID = noteID,
                    EventType = badCutEventType,
                    CutPoint = noteCutInfo.cutPoint.Convert(),
                    CutNormal = noteCutInfo.cutNormal.Convert(),
                    SaberDirection = noteCutInfo.saberDir.Convert(),
                    SaberType = (int)noteCutInfo.saberType,
                    DirectionOK = noteCutInfo.directionOK,
                    CutDirectionDeviation = noteCutInfo.cutDirDeviation,
                    SaberSpeed = noteCutInfo.saberSpeed,
                    CutAngle = noteCutInfo.cutAngle,
                    CutDistanceToCenter = noteCutInfo.cutDistanceToCenter,
                    BeforeCutRating = 0f,
                    AfterCutRating = 0f,
                    Time = audioTimeSyncController.songTime,
                    UnityTimescale = UnityEngine.Time.timeScale,
                    TimeSyncTimescale = audioTimeSyncController.timeScale,

                    TimeDeviation = noteCutInfo.timeDeviation,
                    WorldRotation = noteCutInfo.worldRotation.Convert(),
                    InverseWorldRotation = noteCutInfo.inverseWorldRotation.Convert(),
                    NoteRotation = noteCutInfo.noteRotation.Convert(),
                    NotePosition = noteCutInfo.notePosition.Convert(),
                });
            
            } else if (noteData.colorType != ColorType.None /* not bomb */ && element is MissScoringElement) {

                _noteKeyframes.Add(new NoteEvent() {
                    
                    NoteID = noteID,
                    EventType = NoteEventType.Miss,
                    CutPoint = VRPosition.None(),
                    CutNormal = VRPosition.None(),
                    SaberDirection = VRPosition.None(),
                    SaberType = (int)noteData.colorType,
                    DirectionOK = false, CutDirectionDeviation = 0f,
                    SaberSpeed = 0f, 
                    CutAngle = 0f,
                    CutDistanceToCenter = 0f,
                    BeforeCutRating = 0f,
                    AfterCutRating = 0f,
                    Time = audioTimeSyncController.songTime,
                    UnityTimescale = UnityEngine.Time.timeScale,
                    TimeSyncTimescale = audioTimeSyncController.timeScale,

                    // I couldn't find where to grab these for misses
                    TimeDeviation = 0f,
                    WorldRotation = new VRRotation(),
                    InverseWorldRotation = new VRRotation(),
                    NoteRotation = new VRRotation(),
                    NotePosition = new VRPosition(),
                });
            }
        }

        [AffinityPrefix, AffinityPatch(typeof(ScoreController), nameof(ScoreController.HandleNoteWasCut))]
        protected void BadCutInfoCollector(NoteController noteController, in NoteCutInfo noteCutInfo) {

            _collectedBadCutInfos.Add(noteController.noteData, noteCutInfo);
        }

        public void Dispose() {

            _scoreController.scoringForNoteFinishedEvent -= ScoreController_scoringForNoteFinishedEvent;
            _scoreController.scoringForNoteStartedEvent -= ScoreController_scoringForNoteStartedEvent;
        }

        public List<NoteEvent> Export() {

            return _noteKeyframes;
        }
    }
}
