using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Tools;
using UnityEngine.UIElements;

using CG.Cloud;
using CG.Cloud.Authenticate;
using CG.Profile;
using Gameplay.Hub;
using Gameplay.Quests;
using Newtonsoft.Json;
using ResourceAssets;
using UI;
using UI.Core;
using UI.Core.Audio;

namespace SaveSlots;

[Serializable]
public class PreservedSessionWithMetadata
{
    public PreservedGameSession Session;
    public DateTime Timestamp;
    public PreservedSessionWithMetadata(PreservedGameSession session, DateTime timestamp)
    {
        Session = session;
        Timestamp = timestamp;
    }
}

[BepInPlugin(pluginGUID, pluginName, pluginVersion)]
public class SaveSlotsPlugin : BaseUnityPlugin
{
    const string pluginGUID = "com.gummyboars.voidcrew.saveslots";
    const string pluginName = "Save Slots";
    const string pluginVersion = "1.0.0";

    private Harmony HarmonyInstance = null;

    public static ManualLogSource logger = BepInEx.Logging.Logger.CreateLogSource(pluginName);

    private void Awake()
    {
        SaveSlotsPlugin.logger.LogInfo($"Loading plugin {pluginName}.");
        try
        {
            HarmonyInstance = new Harmony(pluginGUID);
            Assembly assembly = Assembly.GetExecutingAssembly();
            HarmonyInstance.PatchAll(assembly);
            SaveSlotsPlugin.logger.LogInfo($"Plugin {pluginName} version {pluginVersion} loaded.");
        }
        catch (Exception e)
        {
            SaveSlotsPlugin.logger.LogError($"Could not load plugin {pluginName}: {e}");
        }
    }
}

[HarmonyPatch]
public static class SessionSaver
{
    public static Dictionary<string, PreservedSessionWithMetadata> Sessions = new Dictionary<string, PreservedSessionWithMetadata>();
    public static string SessionsText = null;

    public static void Set(PreservedGameSession session)
    {
        DateTime now = DateTime.UtcNow;
        SaveSlotsPlugin.logger.LogInfo($"Saving session with id {session.GameSessionID} at time {now}");
        Sessions[session.GameSessionID] = new PreservedSessionWithMetadata(session, now);
    }

    // When loading the PlayerData from the cloud, also load the PreservedSessionS from the cloud.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CloudLocalProfile), MethodType.Constructor)]
    private static void PatchAfterProfileCreation()
    {
        CloudAuthenticate.OnAuthenticated += LoadPreservedSessions;
    }

