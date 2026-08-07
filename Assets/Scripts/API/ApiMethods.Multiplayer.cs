using OpenBrush.Multiplayer;
using OpenBrush.Multiplayer.Recording;
using UnityEngine;
namespace TiltBrush
{
    public static partial class ApiMethods
    {
        [ApiEndpoint("multiplayer.join", "Joins a multiplayer room")]
        public static void MultiplayerJoin(string nickname, string roomName, bool isPrivate, int maxPlayers, bool silentRoom, bool viewOnlyRoom)
        {
            ConnectionUserInfo userInfo = new ConnectionUserInfo
            {
                Nickname = nickname,
                UserId = MultiplayerManager.m_Instance.UserInfo.UserId,
                Role = MultiplayerManager.m_Instance.UserInfo.Role
            };
            MultiplayerManager.m_Instance.UserInfo = userInfo;

            RoomCreateData roomData = new RoomCreateData
            {
                roomName = roomName,
                @private = isPrivate,
                maxPlayers = maxPlayers,
                silentRoom = silentRoom,
                viewOnlyRoom = viewOnlyRoom
            };
            var joinRoomTask = MultiplayerManager.m_Instance.JoinRoom(roomData);
            AsyncHelpers.RunSync(() => joinRoomTask);
            if (joinRoomTask.Result)
            {
                // TODO - we do this when using the non-VR UI
                // Should we also do it here?
                // var cameraPos = App.VrSdk.GetVrCamera().transform.position;
                // cameraPos.y += 12;
                // App.VrSdk.GetVrCamera().transform.position = cameraPos;
            }
            else
            {
                Debug.LogError("Failed to join room");
            }
        }

        [ApiEndpoint("multiplayer.leave", "Leaves a multiplayer room")]
        public static void MultiplayerLeave()
        {
            var leaveRoomTask = MultiplayerManager.m_Instance.LeaveRoom();
            AsyncHelpers.RunSync(() => leaveRoomTask);
            if (!leaveRoomTask.Result)
            {
                Debug.LogError("Failed to leave room");
            }
        }

        [ApiEndpoint("colocation.host", "Hosts a Meta Quest colocation session")]
        public static void ColocationHost()
        {
            if (!ColocationBootstrap.Host(MultiplayerManager.m_Instance.MyOculusUserId))
            {
                Debug.LogWarning("Colocation is unsupported or has not finished initializing.");
            }
        }

        [ApiEndpoint("colocation.join", "Joins a Meta Quest colocation session")]
        public static void ColocationJoin()
        {
            if (!ColocationBootstrap.Join(MultiplayerManager.m_Instance.MyOculusUserId))
            {
                Debug.LogWarning("Colocation is unsupported or has not finished initializing.");
            }
        }

        [ApiEndpoint("colocation.leave", "Leaves the current Meta Quest colocation session")]
        public static void ColocationLeave()
        {
            ColocationBootstrap.Leave();
        }

        [ApiEndpoint("recording.start", "Starts flight-recording the current multiplayer session (strokes + player pose) for later replay")]
        public static void RecordingStart()
        {
            if (!SessionRecorder.m_Instance.StartRecording())
            {
                Debug.LogWarning("Failed to start session recording - see previous error.");
            }
        }

        [ApiEndpoint("recording.stop", "Stops the current flight recording")]
        public static void RecordingStop()
        {
            SessionRecorder.m_Instance.StopRecording();
        }

#if MP_PHOTON
        [ApiEndpoint("replay.list", "Lists available flight recordings, newest first, comma-separated")]
        public static string ReplayList()
        {
            return string.Join(",", SessionPlaybackController.m_Instance.ListRecordings());
        }

        [ApiEndpoint("replay.start", "Replays a flight recording into the current sketch. Omit filename to replay the most recent recording")]
        public static void ReplayStart(string filename = "")
        {
            if (!SessionPlaybackController.m_Instance.StartReplay(filename))
            {
                Debug.LogWarning("Failed to start replay - see previous error.");
            }
        }

        [ApiEndpoint("replay.stop", "Stops the current replay")]
        public static void ReplayStop()
        {
            SessionPlaybackController.m_Instance.Stop();
        }

        [ApiEndpoint("multiplayer.liveStrokeStreamIntervalMs", "Sets how often (in milliseconds) a locally-drawn stroke's new points are flushed to other players while still being drawn, for fluid remote rendering. Lower is smoother but sends more RPC traffic - default 100")]
        public static void SetLiveStrokeStreamIntervalMs(float ms)
        {
            MultiplayerManager.m_Instance.LiveStrokeFlushIntervalMs = Mathf.Max(0f, ms);
        }
#endif // MP_PHOTON

    }
}
