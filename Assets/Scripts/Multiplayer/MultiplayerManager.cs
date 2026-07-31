// Copyright 2023 The Open Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
#if MP_PHOTON
using Fusion;
#endif
using UnityEngine;
#if OCULUS_SUPPORTED
using OVRPlatform = Oculus.Platform;
#endif
using TiltBrush;
using UnityEngine.Serialization;

namespace OpenBrush.Multiplayer
{
    public enum MultiplayerType
    {
        None,
        Colyseus = 1,
        Photon = 2,
    }

    public class MultiplayerManager : MonoBehaviour
    {

        public static MultiplayerManager m_Instance;
        public MultiplayerType m_MultiplayerType;
        public event Action Disconnected;

        private IDataConnectionHandler m_Manager;
        private IVoiceConnectionHandler m_VoiceManager;

        public ITransientData<PlayerRigData> m_LocalPlayer;
        [HideInInspector] public RemotePlayers m_RemotePlayers;

        public Action<int, ITransientData<PlayerRigData>> localPlayerJoined;
        public Action<RemotePlayer> remotePlayerJoined;
        public Action<int, GameObject> remoteVoiceAdded;
        public Action<int> playerLeft;
        public Action<List<RoomData>> roomDataRefreshed;

        public event Action<ConnectionState> StateUpdated;
        public event Action<bool> RoomOwnershipUpdated;
        public event Action<ConnectionUserInfo> UserInfoStateUpdated;

        private List<RoomData> m_RoomData = new List<RoomData>();
        private double? m_NetworkOffsetTimestamp = null;

        ulong myOculusUserId;
        // Read-only so callers (e.g. colocation.host/colocation.join in
        // Assets/Scripts/API/ApiMethods.Multiplayer.cs) can't accidentally desync this from
        // the value MultiplayerManager itself resolved via the Oculus Platform SDK.
        public ulong MyOculusUserId => myOculusUserId;

        List<ulong> oculusPlayerIds;
        internal string UserId;
        [HideInInspector] public string CurrentRoomName;

        // Local, per-device rendering preference - never networked or synced to other players.
        // Each device decides independently whether it wants to see other players' headset/
        // controller models, e.g. a static recording rig wants brushstrokes only, with nobody
        // else's avatar cluttering the shot. Persisted so it survives app restarts.
        private const string kHideRemotePlayerAvatarsPrefKey = "MultiplayerHideRemotePlayerAvatars";
        public bool HideRemotePlayerAvatars
        {
            get => PlayerPrefs.GetInt(kHideRemotePlayerAvatarsPrefKey, 0) == 1;
            set => PlayerPrefs.SetInt(kHideRemotePlayerAvatarsPrefKey, value ? 1 : 0);
        }

        private ConnectionState _state;

        public ConnectionState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    StateUpdated?.Invoke(_state);
                }
            }
        }

        public string LastError { get; private set; }

#if MP_PHOTON
        // Colocation (OpenBrush.Multiplayer.Colocation.ColocationBootstrap) needs the live
        // NetworkRunner to construct PhotonColocationSignalling - m_Manager has no other
        // public way to expose it, since IDataConnectionHandler stays backend-agnostic.
        // MP_FUSION (used elsewhere in this file's own using directive) is not actually a
        // defined scripting symbol in this project (see ProjectSettings.asset) - MP_PHOTON is
        // the real one, matching PhotonManager.cs/PhotonRPC.cs's own guard.
        public NetworkRunner Runner => (m_Manager as PhotonManager)?.Runner;