    // LoadPreservedSessions and LoadCloudProfile will race. Whichever finishes second is responsible for
    // calling InitializePreservedSessions. TODO: is there a way to combine them? Various profile
    // initialization hooks may depend on the preserved sessions.
    private static async void LoadPreservedSessions()
    {
        SessionsText = await CloudSyncController.ReadFromCloud("PreservedSessions");  // With an s
        if (SessionsText == null)
        {
            SessionsText = "";
        }
        if (PlayerProfile.IsInitalized)
        {
            SaveSlotsPlugin.logger.LogInfo("Preserved sessions were loaded after the rest of the profile.");
            InitializePreservedSessions();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ProfileDataValidator), "PostSetupValidate")]
    private static void PatchAfterProfileLoad()
    {
        if (SessionsText != null)
        {
            SaveSlotsPlugin.logger.LogInfo("Profile was loaded after preserved sessions.");
            InitializePreservedSessions();
        }
    }

    // Save preserved sessions in this class. If none exist (first time using the mod), save the current preserved session.
    private static void InitializePreservedSessions()
    {
        if (PlayerProfile.Instance is CloudLocalProfile clp)  // Only works for cloud profiles
        {
            if (string.IsNullOrEmpty(SessionsText))
            {
                SaveSlotsPlugin.logger.LogInfo("No preserved sessions found");
                // No preserved sessions
                if (clp.PreservedSession.PreservedSession == null || string.IsNullOrEmpty(clp.PreservedSession.PreservedSession.GameSessionID))
                {
                    return;
                }
                // The user has a preserved session, but no preserved sessions. This is probably their first time
                // running this mod.
                SaveSlotsPlugin.logger.LogInfo("Populating preserved sessions from preserved session");
                Set(clp.PreservedSession.PreservedSession);
                return;
            }
            Sessions = JsonConvert.DeserializeObject<Dictionary<string, PreservedSessionWithMetadata>>(SessionsText);
            SaveSlotsPlugin.logger.LogInfo($"Loaded {Sessions.Count} preserved sessions");
            if (clp.PreservedSession.PreservedSession == null || string.IsNullOrEmpty(clp.PreservedSession.PreservedSession.GameSessionID))
            {
                return;
            }
            // If the session saved by the user isn't in the list (they turned the mod off and back on?), add it.
            if (!Sessions.ContainsKey(clp.PreservedSession.PreservedSession.GameSessionID))
            {
                SaveSlotsPlugin.logger.LogInfo($"Preserved session {clp.PreservedSession.PreservedSession.GameSessionID} missing from preserved sessions; adding it");
                Set(clp.PreservedSession.PreservedSession);
            }
        }
    }

    // When ClearSession is called, delete the session if it exists.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CloudPlayerPreservedSessionSync), "ClearSession")]
    private static void PatchBeforeSessionClear(CloudPlayerPreservedSessionSync __instance)
    {
        PropertyInfo preservedSessionInfo = AccessTools.Property(typeof(CloudPlayerPreservedSessionSync), "PreservedSession");
        PreservedGameSession session = (PreservedGameSession) preservedSessionInfo.GetValue(__instance);
        if (session == null || string.IsNullOrEmpty(session.GameSessionID))
        {
            return;
        }
        SaveSlotsPlugin.logger.LogInfo($"Removing session with id {session.GameSessionID}");
        Sessions.Remove(session.GameSessionID);
    }

    // After writing to the cloud, also write all the preserved sessions.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CloudPlayerPreservedSessionSync), "ClearSession")]
    private static void PatchAfterSessionClear()
    {
        CloudSyncController.Instance.Write("PreservedSessions", JsonConvert.SerializeObject(Sessions));
        SaveSlotsPlugin.logger.LogInfo($"Wrote {Sessions.Count} preserved sessions");
    }

    // When StoreSession is called, either overwrite the existing session or create a new one.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CloudPlayerPreservedSessionSync), "StoreSession")]
    private static void PatchBeforeSessionSave(PreservedGameSession session)
    {
        if (session == null || string.IsNullOrEmpty(session.GameSessionID))
        {
            return;
        }
        Set(session);
    }

    // After writing to the cloud, also write all the preserved sessions.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CloudPlayerPreservedSessionSync), "StoreSession")]
    private static void PatchAfterSessionSave()
    {
        CloudSyncController.Instance.Write("PreservedSessions", JsonConvert.SerializeObject(Sessions));
        SaveSlotsPlugin.logger.LogInfo($"Wrote {Sessions.Count} preserved sessions");
        MethodInfo SaveFile = AccessTools.Method(typeof(PlayerProfileLocalSave), "SaveFile");
        SaveFile.Invoke(null, new object[] {"PRESERVED_SESSIONS", JsonConvert.SerializeObject(Sessions)});
    }
}

[HarmonyPatch(typeof(GalaxyMapUIController), "UpdateStyles")]
public static class Patch_UpdateStyles
{
    // UpdateStyles is called when the game mode changes. Override to also display the mutators menu
    // when the game mode is changed to preserved.
    private static void Postfix(VisualElement ____mutatorsMenuRoot, GalaxyMapUIController __instance)
    {
        Type GameModeEntry = AccessTools.Inner(typeof(GalaxyMapUIController), "GameModeEntry");
        FieldInfo shownGameModeEntry = AccessTools.Field(typeof(GalaxyMapUIController), "_shownGameModeEntry");
        FieldInfo gameModeType = AccessTools.Field(GameModeEntry, "GamemodeType");
        var shownEntry = shownGameModeEntry.GetValue(__instance);
        if (shownEntry != null)
        {
            GalaxyMapUIController.GamemodeType shownType = (GalaxyMapUIController.GamemodeType) gameModeType.GetValue(shownEntry);
            if (shownType == GalaxyMapUIController.GamemodeType.Preserved)
            {
                ____mutatorsMenuRoot.SetDisplay(true);
            }
        }
    }
}

