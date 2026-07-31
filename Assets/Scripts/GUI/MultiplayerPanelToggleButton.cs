// Copyright 2026 The Open Brush Authors
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

using OpenBrush.Multiplayer;
using UnityEngine;

namespace TiltBrush
{
    // Same purpose as MultiplayerPanelButton (routes presses to
    // MultiplayerPanel.OnMultiplayerPanelButtonPressed), but for toggle-style buttons (on/off
    // state via IsToggledOn) rather than one-shot actions. ToggleButton and MultiplayerPanelButton
    // are sibling subclasses of OptionButton, not a chain, so a single button class can't get both
    // behaviours for free - this duplicates MultiplayerPanelButton's dispatch-to-panel call on top
    // of ToggleButton's own toggle behaviour.
    public class MultiplayerPanelToggleButton : ToggleButton
    {
        protected override void Start()
        {
            if (m_Command == SketchControlsScript.GlobalCommands.MultiplayerToggleHideRemoteAvatars
                && MultiplayerManager.m_Instance != null)
            {
                IsToggledOn = MultiplayerManager.m_Instance.HideRemotePlayerAvatars;
            }
            base.Start();
        }

        protected override void OnButtonPressed()
        {
            base.OnButtonPressed();

            MultiplayerPanel popup = m_Manager.GetComponent<MultiplayerPanel>();
            Debug.Assert(popup != null);
            popup.OnMultiplayerPanelButtonPressed(this);
        }
    }
} // namespace TiltBrush