#endif

        public ConnectionUserInfo UserInfo
        {
            get => m_Manager?.UserInfo ?? default;
            set
            {
                if (m_Manager != null)
                {
                    m_Manager.UserInfo = value;
                }
            }
        }
        private string m_oldNickName = null;

        [HideInInspector] public RoomCreateData CurrentRoomData;

        private bool _isUserRoomOwner = false;
        private bool isUserRoomOwner
        {
            get => _isUserRoomOwner;
            set
            {
                _isUserRoomOwner = value;
                RoomOwnershipUpdated?.Invoke(value);
            }
        }

        private bool _isViewOnly;

        [NonSerialized] public bool m_IsAllMutedForMe;
        [NonSerialized] public bool m_IsAllMutedForAll;

        public bool IsViewOnly
        {
            get
            {
                // If the user is not in a room, then they can't be view only
                if (State != ConnectionState.IN_ROOM) return false;
                // Room owners are never in view-only mode
                if (isUserRoomOwner) return false;
                return _isViewOnly;
            }
            set => _isViewOnly = value;
        }

        void Awake()
        {
            m_Instance = this;
            oculusPlayerIds = new List<ulong>();
        }

        void Start()
        {
#if OCULUS_SUPPORTED
            // TEMP DEV WORKAROUND (remove once root cause is fixed): on this dev machine's PC/Link build,
            // com.meta.xr.sdk.platform ships no Windows LibOVRPlatform64_1.dll (Android-only in this
            // package version), so Oculus.Platform.Core.Initialize() throws DllNotFoundException and
            // GetLoggedInUser() returns null here, crashing the rest of Start() before Photon ever
            // connects. Not believed to reproduce in production (properly published/signed builds,
            // launched via the real Oculus/Meta Quest Link library rather than a raw .exe).
            // Only affects Oculus-account-based spatial anchor sharing (colocation) below - core Photon
            // Fusion multiplayer (strokes/avatars) does not depend on myOculusUserId/oculusPlayerIds.
            try
            {
                // DIAGNOSTIC (2026-07-28): investigating GetLoggedInUser() returning a fast,
                // error-free "success" with ID=0 on Android sideloaded builds - per Meta's own
                // docs an entitlement check should run before user-data calls, and this was
                // entirely missing from the codebase. Logging both Platform's actual init state
                // and a real entitlement check result so we know definitively whether this is
                // Data-Use-Checkup/entitlement related (external, dashboard-side) versus
                // something else, rather than continuing to guess from response timing alone.
                bool platformInitialized = OVRPlatform.Core.IsInitialized();
                Debug.Log($"[Diagnostic] Oculus Platform Core.IsInitialized() = {platformInitialized}");

                // IsUserEntitledToApplication() returns null (not a failed Request) when the
                // platform isn't initialized - guarded explicitly here, in its own try/catch,
                // so a null OnComplete() call can never suppress the GetLoggedInUser() call
                // below it.
                if (platformInitialized)
                {
                    try
                    {
                        OVRPlatform.Entitlements.IsUserEntitledToApplication().OnComplete((entitlementMsg) => {
                            if (!entitlementMsg.IsError)
                            {
                                Debug.Log("[Diagnostic] Entitlement check PASSED - user is entitled to this application.");
                            }
                            else
                            {
                                Debug.LogError($"[Diagnostic] Entitlement check FAILED: {entitlementMsg.GetError()}");
                            }
                        });
                    }
                    catch (Exception entitlementEx)
                    {
                        Debug.LogError($"[Diagnostic] Entitlement check threw: {entitlementEx.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning("[Diagnostic] Skipping entitlement check - Oculus Platform Core is not initialized.");
                }

                OVRPlatform.Users.GetLoggedInUser().OnComplete((msg) => {
                    if (!msg.IsError)
                    {
                        myOculusUserId = msg.GetUser().ID;
                        Debug.Log($"OculusID: {myOculusUserId}");
                        oculusPlayerIds.Add(myOculusUserId);
                    }
                    else
                    {
                        Debug.LogError(msg.GetError());
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TEMP WORKAROUND] Skipping Oculus user lookup - Platform not available: {ex.Message}");
            }
#endif

            State = ConnectionState.INITIALIZING;
            switch (m_MultiplayerType)
            {
                case MultiplayerType.Photon:
#if MP_PHOTON
                    m_Manager = new PhotonManager(this);
                    m_Manager.Disconnected += OnConnectionHandlerDisconnected;
                    if (m_Manager != null) ControllerConsoleScript.m_Instance.AddNewLine("PhotonManager Loaded");
                    else ControllerConsoleScript.m_Instance.AddNewLine("PhotonManager Not Loaded");
#endif
#if MP_PHOTON
                    m_VoiceManager = new PhotonVoiceManager(this);
                    if (m_VoiceManager != null) ControllerConsoleScript.m_Instance.AddNewLine("PhotonVoiceManager Loaded");
                    else ControllerConsoleScript.m_Instance.AddNewLine("PhotonVoiceManager Not Loaded");
#endif 
                    break;
                default:
                    return;
            }
            if (m_VoiceManager != null && m_Manager != null) State = ConnectionState.INITIALIZED;

            roomDataRefreshed += OnRoomDataRefreshed;
            localPlayerJoined += OnLocalPlayerJoined;
            remotePlayerJoined += OnRemotePlayerJoined;
            remoteVoiceAdded += OnRemoteVoiceConnected;
            playerLeft += OnPlayerLeft;
            StateUpdated += UpdateSketchMemoryScriptTimeOffset;

            SketchMemoryScript.m_Instance.CommandPerformed += OnCommandPerformed;
            SketchMemoryScript.m_Instance.CommandUndo += OnCommandUndo;
            SketchMemoryScript.m_Instance.CommandRedo += OnCommandRedo;
        }

        void OnDestroy()
        {
            roomDataRefreshed -= OnRoomDataRefreshed;
            localPlayerJoined -= OnLocalPlayerJoined;
            remotePlayerJoined -= OnRemotePlayerJoined;
            remoteVoiceAdded -= OnRemoteVoiceConnected;
            playerLeft -= OnPlayerLeft;
            StateUpdated -= UpdateSketchMemoryScriptTimeOffset;

            SketchMemoryScript.m_Instance.CommandPerformed -= OnCommandPerformed;
            SketchMemoryScript.m_Instance.CommandUndo -= OnCommandUndo;
            SketchMemoryScript.m_Instance.CommandRedo -= OnCommandRedo;
        }

        public async Task<bool> Connect()
        {
            State = ConnectionState.CONNECTING;

            var successData = false;
            if (m_Manager != null) successData = await m_Manager.Connect();

            var successVoice = false;
            if (m_VoiceManager != null) successVoice = await m_VoiceManager.Connect();

            if (!successData)
            {
                State = ConnectionState.ERROR;
                LastError = m_Manager.LastError;
            }
            else if (!successVoice)
            {
                State = ConnectionState.ERROR;
                LastError = m_VoiceManager.LastError;
            }
            else State = ConnectionState.IN_LOBBY;


            return successData & successVoice;
        }

        public async Task<bool> JoinRoom(RoomCreateData RoomData)
        {
            State = ConnectionState.JOINING_ROOM;

            // check if room exist to determine if user is room owner
            DoesRoomNameExist(RoomData.roomName);
            if (!isUserRoomOwner) SketchMemoryScript.m_Instance.ClearMemory();

            bool successData = false;
            if (m_Manager != null) successData = await m_Manager.JoinRoom(RoomData);

            bool successVoice = false;
            if (m_VoiceManager != null) successVoice = await m_VoiceManager.JoinRoom(RoomData);
            m_VoiceManager?.StartSpeaking();

            if (!successData)
            {
                State = ConnectionState.ERROR;
                LastError = m_Manager.LastError;
            }
            else if (!successVoice)
            {
                State = ConnectionState.ERROR;
                LastError = m_VoiceManager.LastError;
            }
            else State = ConnectionState.IN_ROOM;

            //asing the room name to the current room name
            RoomCreateData CurrentRoomData = RoomData;

            return successData & successVoice;
        }

        public async Task<bool> LeaveRoom(bool force = false)
        {
            State = ConnectionState.LEAVING_ROOM;

            bool successData = false;
            if (m_Manager != null) successData = await m_Manager.LeaveRoom();

            bool successVoice = false;
            m_VoiceManager?.StopSpeaking();
            if (m_VoiceManager != null) successVoice = await m_VoiceManager.LeaveRoom();

            if (!successData)
            {
                State = ConnectionState.ERROR;
                LastError = m_Manager.LastError;
            }
            else if (!successVoice)
            {
                State = ConnectionState.ERROR;
                LastError = m_VoiceManager.LastError;
            }
            else State = ConnectionState.IN_LOBBY;

            return successData & successVoice;
        }

        public async Task<bool> Disconnect()
        {
            State = ConnectionState.DISCONNECTING;

            bool successData = false;
            if (m_Manager != null) successData = await m_Manager.Disconnect();

            bool successVoice = false;
            if (m_VoiceManager != null) successVoice = await m_VoiceManager.Disconnect();

            if (!successData)
            {
                State = ConnectionState.ERROR;
                LastError = m_Manager?.LastError;
            }
            else if (!successVoice)
            {
                State = ConnectionState.ERROR;
                LastError = m_VoiceManager?.LastError;
            }
            else State = ConnectionState.DISCONNECTED;

            return successData && successVoice;
        }

        public bool DoesRoomNameExist(string roomName)
        {

            bool roomExist = m_RoomData.Any(room => room.roomName == roomName);

            // Room does not exist
            if (!roomExist)
            {
                isUserRoomOwner = true;
                return false;
            }

            // Find the room with the given name
            RoomData? room = m_RoomData.FirstOrDefault(r => r.roomName == roomName);

            // Room exists 
            RoomData r = (RoomData)room;
            if (r.numPlayers == 0) isUserRoomOwner = true;// and is empty user becomes room owner
            else isUserRoomOwner = false; // not empty user is not the room owner

            return true;
        }

        public void RoomOwnershipReceived(RemotePlayerSettings[] playerSettings, RoomCreateData roomData)
        {

            CurrentRoomData = roomData;

            foreach (var p in playerSettings)
            {
                var PlayerId = p.m_PlayerId;
                var mplayer = m_Instance.GetPlayerById(PlayerId);
                if (mplayer == null) continue;
                mplayer.m_IsMutedForAll = p.m_IsMutedForAll;
                mplayer.m_IsViewOnly = p.m_IsViewOnly;
            }
            // TODO Refresh GUI

            isUserRoomOwner = true;
        }

        public void RoomOwnershipTransferToUser(int playerId)
        {
            if (!isUserRoomOwner) return;

            var playerSettings = new RemotePlayerSettings[m_RemotePlayers.List.Count];

            for (var i = 0; i < m_RemotePlayers.List.Count; i++)
            {
                var player = m_RemotePlayers.List[i];
                playerSettings[i] = new RemotePlayerSettings(player.PlayerId, player.m_IsMutedForAll, player.m_IsViewOnly);
            }
            m_Manager.RpcTransferRoomOwnership(playerId, playerSettings, CurrentRoomData);
            isUserRoomOwner = false;
        }

        // Not really a multiplayer function but placing it here for consistency with other methods
        public void MutePlayerForMe(bool muted, int playerId)
        {
            GetPlayerById(playerId).m_IsMutedForMe = muted;
            MultiplayerAudioSourcesManager.m_Instance.SetMuteForPlayer(playerId, muted);
        }

        public void MutePlayerForAll(bool muted, int playerId)
        {
            if (!isUserRoomOwner) return;
            GetPlayerById(playerId).m_IsMutedForAll = muted;
            MultiplayerAudioSourcesManager.m_Instance.SetMuteForPlayer(playerId, muted);
            m_Manager.RpcMutePlayer(muted, playerId);
        }

        public void SetUserViewOnlyMode(bool isViewOnly, int playerId)
        {
            if (!isUserRoomOwner) return;
            GetPlayerById(playerId).m_IsViewOnly = isViewOnly;
            m_Manager.RpcSetUserViewOnlyMode(isViewOnly, playerId);
        }

        public void KickPlayerOut(int playerId)
        {
            if (!isUserRoomOwner) return;
            m_Manager.RpcKickPlayerOut(playerId);
        }

        void OnRoomDataRefreshed(List<RoomData> rooms)
        {
            m_RoomData = rooms;
        }

        void Update()
        {
            if (App.CurrentState != App.AppState.Standard || m_Manager == null)
            {
                return;
            }

            if (State != ConnectionState.IN_ROOM)
            {
                m_oldNickName = null;
                return;
            }

            m_Manager.Update();
            m_VoiceManager.Update();

            // Transmit local player data relative to scene origin
            var headRelativeToScene = App.Scene.AsScene[App.VrSdk.GetVrCamera().transform];
            var pointerRelativeToScene = App.Scene.AsScene[PointerManager.m_Instance.MainPointer.transform];
            var headScale = App.VrSdk.GetVrCamera().transform.localScale;
            var leftController = InputManager.m_Instance.GetController(InputManager.ControllerName.Brush).transform;
            var rightController = InputManager.m_Instance.GetController(InputManager.ControllerName.Wand).transform;
            var leftHandRelativeToScene = App.Scene.AsScene[leftController];
            var rightHandRelativeToScene = App.Scene.AsScene[rightController];

            var data = new PlayerRigData
            {
                HeadPosition = headRelativeToScene.translation,
                HeadRotation = headRelativeToScene.rotation,
                ToolPosition = pointerRelativeToScene.translation,
                ToolRotation = pointerRelativeToScene.rotation,
                LeftHandPosition = leftHandRelativeToScene.translation,
                LeftHandRotation = leftHandRelativeToScene.rotation,
                RightHandPosition = rightHandRelativeToScene.translation,
                RightHandRotation = rightHandRelativeToScene.rotation,

                BrushData = new BrushData
                {
                    Color = PointerManager.m_Instance.MainPointer.GetCurrentColor(),
                    Size = PointerManager.m_Instance.MainPointer.BrushSize01,
                    Guid = BrushController.m_Instance.ActiveBrush?.m_Guid.ToString(),
                },
                ExtraData = new ExtraData
                {
                    OculusPlayerId = myOculusUserId,
                },
                IsRoomOwner = isUserRoomOwner,
                SceneScale = App.Scene.Pose.scale,
                isReceivingVoiceTransmission = m_VoiceManager.isTransmitting,
                Nickname = UserInfo.Nickname //TODO: remove from PlayerRigData or encode it and use photon to retrieve the string
            };



            if (m_LocalPlayer != null)
            {
                m_LocalPlayer.TransmitData(data);
            }


            // Update remote user refs, and send Anchors if new player joins.
            bool newUser = false;
            foreach (var playerData in m_RemotePlayers.List)
            {
                ITransientData<PlayerRigData> player = playerData.TransientData;

                if (!player.IsSpawned) continue;

                data = player.ReceiveData();
#if OCULUS_SUPPORTED
                // New user, share the anchor with them
                if (data.ExtraData.OculusPlayerId != 0 && !oculusPlayerIds.Contains(data.ExtraData.OculusPlayerId))
                {
                    Debug.Log("detected new user!");
                    Debug.Log(data.ExtraData.OculusPlayerId);
                    oculusPlayerIds.Add(data.ExtraData.OculusPlayerId);
                    newUser = true;
                }
#endif // OCULUS_SUPPORTED
            }

            if (newUser)
            {
                ShareAnchors();
            }
        }

        void OnLocalPlayerJoined(int id, ITransientData<PlayerRigData> playerData)
        {
            // the user is the room owner if is the firt to get in 
            isUserRoomOwner = m_Manager.GetPlayerCount() == 1 ? true : false;
            // if not room owner clear scene 
            if (!isUserRoomOwner) SketchMemoryScript.m_Instance.ClearMemory();

            m_LocalPlayer = playerData;
            m_LocalPlayer.PlayerId = id;

        }

        void OnRemotePlayerJoined(RemotePlayer newRemotePlayer)
        {
            m_RemotePlayers.AddPlayer(newRemotePlayer);

            if (!isUserRoomOwner) return;  //below this line is only room owner responsability 

            MultiplayerSceneSync.m_Instance.StartSyncronizationForUser(newRemotePlayer.PlayerId);
            if (CurrentRoomData.silentRoom == true) MutePlayerForAll(true, newRemotePlayer.PlayerId);
            if (CurrentRoomData.viewOnlyRoom == true) SetUserViewOnlyMode(true, newRemotePlayer.PlayerId);
        }

        public void SetRoomSilent(bool isSilent)
        {
            if (!isUserRoomOwner) return;
            CurrentRoomData.silentRoom = isSilent;
            for (int i = 0; i < m_RemotePlayers.List.Count; i++)
            {
                var player = m_RemotePlayers.List[i];
                player.m_IsMutedForAll = CurrentRoomData.silentRoom;
                MutePlayerForAll(player.m_IsMutedForAll, player.PlayerId);
            }
        }

        public void SetRoomViewOnly(bool isViewOnly)
        {
            if (!isUserRoomOwner) return;
            CurrentRoomData.viewOnlyRoom = isViewOnly;
            for (int i = 0; i < m_RemotePlayers.List.Count; i++)
            {
                var player = m_RemotePlayers.List[i];
                player.m_IsViewOnly = CurrentRoomData.viewOnlyRoom;
                SetUserViewOnlyMode(player.m_IsViewOnly, player.PlayerId);
            }
        }

        public RemotePlayer GetPlayerById(int id)
        {
            RemotePlayer playerData = m_RemotePlayers.List.FirstOrDefault(x => x.PlayerId == id);
            if (playerData == null)
            {
                Debug.LogWarning($"PlayerRigData with ID {id} not found");
                return null;
            }

            if (playerData.PlayerGameObject == null)
            {
                Debug.LogWarning($"RemotePlayerGameObject with ID {id} not found");
                return null;
            }

            return playerData;
        }

        public void OnRemoteVoiceConnected(int id, GameObject voicePrefab)
        {
            var playerData = GetPlayerById(id);
            if (playerData == null) return;

            Transform headTransform = playerData.PlayerGameObject.transform.Find("HeadTransform");
            if (headTransform != null)
            {
                voicePrefab.transform.SetParent(headTransform, false);
                playerData.VoiceGameObject = voicePrefab;
            }
            else
            {
                Debug.LogWarning($"HeadTransform not found in {playerData.PlayerGameObject.name}");
            }

            AudioSource audioSource = voicePrefab.GetComponent<AudioSource>();
            if (audioSource == null)
            {
                Debug.LogWarning($"VoicePrefab with ID {id} lack AudioSource :S ");
                return;
            }
            MultiplayerAudioSourcesManager.m_Instance.AddAudioSource(id, audioSource);
        }

        public void SendLargeDataToPlayer(int playerId, byte[] Data, int percentage)
        {
            m_Manager.SendLargeDataToPlayer(playerId, Data, percentage);
        }

        void OnPlayerLeft(int id)
        {
            if (m_LocalPlayer.PlayerId == id)
            {
                m_LocalPlayer = null;
                Debug.Log("Possible to get here!");
                return;
            }

            m_RemotePlayers.RemovePlayerById(id);

            // Reassign Ownership if needed 
            // Check if any remaining player is the room owner
            bool anyRoomOwner = m_RemotePlayers.List.Any(player => m_Manager.GetPlayerRoomOwnershipStatus(player.PlayerId))
                                || isUserRoomOwner;

            // If there's still a room owner, no reassignment is needed
            if (anyRoomOwner) return;

            // If there are no other players left, the local player becomes the room owner
            if (m_RemotePlayers.List.Count == 0)
            {
                isUserRoomOwner = true;
                return;
            }

            // Since There are other players left
            // Determine the new room owner by the lowest PlayerId
            var allPlayers = new List<RemotePlayer> { new RemotePlayer { PlayerId = m_LocalPlayer.PlayerId } };
            allPlayers.AddRange(m_RemotePlayers.List);

            // Find the player with the lowest PlayerId
            var newOwner = allPlayers.OrderBy(player => player.PlayerId).First();

            // If the new owner is the local player, set the flag
            if (m_LocalPlayer.PlayerId == newOwner.PlayerId) isUserRoomOwner = true;

        }

        public async void OnCommandPerformed(BaseCommand command)
        {
            if (State == ConnectionState.IN_ROOM)
            {
                await m_Manager.PerformCommand(command);
            }
        }

        public async void SendCommandToPlayer(BaseCommand command, int playerID)
        {
            if (State == ConnectionState.IN_ROOM)
            {
                await m_Manager.SendCommandToPlayer(command, playerID);
            }
        }

        public async Task<bool> CheckCommandReception(BaseCommand command, int id)
        {
            if (State == ConnectionState.IN_ROOM)
            {
                return await m_Manager.CheckCommandReception(command, id);
            }

            return false;
        }

        public async Task<bool> CheckStrokeReception(Stroke stroke, int id)
        {
            if (State == ConnectionState.IN_ROOM)
            {
                return await m_Manager.CheckStrokeReception(stroke, id);
            }

            return false;
        }

        public void OnCommandUndo(BaseCommand command)
        {
            if (State == ConnectionState.IN_ROOM)
            {
                m_Manager.UndoCommand(command);
            }
        }

        public void OnCommandRedo(BaseCommand command)
        {
            if (State == ConnectionState.IN_ROOM)
            {
                m_Manager.RedoCommand(command);
            }
        }

        async void ShareAnchors()
        {
#if OCULUS_SUPPORTED
            Debug.Log($"sharing to {oculusPlayerIds.Count} Ids");
            var success = await OculusMRController.m_Instance.m_SpatialAnchorManager.ShareAnchors(oculusPlayerIds);

            if (success)
            {
                if (!OculusMRController.m_Instance.m_SpatialAnchorManager.AnchorUuid.Equals(String.Empty))
                {
                    await m_Manager.RpcSyncToSharedAnchor(OculusMRController.m_Instance.m_SpatialAnchorManager.AnchorUuid);
                }
            }
#endif // OCULUS_SUPPORTED
        }

        private void OnConnectionHandlerDisconnected()
        {
            m_LocalPlayer = null;// Clean up local player reference
            m_RemotePlayers.ClearList();// Clean up remote player references
            LastError = null;
            State = ConnectionState.DISCONNECTED;
            StateUpdated?.Invoke(State);
            Disconnected?.Invoke();// Invoke the Disconnected event
        }

        public void StartSpeaking()
        {
            m_VoiceManager?.StartSpeaking();
        }

        public void StopSpeaking()
        {
            m_VoiceManager?.StopSpeaking();
        }

        public bool IsDisconnectable()
        {

            return State == ConnectionState.IN_ROOM || State == ConnectionState.IN_LOBBY;
        }

        public bool IsConnectable()
        {
            return State == ConnectionState.INITIALIZED || State == ConnectionState.DISCONNECTED;
        }

        public bool CanJoinRoom()
        {
            return State == ConnectionState.IN_LOBBY;
        }

        public bool CanLeaveRoom()
        {
            return State == ConnectionState.IN_ROOM;
        }

        public bool HasRemotePlayersInRoom()
        {
            return State == ConnectionState.IN_ROOM && m_RemotePlayers != null && m_RemotePlayers.List.Count > 0;
        }

        public bool IsUserRoomOwner()
        {
            return isUserRoomOwner;
        }

        public bool IsRemotePlayerStillConnected(int playerId)
        {
            if (m_RemotePlayers.List.Any(player => player.PlayerId == playerId)) return true;
            return false;
        }

        public int? GetNetworkedTimestampMilliseconds()
        {
            if (State == ConnectionState.IN_ROOM)
            {
                if (m_Manager != null) return m_Manager.GetNetworkedTimestampMilliseconds();
            }

            return null;
        }

        // this only needs to be done once when the room is created
        private void UpdateSketchMemoryScriptTimeOffset(ConnectionState state)
        {
            // Ensure the offset is set only once upon connecting as room owner
            if (state == ConnectionState.IN_ROOM
                && isUserRoomOwner
                && m_NetworkOffsetTimestamp == null)
            {
                // Capture the current sketch time as the base offset for network synchronization
                m_NetworkOffsetTimestamp = (int)(App.Instance.CurrentSketchTime * 1000);
                SketchMemoryScript.m_Instance.SetTimeOffsetToAllStacks((int)m_NetworkOffsetTimestamp);
            }
        }
    }
}