[HarmonyPatch(typeof(GalaxyMapUIController), "UpdateAvailableGamemodes")]
public static class Patch_UpdateAvailableGamemodes
{
    private static GalaxyMapUIController Instance = null;

    // A number of UI elements are filled in once, which is incompatible with multiple saves. Each time the user
    // switches to a different save, this function must be called to update the UI. The Instance must have been
    // previously set by a call to UpdateAvailableGameModes.
    public static void UpdatePreservedSessionDisplay()
    {
        if (Instance != null)
        {
            MethodInfo UpdatePreservedSessionInfo = AccessTools.Method(typeof(GalaxyMapUIController), "UpdatePreservedSessionInfo");
            UpdatePreservedSessionInfo.Invoke(Instance, new object[] {});

            PreservedGameSession preservedSession = PlayerProfile.Instance.PreservedSession.PreservedSession;
            if (preservedSession != null)
            {
                FieldInfo _includedMutatorsList = AccessTools.Field(typeof(GalaxyMapUIController), "_includedMutatorsList");
                MutatorsElementListVE includedMutatorsList = (MutatorsElementListVE) _includedMutatorsList.GetValue(Instance);
                includedMutatorsList.parent.SetDisplay(preservedSession.Mutators.Count > 0);
                includedMutatorsList.UpdateMutatorsList(preservedSession.Mutators.AsReadOnly());
                if (HubQuestManager.Instance.QuestStartType == HubQuestManager.SessionStartType.Preserved)
                {
                    // We would like to call SelectPreservedSessionQuest, but it short circuits.
                    HubShipManager.Instance.SelectShip(preservedSession.Ship.AsIntArray());
                    MethodInfo NotifyPreservedQuestSelected = AccessTools.Method(typeof(HubQuestManager), "NotifyPreservedQuestSelected");
                    NotifyPreservedQuestSelected.Invoke(HubQuestManager.Instance, new object[] {});
                }
            }

        }
    }

    // Updates the mutators list UI like the above method, but avoids calling NotifyPreservedQuestSelected in
    // order to not trigger an infinite loop from the OnShown handler.
    public static void UpdateJustTheMutatorsList()
    {
        PreservedGameSession preservedSession = PlayerProfile.Instance.PreservedSession.PreservedSession;
        if (Instance != null && preservedSession != null)
        {
            FieldInfo _includedMutatorsList = AccessTools.Field(typeof(GalaxyMapUIController), "_includedMutatorsList");
            MutatorsElementListVE includedMutatorsList = (MutatorsElementListVE) _includedMutatorsList.GetValue(Instance);
            includedMutatorsList.parent.SetDisplay(preservedSession.Mutators.Count > 0);
            includedMutatorsList.UpdateMutatorsList(preservedSession.Mutators.AsReadOnly());
        }
    }

    // Before updating available game modes, select the session that will be the "default" session. If the user
    // has an unmodded saved session, that one is used and nothing is done. Otherwise, the latest session is used.
    private static void Prefix()
    {
        if (PlayerProfile.Instance.PreservedSession.PreservedSession != null && !string.IsNullOrEmpty(PlayerProfile.Instance.PreservedSession.PreservedSession.GameSessionID))
        {
            return;
        }
        PreservedSessionWithMetadata latestSession = null;
        foreach (KeyValuePair<string, PreservedSessionWithMetadata> pair in SessionSaver.Sessions)
        {
            if (latestSession == null || latestSession.Timestamp.CompareTo(pair.Value.Timestamp) <= 0)
            {
                latestSession = pair.Value;
            }
        }
        if (latestSession != null)
        {
            SaveSlotsPlugin.logger.LogInfo($"Using latest saved session {latestSession.Session.GameSessionID}");
            PlayerProfile.Instance.PreservedSession.PreservedSession = latestSession.Session;
        }
    }

    // UpdateAvailableGamemodes will create the different game modes and attach OnShown and OnActivate handlers
    // to them. We add an extra action to the preserved session's OnShown to set the selection menu's shown
    // quest to null when the preserved session is shown. This gives the mutator menu the information it needs
    // to show and update the mutators list.
    // We also update the mutators selection menu, as the original OnShown delegate updates it with static data.
    private static void Postfix(GalaxyMapUIController __instance, MutatorsSelectionMenu ____mutatorsSelectionMenu)
    {
        Instance = __instance;
        FieldInfo gamemodeEntries = AccessTools.Field(typeof(GalaxyMapUIController), "_gamemodeEntries");
        IDictionary _gamemodeEntries = (IDictionary) gamemodeEntries.GetValue(__instance);
        object preservedEntry = _gamemodeEntries[GalaxyMapUIController.GamemodeType.Preserved];
        if (preservedEntry == null)
        {
            SaveSlotsPlugin.logger.LogInfo("Preserved game mode entry was null");
            return;
        }
        Type GameModeEntry = AccessTools.Inner(typeof(GalaxyMapUIController), "GameModeEntry");
        FieldInfo OnShownF = AccessTools.Field(GameModeEntry, "OnShown");
        Action OnShown = (Action) OnShownF.GetValue(preservedEntry);
        OnShown += delegate
        {
            ____mutatorsSelectionMenu.OnShownQuestChangedInGalaxyMap(null);
            UpdateJustTheMutatorsList();
        };
        OnShownF.SetValue(preservedEntry, OnShown);
    }
}

// Each time we update mutator states, we choose between two disjoint sets of SelectableVE elements. One is
// the mutators; the other is the list of saves. We will set them to visible or invisible depending on the
// current shownQuest. We will also hide or show the unseenCounter label and selected mutators list accordingly.
// Lastly, we will replace the text of the first label of the selected mutators dropdown.
[HarmonyPatch(typeof(MutatorsSelectionMenu))]
public static class Patch_MutatorsMenu
{
    public static Dictionary<string, Toggle> saves = new Dictionary<string, Toggle>();
    public static Label mutatorsDropdownLabel;
    public static string origText;

    // Helper to get a user-readable name for a save.
    public static string GetSaveText(PreservedSessionWithMetadata session)
    {
        DateTime savedTime = session.Timestamp.ToLocalTime();
        string timeText = $"{savedTime.ToShortDateString()} {savedTime.ToShortTimeString()}";
        string headerText = "SAVED SESSION";
        if (!ResourceAssetContainer<ShipLoadoutDataContainer, ShipLoadoutData, ShipLoadoutDataDef>.Instance.TryGetByGuid(session.Session.Ship, out var asset))
        {
            return (headerText + " " + timeText).ToUpper();
        }
        if ((bool)asset.ShipContextInfo && asset.ContextInfo != null)
        {
            headerText = asset.ShipContextInfo.HeaderText + ": " + asset.ContextInfo.HeaderText;
        }
        string saveText = (headerText + " " + timeText).ToUpper();
        if (saveText.StartsWith("METEM "))
        {
            saveText = saveText.Substring("METEM ".Length);
        }
        return saveText;
    }

    // Before running UpdateMutatorStates, set all mutator toggles to visible so that it starts from an expected
    // state (unless the shown quest is a preserved session).
    [HarmonyPrefix]
    [HarmonyPatch("UpdateMutatorStates")]
    private static void BeforeUpdate(QuestAsset ____shownQuest, Dictionary<GUIDUnion, Toggle> ____mutatorToggles)
    {
        if (____shownQuest == null)
        {
            return;
        }
        // When choosing a quest other than preserved, set all these mutators to shown. Hide saves.
        foreach (KeyValuePair<GUIDUnion, Toggle> mutatorToggle in ____mutatorToggles)
        {
            mutatorToggle.Value.parent.SetDisplay(true);
        }
        foreach (KeyValuePair<string, Toggle> saveToggle in saves)
        {
            saveToggle.Value.parent.SetDisplay(false);
        }
    }

    // Update these: toggle visibility, unseen counter visibility, selected mutators list visibility, label text.
    [HarmonyPostfix]
    [HarmonyPatch("UpdateMutatorStates")]
    private static void AfterUpdate(QuestAsset ____shownQuest, Dictionary<GUIDUnion, Toggle> ____mutatorToggles, Label ____mutatorsSelectedLabel, Label ____unseenCounter, MutatorsSelectionMenu __instance, MutatorsElementListVE ____selectedMutatorsList)
    {
        mutatorsDropdownLabel = (Label) ____mutatorsSelectedLabel.parent[0];

        if (____shownQuest != null)
        {
            // For normal quests, the label should have its original text.
            if (!string.IsNullOrEmpty(origText))
            {
                mutatorsDropdownLabel.text = origText;
                origText = null;
            }
            ____selectedMutatorsList.parent.SetDisplay(true);
            // Run CheckForUnseenMutators to set the visibility of the unseen counter appropriately.
            __instance.CheckForUnseenMutators();
            return;
        }

        // For a preserved quest, save the original text of the label.
        if (string.IsNullOrEmpty(origText))
        {
            origText = mutatorsDropdownLabel.text;
        }
        string labelText = "SAVED SESSION";
        if (PlayerProfile.Instance.PreservedSession.PreservedSession != null && SessionSaver.Sessions.ContainsKey(PlayerProfile.Instance.PreservedSession.PreservedSession.GameSessionID))
        {
            labelText = GetSaveText(SessionSaver.Sessions[PlayerProfile.Instance.PreservedSession.PreservedSession.GameSessionID]);
        }
        mutatorsDropdownLabel.text = labelText;  // Dropdown should show the selected save slot info.
        ____mutatorsSelectedLabel.text = "";  // Don't show the count of mutators selected.
        ____unseenCounter.SetDisplay(false);  // Don't show an indicator for unseen mutators.
        ____selectedMutatorsList.parent.SetDisplay(false);  // Don't show the selected mutators list.

        // When choosing a preserved quest, set all mutators to not shown. Show saves.
        foreach (KeyValuePair<GUIDUnion, Toggle> mutatorToggle in ____mutatorToggles)
        {
            mutatorToggle.Value.parent.SetDisplay(false);
        }
        foreach (KeyValuePair<string, Toggle> saveToggle in saves)
        {
            saveToggle.Value.parent.SetDisplay(true);
        }
    }

    // Regrettably, CheckForUnseenMutators is run after UpdateMutatorStates and can update the visibilty of the
    // unseen counter. Run AfterUpdate again to fix visibility for preserved quests.
    [HarmonyPostfix]
    [HarmonyPatch("CheckForUnseenMutators")]
    private static void ReUpdateMutatorStates(QuestAsset ____shownQuest, Dictionary<GUIDUnion, Toggle> ____mutatorToggles, Label ____mutatorsSelectedLabel, Label ____unseenCounter, MutatorsSelectionMenu __instance, MutatorsElementListVE ____selectedMutatorsList)
    {
        if (____shownQuest == null)
        {
            AfterUpdate(____shownQuest, ____mutatorToggles, ____mutatorsSelectedLabel, ____unseenCounter, __instance, ____selectedMutatorsList);
        }
    }

    // Same as above, except this one updates the selected mutators label text.
    [HarmonyPostfix]
    [HarmonyPatch("UpdateSelectedMutatorsCount")]
    private static void ReUpdateMutatorsCount(QuestAsset ____shownQuest, Dictionary<GUIDUnion, Toggle> ____mutatorToggles, Label ____mutatorsSelectedLabel, Label ____unseenCounter, MutatorsSelectionMenu __instance, MutatorsElementListVE ____selectedMutatorsList)
    {
        if (____shownQuest == null)
        {
            AfterUpdate(____shownQuest, ____mutatorToggles, ____mutatorsSelectedLabel, ____unseenCounter, __instance, ____selectedMutatorsList);
        }
    }

    // When filling the mutators list, also create a number of SelectableVE elements that represent save slots.
    // This happens once every time the lobby is loaded, so it clears out old ui elements each time.
    [HarmonyPrefix]
    [HarmonyPatch("FillMutatorsList")]
    private static void CreateSaveVisualElements(MutatorsSelectionMenu __instance, ScrollView ____mutatorsView, SelectableVE ____mutatorsMenuToggle)
    {
        foreach (KeyValuePair<string, Toggle> pair in saves)
        {
            pair.Value.parent.SetDisplay(false);
        }
        saves.Clear();

        // TODO: should these be radio buttons in a radio button group instead?
        foreach (KeyValuePair<string, PreservedSessionWithMetadata> pair in SessionSaver.Sessions)
        {
            DateTime savedTime = pair.Value.Timestamp.ToLocalTime();
            string labelText = GetSaveText(pair.Value);
            Toggle toggle = MakeSaveEntry(____mutatorsView.contentContainer, pair.Key, labelText, ____mutatorsMenuToggle);
            if (PlayerProfile.Instance.PreservedSession.PreservedSession != null && PlayerProfile.Instance.PreservedSession.PreservedSession.GameSessionID == pair.Key)
            {
                toggle.SetValueWithoutNotify(true);
            }
        }
    }

    private static Toggle MakeSaveEntry(VisualElement parent, string sessionID, string labelText, SelectableVE menuToggle)
    {
        SelectableVE selectableVE = new SelectableVE();
        selectableVE.name = sessionID;
        selectableVE.focusable = true;
        selectableVE.AddToClassList("mutator-entry");
        UIAudioProvider.AddSounds(selectableVE);
        parent.Add(selectableVE);
        VisualElement overlays = VEFactoryHelper.SetupVE<VisualElement>(selectableVE, "Overlays", new string[1] { "mutator-overlays" });
        VEFactoryHelper.SetupVE<VisualElement>(overlays, "UnseenMarker", new string[1] { "mutator-unseen-marker" });
        VEFactoryHelper.SetupVE<VisualElement>(overlays, "LockedIcon", new string[1] { "mutator-locked-icon" });
        VEFactoryHelper.SetupVE<VisualElement>(selectableVE, "MutatorIcon", new string[1] { "mutator-icon" }).style.backgroundImage = new StyleBackground(DataTable<UIHelperData>.Instance.SaveProgressContextInfo.Icon);
        Toggle toggle = new Toggle(labelText);
        UIAudioProvider.AddSounds(toggle);
        toggle.AddToClassList("metem-toggle");
        toggle.AddToClassList("mutator-toggle");
        toggle.RegisterValueChangedCallback(delegate(ChangeEvent<bool> evt)
        {
            if (evt.newValue)
            {
                if (!SessionSaver.Sessions.ContainsKey(sessionID) || string.IsNullOrEmpty(SessionSaver.Sessions[sessionID].Session.GameSessionID))
                {
                    SaveSlotsPlugin.logger.LogError($"Tried to select session not in saves: {sessionID}");
                    toggle.SetValueWithoutNotify(false);
                    return;
                }
                // Deselect everything else and replace the preserved session in the player profile.
                foreach (KeyValuePair<string, Toggle> pair in saves)
                {
                    if (pair.Key != sessionID)
                    {
                        pair.Value.SetValueWithoutNotify(false);
                    }
                }
                SaveSlotsPlugin.logger.LogInfo($"Setting profile preserved session to {sessionID}");
                PlayerProfile.Instance.PreservedSession.PreservedSession = SessionSaver.Sessions[sessionID].Session;
                // Update the dropdown text; update the session info; then close the dropdown.
                mutatorsDropdownLabel.text = toggle.label;
                Patch_UpdateAvailableGamemodes.UpdatePreservedSessionDisplay();
                menuToggle.Unselect();
                // TODO: make this work nicely if they have already selected the preserved quest
                // Might just need to make sure the UI gets updated?
                return;
            }
            else
            {
                string otherSelected = null;
                foreach (KeyValuePair<string, Toggle> pair in saves)
                {
                    if (pair.Key != sessionID && pair.Value.value)
                    {
                        otherSelected = pair.Key;
                        break;
                    }
                }
                if (otherSelected == null)
                {
                    SaveSlotsPlugin.logger.LogInfo("Tried to deselect the last session");
                    toggle.SetValueWithoutNotify(true);
                    return;
                }
                // If the user somehow has multiple sessions selected and deselects one, make sure one of the
                // remaining sessions is now the selected session.
                if (PlayerProfile.Instance.PreservedSession.PreservedSession == null || PlayerProfile.Instance.PreservedSession.PreservedSession.GameSessionID == sessionID)
                {
                    SaveSlotsPlugin.logger.LogInfo($"Deselected {sessionID}. Setting profile preserved session to {otherSelected}");
                    PlayerProfile.Instance.PreservedSession.PreservedSession = SessionSaver.Sessions[otherSelected].Session;
                    if (saves.ContainsKey(otherSelected))
                    {
                        mutatorsDropdownLabel.text = saves[otherSelected].label;
                        Patch_UpdateAvailableGamemodes.UpdatePreservedSessionDisplay();
                        menuToggle.Unselect();
                    }
                }
            }
        });
        selectableVE.Add(toggle);
        saves.Add(sessionID, toggle);
        return toggle;
    }
}
